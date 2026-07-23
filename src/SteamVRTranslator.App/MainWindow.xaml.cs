using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SteamVRTranslator.App.AndroidMirror;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.Input;
using SteamVRTranslator.App.Speech;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Subtitles;
using SteamVRTranslator.App.Translation;

namespace SteamVRTranslator.App;

public partial class MainWindow : Window
{
    private readonly ConfigurationStore _configurationStore = new();
    private readonly AppLog _log;
    private readonly HttpClient _providerHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly OpenAiCompatibleProviderClient _providerClient;
    private readonly SenseVoiceDownloadService _senseVoiceDownloads;
    private readonly SubtitleDiarizationDownloadService _subtitleModelDownloads;
    private readonly AndroidMirrorRuntimeService _androidMirrorRuntime = new();
    private readonly DesktopPushToTalkHotKey _desktopVoiceHotKey;
    private readonly DispatcherTimer _promptSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };
    private readonly DispatcherTimer _oscChunkIntervalSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private AppConfiguration _configuration;
    private List<TranslationProviderConfiguration> _providers = [];
    private readonly Dictionary<string, List<string>> _providerModelCatalog =
        new(StringComparer.OrdinalIgnoreCase);
    private ICollectionView? _providerModelView;
    private string _providerModelFilter = string.Empty;
    private SteamVrTranslationRuntime? _runtime;
    private long? _managerOverlayId;
    private long? _subtitleOverlayId;
    private SubtitleSessionController? _subtitleSession;
    private SubtitleHistoryWindow? _subtitleHistoryWindow;
    private ScrcpyAndroidSession? _androidMirrorSession;
    private AndroidMirrorWindow? _androidMirrorWindow;
    private long? _androidMirrorOverlayId;
    private bool _stoppingRuntime;
    private bool _startingRuntime;
    private bool _stoppingAndroidMirror;
    private bool _androidMirrorControlBusy;
    private CancellationTokenSource? _androidMirrorConfigurationDebounce;
    private CancellationTokenSource? _subtitleReplayCancellation;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _populatingProvider;
    private bool _updatingProviderModelText;
    private bool _updatingPromptControls;
    private bool _applyingVrControlPanelSettings;
    private bool _updatingExperimentalFeatures;
    private bool _promptSettingsDirty;
    private bool _capturingDesktopVoiceHotKey;
    private string _activePage = "capture";

    public MainWindow()
    {
        _log = new AppLog(AppContext.BaseDirectory);
        try
        {
            _configuration = _configurationStore.Load();
        }
        catch (Exception exception)
        {
            _configuration = new AppConfiguration
            {
                UiLanguage = ApplicationLanguages.DetectSystem()
            };
            _configuration.ApplyPromptLanguage();
            _log.Error("配置文件无法读取，已回退到默认配置。", exception);
        }

        AppLocalization.SetLanguage(_configuration.UiLanguage);
        InitializeComponent();
        _desktopVoiceHotKey = new DesktopPushToTalkHotKey();
        _desktopVoiceHotKey.PressedChanged += OnDesktopVoiceHotKeyPressedChanged;
        ProviderModelComboBox.AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(ProviderModelComboBox_TextChanged));
        _providerClient = new OpenAiCompatibleProviderClient(_providerHttpClient);
        _senseVoiceDownloads = new SenseVoiceDownloadService(AppContext.BaseDirectory);
        _subtitleModelDownloads = new SubtitleDiarizationDownloadService(AppContext.BaseDirectory);

        _log.MessageWritten += OnLogMessageWritten;
        _senseVoiceDownloads.ProgressChanged += OnSenseVoiceDownloadProgressChanged;
        _subtitleModelDownloads.ProgressChanged += OnSubtitleModelDownloadProgressChanged;
        _androidMirrorRuntime.ProgressChanged += OnAndroidMirrorDownloadProgressChanged;
        _promptSaveTimer.Tick += PromptSaveTimer_Tick;
        _oscChunkIntervalSaveTimer.Tick += OscChunkIntervalSaveTimer_Tick;
        PopulateControls();
        Loaded += OnLoaded;
        _log.Info($"控制窗口已启动。版本={typeof(MainWindow).Assembly.GetName().Version}");
        _log.Info($"程序目录：{AppContext.BaseDirectory}");
        _log.Info($"配置文件：{_configurationStore.FilePath}");
        _log.Info($"日志文件：{_log.FilePath}");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        FlushPendingPromptSave();
        FlushPendingOscChunkIntervalSave();
        if (_allowClose || _runtime is null)
        {
            _subtitleHistoryWindow?.ClosePermanently();
            _subtitleHistoryWindow = null;
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        IsEnabled = false;
        _ = StopThenCloseAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _promptSaveTimer.Stop();
        _promptSaveTimer.Tick -= PromptSaveTimer_Tick;
        _oscChunkIntervalSaveTimer.Stop();
        _oscChunkIntervalSaveTimer.Tick -= OscChunkIntervalSaveTimer_Tick;
        _subtitleReplayCancellation?.Cancel();
        _subtitleReplayCancellation?.Dispose();
        _androidMirrorConfigurationDebounce?.Cancel();
        _androidMirrorConfigurationDebounce?.Dispose();
        _subtitleSession?.Dispose();
        _desktopVoiceHotKey.PressedChanged -= OnDesktopVoiceHotKeyPressedChanged;
        _desktopVoiceHotKey.Dispose();
        _providerHttpClient.Dispose();
        _subtitleModelDownloads.ProgressChanged -= OnSubtitleModelDownloadProgressChanged;
        _subtitleModelDownloads.Dispose();
        _androidMirrorRuntime.ProgressChanged -= OnAndroidMirrorDownloadProgressChanged;
        _androidMirrorRuntime.Dispose();
        _senseVoiceDownloads.ProgressChanged -= OnSenseVoiceDownloadProgressChanged;
        _senseVoiceDownloads.Dispose();
        _log.MessageWritten -= OnLogMessageWritten;
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetActivePage("capture");
        UpdateAsrInstallStatus();
        UpdateSubtitleModelStatus();
        UpdateAndroidMirrorRuntimeStatus();
        UpdateExperimentalFeatureUi();
        if (_configuration.AndroidMirror.Enabled)
        {
            await RefreshAndroidDevicesAsync();
        }
        _log.Info("主窗口已完成加载。");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_startingRuntime)
        {
            return;
        }

        try
        {
            if (_runtime is not null)
            {
                await StopRuntimeAsync();
                return;
            }

            _startingRuntime = true;
            StartButton.IsEnabled = false;
            if (!await EnsureSenseVoiceInstalledForStartAsync())
            {
                return;
            }

            _configuration = ReadControls();
            await ValidateEnabledExperimentalFeaturesForStartAsync();
            _subtitleSession?.ApplyConfiguration(_configuration);
            _configurationStore.Save(_configuration);
            _log.Info(
                $"启动请求：捕获源=SteamVR Compositor 单眼纹理，" +
                $"Provider={DescribeActiveProvider(_configuration.Translation)}，" +
                $"VRChat语音={_configuration.VrChatVoiceInput.Enabled}，" +
                $"截图目录={ResolveCaptureDirectory(_configuration)}");
            _runtime = new SteamVrTranslationRuntime(_configuration, _log);
            _runtime.StatusChanged += OnRuntimeStatusChanged;
            _runtime.ControlPanelStateChanged += OnControlPanelStateChanged;
            _runtime.SubtitlePanelRequested += OnSubtitlePanelRequested;
            _runtime.SubtitleControlRequested += OnSubtitleControlRequested;
            _runtime.AndroidMirrorControlRequested += OnAndroidMirrorControlRequested;
            _desktopVoiceHotKey.Apply(
                _configuration.VrChatVoiceInput.Enabled &&
                _configuration.VrChatVoiceInput.DesktopHotKeyEnabled,
                _configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey);
            await _runtime.StartAsync();
            PushAndroidMirrorControlState();
            PushSubtitleControlState();
            StartButtonText.Text = T("Action.StopService");
            StartButtonIcon.Data = MaterialIconPaths.Stop;
            StartButton.IsEnabled = true;
            TestSelectionButton.IsEnabled = true;
            BindingsButton.IsEnabled = true;
            WpfOverlayButton.IsEnabled = true;
            OpenSubtitleHistoryVrButton.IsEnabled = true;
            SetConfigurationControlsEnabled(false);
            UpdateAndroidMirrorStartAvailability();
        }
        catch (Exception exception)
        {
            _log.Error("启动失败。", exception);
            MessageBox.Show(this, exception.Message, T("Dialog.StartFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _startingRuntime = false;
            if (_runtime is null)
            {
                StartButton.IsEnabled = true;
                SetConfigurationControlsEnabled(true);
            }
        }
    }

    private void TestSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            MessageBox.Show(this, T("Dialog.NotStarted.Message"), T("Dialog.NotStarted.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _runtime.RequestToggle("控制窗口模拟左摇杆");
    }

    private void BindingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            MessageBox.Show(this, T("Dialog.NotConnected.Message"), T("Dialog.NotConnected.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _runtime.RequestOpenBindings();
    }

    private async void WpfOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        WpfOverlayButton.IsEnabled = false;
        try
        {
            if (_managerOverlayId is { } overlayId)
            {
                await _runtime.CloseWindowAsync(overlayId);
                _managerOverlayId = null;
                WpfOverlayButtonText.Text = T("Action.ShowManagerVr");
                return;
            }

            _managerOverlayId = await _runtime.ShowWindowAsync(
                this,
                new WpfSpatialOverlayOptions
                {
                    Name = "SteamVR Translator Manager",
                    WidthMeters = 0.92f,
                    DistanceMeters = 0.8f,
                    CanGrab = true,
                    MaximumFramesPerSecond = 60
                });
            WpfOverlayButtonText.Text = T("Action.HideManagerVr");
        }
        catch (Exception exception)
        {
            _log.Error("切换 WPF 管理器空间窗口失败。", exception);
            MessageBox.Show(this, exception.Message, T("Dialog.SpatialWindowFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            WpfOverlayButton.IsEnabled = _runtime is not null;
        }
    }

    private async void RefreshAndroidDevicesButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAndroidDevicesAsync();

    private async Task RefreshAndroidDevicesAsync()
    {
        _androidMirrorControlBusy = true;
        PushAndroidMirrorControlState();
        RefreshAndroidDevicesButton.IsEnabled = false;
        try
        {
            var adbPath = _androidMirrorRuntime.ResolveAvailableAdbPath();
            if (adbPath is null)
            {
                AndroidDeviceComboBox.ItemsSource = null;
                AndroidMirrorStatusText.Text = T("AndroidMirror.Status.NoAdb");
                return;
            }

            var devices = await new AdbClient(adbPath).GetDevicesAsync();
            AndroidDeviceComboBox.ItemsSource = devices;
            AndroidDeviceComboBox.SelectedItem = devices.FirstOrDefault(device =>
                    string.Equals(
                        device.Serial,
                        _configuration.AndroidMirror.DeviceSerial,
                        StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(device => device.IsOnline)
                ?? devices.FirstOrDefault();
            var onlineCount = devices.Count(device => device.IsOnline);
            AndroidMirrorStatusText.Text = onlineCount == 0
                ? T("AndroidMirror.Status.NoDevices")
                : AppLocalization.Format("AndroidMirror.Status.DeviceCount", onlineCount);
        }
        catch (Exception exception)
        {
            _log.Error("[android-mirror] 刷新 ADB 设备失败。", exception);
            AndroidMirrorStatusText.Text = exception.Message;
        }
        finally
        {
            _androidMirrorControlBusy = false;
            RefreshAndroidDevicesButton.IsEnabled = _androidMirrorSession is null;
            UpdateAndroidMirrorStartAvailability();
            PushAndroidMirrorControlState();
        }
    }

    private async void DownloadAndroidMirrorRuntimeButton_Click(object sender, RoutedEventArgs e)
        => await DownloadAndroidMirrorRuntimeAsync(showDialog: true);

    private async Task DownloadAndroidMirrorRuntimeAsync(bool showDialog)
    {
        _androidMirrorControlBusy = true;
        PushAndroidMirrorControlState();
        DownloadAndroidMirrorRuntimeButton.IsEnabled = false;
        DownloadPanel.Visibility = Visibility.Visible;
        AndroidMirrorStatusText.Text = T("AndroidMirror.Status.Downloading");
        try
        {
            await _androidMirrorRuntime.DownloadAsync();
            UpdateAndroidMirrorRuntimeStatus();
            await RefreshAndroidDevicesAsync();
            _log.Info("[android-mirror] scrcpy 与 FFmpeg D3D11VA 运行时已下载并校验。");
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = T("Download.Cancelled");
            _log.Info("[android-mirror] 手机镜像运行时下载已取消。");
        }
        catch (Exception exception)
        {
            _log.Error("[android-mirror] 手机镜像运行时下载失败。", exception);
            AndroidMirrorStatusText.Text = exception.Message;
            if (showDialog)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    T("Dialog.DownloadFailed"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _androidMirrorControlBusy = false;
            DownloadAndroidMirrorRuntimeButton.IsEnabled =
                _androidMirrorSession is null &&
                (!_androidMirrorRuntime.IsInstalled ||
                 !_androidMirrorRuntime.IsHardwareDecoderInstalled);
            await Task.Delay(500);
            DownloadPanel.Visibility = Visibility.Collapsed;
            UpdateAndroidMirrorStartAvailability();
            PushAndroidMirrorControlState();
        }
    }

    private async void StartStopAndroidMirrorButton_Click(object sender, RoutedEventArgs e)
        => await ToggleAndroidMirrorAsync(showDialog: true);

    private async Task ToggleAndroidMirrorAsync(bool showDialog)
    {
        if (_androidMirrorSession is not null)
        {
            await StopAndroidMirrorAsync(closeOverlay: true);
            return;
        }

        if (!_configuration.AndroidMirror.Enabled)
        {
            AndroidMirrorStatusText.Text = T("AndroidMirror.Status.Disabled");
            return;
        }

        if (_runtime is null)
        {
            AndroidMirrorStatusText.Text = T("AndroidMirror.Status.RequiresSteamVr");
            return;
        }
        if (!_androidMirrorRuntime.IsInstalled ||
            AndroidDeviceComboBox.SelectedItem is not AndroidDeviceInfo { IsOnline: true } device)
        {
            UpdateAndroidMirrorStartAvailability();
            return;
        }

        StartStopAndroidMirrorButton.IsEnabled = false;
        _androidMirrorControlBusy = true;
        PushAndroidMirrorControlState();
        var configuration = ReadAndroidMirrorControls();
        var decoder = AndroidVideoDecoders.Normalize(configuration.VideoDecoder);
        if (decoder == AndroidVideoDecoders.D3D11 &&
            !_androidMirrorRuntime.IsHardwareDecoderInstalled)
        {
            AndroidMirrorStatusText.Text = T("AndroidMirror.Status.HardwareRuntimeMissing");
            UpdateAndroidMirrorStartAvailability();
            return;
        }
        var decodeMode = decoder == AndroidVideoDecoders.D3D11
            ? AndroidVideoDecodeMode.D3D11Hardware
            : AndroidVideoDecodeMode.Software;
        var session = new ScrcpyAndroidSession(
            _androidMirrorRuntime,
            _log,
            decodeMode,
            _androidMirrorRuntime.DecoderRuntimeDirectory);
        var window = new AndroidMirrorWindow(session);
        session.StatusChanged += OnAndroidMirrorSessionStatusChanged;
        window.Closed += AndroidMirrorWindow_Closed;
        _androidMirrorSession = session;
        _androidMirrorWindow = window;
        try
        {
            _configuration.AndroidMirror = configuration;
            SaveConfigurationSafely("Android 手机镜像设置");
            await session.StartAsync(device, configuration);
            var firstFrame = await window.WaitForFirstFrameAsync(
                TimeSpan.FromSeconds(15),
                CancellationToken.None);
            window.ConfigureForSource(
                firstFrame.Width,
                firstFrame.Height,
                configuration.WindowWidthMeters);
            _androidMirrorOverlayId = await _runtime.ShowWindowAsync(
                window,
                new WpfSpatialOverlayOptions
                {
                    Name = "SteamVR Translator Android Mirror",
                    WidthMeters = (float)window.RecommendedWidthMeters,
                    DistanceMeters = 0.62f,
                    Placement = WpfSpatialOverlayPlacement.LeftHand,
                    CanGrab = true,
                    ShowToolbarWhenGrabbed = true,
                    CloseWindowOnOverlayRemoval = true,
                    MaximumFramesPerSecond = configuration.MaximumFramesPerSecond
                });
            AndroidMirrorStartStopText.Text = T("AndroidMirror.Stop");
            AndroidMirrorStartStopIcon.Data = MaterialIconPaths.Stop;
            AndroidMirrorStatusText.Text = T("AndroidMirror.Status.Running");
            _log.Info(
                $"[android-mirror] VR 窗口已创建：overlay={_androidMirrorOverlayId}，" +
                $"设备={device.Serial}，画面={firstFrame.Width}x{firstFrame.Height}，" +
                $"物理尺寸约={window.RecommendedWidthMeters * 100:F1}x" +
                $"{window.RecommendedHeightMeters * 100:F1}cm。" );
        }
        catch (Exception exception)
        {
            _log.Error("[android-mirror] 启动手机镜像失败。", exception);
            AndroidMirrorStatusText.Text = exception.Message;
            await StopAndroidMirrorAsync(closeOverlay: true);
            if (showDialog)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    T("Dialog.SpatialWindowFailed"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _androidMirrorControlBusy = false;
            SetAndroidMirrorControlsEnabled(_androidMirrorSession is null);
            UpdateAndroidMirrorStartAvailability();
            PushAndroidMirrorControlState();
        }
    }

    private void OnAndroidMirrorSessionStatusChanged(object? sender, string message) =>
        Dispatcher.BeginInvoke(() =>
        {
            AndroidMirrorStatusText.Text = message;
            PushAndroidMirrorControlState();
        });

    private async void AndroidMirrorWindow_Closed(object? sender, EventArgs e) =>
        await StopAndroidMirrorAsync(closeOverlay: false);

    private async Task StopAndroidMirrorAsync(
        bool closeOverlay,
        SteamVrTranslationRuntime? owningRuntime = null)
    {
        if (_stoppingAndroidMirror ||
            (_androidMirrorSession is null && _androidMirrorOverlayId is null))
        {
            return;
        }

        _stoppingAndroidMirror = true;
        var session = _androidMirrorSession;
        var window = _androidMirrorWindow;
        var overlayId = _androidMirrorOverlayId;
        _androidMirrorSession = null;
        _androidMirrorWindow = null;
        _androidMirrorOverlayId = null;
        if (session is not null)
        {
            session.StatusChanged -= OnAndroidMirrorSessionStatusChanged;
        }
        if (window is not null)
        {
            window.Closed -= AndroidMirrorWindow_Closed;
        }

        if (closeOverlay && (owningRuntime ?? _runtime) is { } runtime && overlayId is { } id)
        {
            try
            {
                await runtime.CloseWindowAsync(id);
            }
            catch (OperationCanceledException)
            {
                _log.Info("[android-mirror] SteamVR 已停止，跳过镜像叠加层关闭确认。");
            }
            catch (Exception exception)
            {
                _log.Error("[android-mirror] 关闭手机镜像叠加层失败，将继续释放会话。", exception);
            }
        }

        if (session is not null)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception exception)
            {
                _log.Error("[android-mirror] 释放手机镜像会话失败。", exception);
            }
        }
        if (window is { IsLoaded: true })
        {
            try
            {
                window.Close();
            }
            catch (Exception exception)
            {
                _log.Error("[android-mirror] 关闭手机镜像窗口失败。", exception);
            }
        }

        _stoppingAndroidMirror = false;
        AndroidMirrorStartStopText.Text = T("AndroidMirror.Start");
        AndroidMirrorStartStopIcon.Data = MaterialIconPaths.Play;
        AndroidMirrorStatusText.Text = T("AndroidMirror.Status.Ready");
        SetAndroidMirrorControlsEnabled(true);
        UpdateAndroidMirrorStartAvailability();
        PushAndroidMirrorControlState();
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _log.DirectoryPath,
            UseShellExecute = true
        });
    }

    private void NavigateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
        {
            SetActivePage(page);
        }
    }

    private void SetActivePage(string page)
    {
        _activePage = page;
        CapturePage.Visibility = page == "capture" ? Visibility.Visible : Visibility.Collapsed;
        ProvidersPage.Visibility = page == "providers" ? Visibility.Visible : Visibility.Collapsed;
        PromptsPage.Visibility = page == "prompts" ? Visibility.Visible : Visibility.Collapsed;
        VoicePage.Visibility = page == "voice" ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesPage.Visibility = page == "subtitles" ? Visibility.Visible : Visibility.Collapsed;
        AndroidMirrorPage.Visibility = page == "android-mirror" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;

        var buttons = new[]
        {
            CaptureNavButton,
            ProviderNavButton,
            PromptsNavButton,
            VoiceNavButton,
            SubtitlesNavButton,
            AndroidMirrorNavButton,
            DiagnosticsNavButton
        };
        foreach (var button in buttons)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = new SolidColorBrush(Color.FromRgb(223, 227, 234));
        }

        var active = page switch
        {
            "providers" => ProviderNavButton,
            "prompts" => PromptsNavButton,
            "voice" => VoiceNavButton,
            "subtitles" => SubtitlesNavButton,
            "android-mirror" => AndroidMirrorNavButton,
            "diagnostics" => DiagnosticsNavButton,
            _ => CaptureNavButton
        };
        active.Background = new SolidColorBrush(Color.FromRgb(61, 115, 230));
        active.Foreground = Brushes.White;

        (PageTitleText.Text, PageContextText.Text) = page switch
        {
            "providers" => (T("Page.Providers.Title"), T("Page.Providers.Context")),
            "prompts" => (T("Page.Prompts.Title"), T("Page.Prompts.Context")),
            "voice" => (T("Page.Voice.Title"), T("Page.Voice.Context")),
            "subtitles" => (T("Page.Subtitles.Title"), T("Page.Subtitles.Context")),
            "android-mirror" => (T("Page.AndroidMirror.Title"), T("Page.AndroidMirror.Context")),
            "diagnostics" => (T("Page.Diagnostics.Title"), T("Page.Diagnostics.Context")),
            _ => (T("Page.Capture.Title"), T("Page.Capture.Context"))
        };
    }

    private void UiLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || UiLanguageComboBox.SelectedItem is null)
        {
            return;
        }

        var language = ApplicationLanguages.Normalize(SelectedTag(UiLanguageComboBox));
        if (string.Equals(language, _configuration.UiLanguage, StringComparison.Ordinal))
        {
            return;
        }

        FlushPendingPromptSave();
        UpdateSelectedProviderFromControls();
        _configuration.UiLanguage = language;
        _configuration.ApplyPromptLanguage();
        _configuration.Speech.EffectiveRecognitionLanguage = SpeechRecognitionLanguages.Resolve(
            _configuration.Speech.RecognitionLanguage,
            language);
        AppLocalization.SetLanguage(language);
        PopulatePromptControls();
        RefreshLocalizedDynamicText();

        var translation = CreateTranslationSnapshot(
            _configuration.Translation.Providers,
            _configuration.Translation.ActiveProviderId,
            SelectedTag(TargetLanguageComboBox));
        ApplyRuntimeTranslationSettings(translation, "interface language changed");
        SaveConfigurationSafely("interface language changed");
    }

    private void AsrLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || AsrLanguageComboBox.SelectedItem is null)
        {
            return;
        }

        _configuration.Speech.RecognitionLanguage = SpeechRecognitionLanguages.Normalize(
            SelectedTag(AsrLanguageComboBox));
        SaveConfigurationSafely("speech recognition language changed");
    }

    private void RefreshLocalizedDynamicText()
    {
        SetActivePage(_activePage);
        ProviderListBox.Items.Refresh();
        PopulateSelectedProvider();
        UpdateActiveProviderSummary();
        if (MicrophoneComboBox.Items.Count > 0 && MicrophoneComboBox.Items[0] is ComboBoxItem defaultMicrophone)
        {
            defaultMicrophone.Content = T("Voice.Microphone.Default");
        }
        StartButtonText.Text = _runtime is null ? T("Action.StartService") : T("Action.StopService");
        WpfOverlayButtonText.Text = _managerOverlayId is null
            ? T("Action.ShowManagerVr")
            : T("Action.HideManagerVr");
        PromptSaveStatusText.Text = T("Prompt.Loaded");
        UpdateDesktopVoiceHotKeyButton();
        UpdateAsrInstallStatus();
    }

    private void AddProviderButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedProviderFromControls();
        var provider = new TranslationProviderConfiguration
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = T("Provider.NewName"),
            Type = TranslationProviderConfiguration.OpenAiCompatibleType,
            BaseUrl = "https://api.openai.com/v1"
        };
        _providers.Add(provider);
        RefreshProviderList(provider);
    }

    private void DeleteProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected)
        {
            return;
        }

        if (selected.IsMock)
        {
            MessageBox.Show(this, T("Dialog.MockCannotDelete"), T("Dialog.CannotDelete"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var index = _providers.IndexOf(selected);
        _providers.Remove(selected);
        _providerModelCatalog.Remove(selected.Id);
        if (string.Equals(_configuration.Translation.ActiveProviderId, selected.Id, StringComparison.OrdinalIgnoreCase))
        {
            _configuration.Translation.ActiveProviderId = _providers[Math.Min(index, _providers.Count - 1)].Id;
        }

        RefreshProviderList(_providers[Math.Min(index, _providers.Count - 1)]);
    }

    private void ProviderActiveCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected)
        {
            return;
        }

        var selectedIsActive = string.Equals(
            selected.Id,
            _configuration.Translation.ActiveProviderId,
            StringComparison.OrdinalIgnoreCase);
        if (ProviderActiveCheckBox.IsChecked == true)
        {
            TryActivateProvider(selected);
            return;
        }

        if (!selectedIsActive)
        {
            UpdateProviderActiveState();
            return;
        }

        if (selected.IsMock)
        {
            // The built-in Mock provider is the required fallback and cannot be disabled.
            UpdateProviderActiveState();
            return;
        }

        var mock = _providers.First(provider => provider.IsMock);
        TryActivateProvider(mock);
    }

    private bool TryActivateProvider(TranslationProviderConfiguration provider)
    {
        UpdateSelectedProviderFromControls();
        var translation = CreateTranslationSnapshot(
            _providers,
            provider.Id,
            SelectedTag(TargetLanguageComboBox));
        try
        {
            ValidateActiveProvider(translation);
            ApplyRuntimeTranslationSettings(translation, $"启用 Provider {provider.DisplayName}");
            SaveConfigurationSafely($"启用 Provider {provider.DisplayName}");
        }
        catch (Exception exception)
        {
            ProviderConnectionText.Text = exception.Message;
            ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(177, 47, 52));
            _log.Error($"[configuration] 无法启用 Provider：{provider.DisplayName}。", exception);
            UpdateProviderActiveState();
            return false;
        }

        ProviderListBox.Items.Refresh();
        UpdateProviderActiveState();
        PopulatePromptProviderControls();
        if (!provider.IsMock)
        {
            ProviderConnectionText.Text = AppLocalization.Format("Provider.Connection.Enabled", provider.DisplayName);
            ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(8, 126, 114));
        }
        UpdateActiveProviderSummary();
        return true;
    }

    private void LivePreference_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            var translation = CreateTranslationSnapshot(
                _configuration.Translation.Providers,
                _configuration.Translation.ActiveProviderId,
                SelectedTag(TargetLanguageComboBox));
            ApplyRuntimeTranslationSettings(translation, "更新可热应用设置");
            _configuration.VrChatVoiceInput.DownloadSource =
                HfMirrorCheckBox.IsChecked == true ? "hf-mirror" : "official";
            SaveConfigurationSafely("可热应用设置");
        }
        catch (Exception exception)
        {
            _log.Error("[configuration] 应用运行时设置失败。", exception);
            ShowConfigurationError(exception.Message);
        }
    }

    private void OscChunkIntervalTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _oscChunkIntervalSaveTimer.Stop();
        _oscChunkIntervalSaveTimer.Start();
    }

    private void OscChunkIntervalSaveTimer_Tick(object? sender, EventArgs e)
    {
        _oscChunkIntervalSaveTimer.Stop();
        SaveOscChunkInterval("OSC 超长文本分段间隔");
    }

    private void FlushPendingOscChunkIntervalSave()
    {
        if (!_oscChunkIntervalSaveTimer.IsEnabled)
        {
            return;
        }

        _oscChunkIntervalSaveTimer.Stop();
        SaveOscChunkInterval("关闭前保存 OSC 分段间隔");
    }

    private void SaveOscChunkInterval(string reason)
    {
        try
        {
            var milliseconds = ReadOscChunkIntervalMilliseconds();
            if (_configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds == milliseconds)
            {
                return;
            }

            _configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds = milliseconds;
            _runtime?.ApplyOscStreamingChunkInterval(milliseconds);
            SaveConfigurationSafely(reason);
        }
        catch (Exception exception)
        {
            _log.Error("[configuration] OSC 分段间隔保存失败。", exception);
            ShowConfigurationError(exception.Message);
        }
    }

    private int ReadOscChunkIntervalMilliseconds()
    {
        if (!int.TryParse(OscChunkIntervalTextBox.Text, out var milliseconds) ||
            milliseconds is
                < VrChatVoiceInputConfiguration.MinimumStreamingChunkIntervalMilliseconds or
                > VrChatVoiceInputConfiguration.MaximumStreamingChunkIntervalMilliseconds)
        {
            throw new InvalidOperationException(
                AppLocalization.Format(
                    "Validation.OscInterval",
                    VrChatVoiceInputConfiguration.MinimumStreamingChunkIntervalMilliseconds,
                    VrChatVoiceInputConfiguration.MaximumStreamingChunkIntervalMilliseconds));
        }

        return milliseconds;
    }

    private void PromptEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _updatingPromptControls)
        {
            return;
        }

        _promptSettingsDirty = true;
        PromptSaveStatusText.Text = T("Prompt.Waiting");
        PromptSaveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
        _promptSaveTimer.Stop();
        _promptSaveTimer.Start();
    }

    private void PromptProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _updatingPromptControls)
        {
            return;
        }

        _promptSettingsDirty = true;
        PromptSaveStatusText.Text = T("Prompt.Waiting");
        PromptSaveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
        _promptSaveTimer.Stop();
        _promptSaveTimer.Start();
    }

    private void PromptSaveTimer_Tick(object? sender, EventArgs e)
    {
        _promptSaveTimer.Stop();
        SavePromptSettings("编辑提示词");
    }

    private void ResetCurrentPromptButton_Click(object sender, RoutedEventArgs e)
    {
        _updatingPromptControls = true;
        try
        {
            var defaults = BuiltInPromptDefaults.Create(_configuration.UiLanguage);
            switch (PromptTabControl.SelectedIndex)
            {
                case 1:
                    LayoutSystemPromptTextBox.Text = defaults.LayoutSystemPrompt;
                    LayoutTranslationPromptTextBox.Text = defaults.LayoutTranslationPrompt;
                    break;
                case 2:
                    CustomCommandSystemPromptTextBox.Text = defaults.CustomCommandSystemPrompt;
                    CustomCommandPromptTextBox.Text = defaults.CustomCommandPrompt;
                    break;
                case 3:
                    VoiceTranslationSystemPromptTextBox.Text = defaults.VoiceTranslationSystemPrompt;
                    VoiceTranslationPromptTextBox.Text = defaults.VoiceTranslationPrompt;
                    break;
                case 4:
                    SubtitleTranslationSystemPromptTextBox.Text = defaults.SubtitleTranslationSystemPrompt;
                    SubtitleTranslationPromptTextBox.Text = defaults.SubtitleTranslationPrompt;
                    break;
                default:
                    MarkdownSystemPromptTextBox.Text = defaults.MarkdownSystemPrompt;
                    MarkdownTranslationPromptTextBox.Text = defaults.MarkdownTranslationPrompt;
                    break;
            }
        }
        finally
        {
            _updatingPromptControls = false;
        }

        _promptSettingsDirty = true;
        SavePromptSettings("恢复内置提示词默认值");
    }

    private void SavePromptSettings(string reason)
    {
        if (!_promptSettingsDirty)
        {
            return;
        }

        try
        {
            CopyPromptEditorsTo(_configuration.GetPromptSet());
            _configuration.ApplyPromptLanguage();
            var translation = CreateTranslationSnapshot(
                _configuration.Translation.Providers,
                _configuration.Translation.ActiveProviderId,
                SelectedTag(TargetLanguageComboBox));
            CopyPromptEditorsTo(translation);
            ApplyRuntimeTranslationSettings(translation, reason);
            var saved = SaveConfigurationSafely(reason);
            _promptSettingsDirty = !saved;
            PromptSaveStatusText.Text = saved ? T("Prompt.Saved") : T("Prompt.SaveFailed");
            PromptSaveStatusText.Foreground = saved
                ? new SolidColorBrush(Color.FromRgb(8, 126, 114))
                : new SolidColorBrush(Color.FromRgb(177, 47, 52));
        }
        catch (Exception exception)
        {
            _log.Error("[configuration] 更新提示词失败。", exception);
            PromptSaveStatusText.Text = AppLocalization.Format("Prompt.NotSaved", exception.Message);
            PromptSaveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(177, 47, 52));
            ShowConfigurationError(exception.Message);
        }
    }

    private void FlushPendingPromptSave()
    {
        _promptSaveTimer.Stop();
        if (_promptSettingsDirty && IsLoaded)
        {
            SavePromptSettings("关闭前保存提示词");
        }
    }

    private void ProviderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateSelectedProvider();
    }

    private void ProviderField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_populatingProvider)
        {
            return;
        }

        UpdateSelectedProviderFromControls();
        ProviderListBox.Items.Refresh();
        UpdateActiveProviderSummary();
        ProviderConnectionText.Text = T("Provider.Connection.Changed");
        ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
    }

    private void ProviderNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_populatingProvider ||
            ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected)
        {
            return;
        }

        selected.Name = ProviderNameTextBox.Text.Trim();
        ProviderListBox.Items.Refresh();
        UpdateActiveProviderSummary();
    }

    private void ProviderField_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingProvider)
        {
            return;
        }

        UpdateSelectedProviderFromControls();
        ProviderConnectionText.Text = T("Provider.Connection.KeyChanged");
        ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
    }

    private void ProviderModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingProvider ||
            _updatingProviderModelText ||
            ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected ||
            selected.IsMock)
        {
            return;
        }

        if (ProviderModelComboBox.SelectedItem is not string model)
        {
            return;
        }

        _updatingProviderModelText = true;
        try
        {
            selected.Model = model;
            _providerModelFilter = string.Empty;
            _providerModelView?.Refresh();
            ProviderModelComboBox.Text = model;
        }
        finally
        {
            _updatingProviderModelText = false;
        }
        ProviderListBox.Items.Refresh();
        UpdateActiveProviderSummary();
    }

    private void ProviderModelComboBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_populatingProvider ||
            _updatingProviderModelText ||
            ProviderListBox.SelectedItem is not TranslationProviderConfiguration { IsMock: false })
        {
            return;
        }

        if (e.OriginalSource is not TextBox editor)
        {
            return;
        }

        var query = editor.Text;
        if (ProviderModelComboBox.SelectedItem is string selectedModel &&
            string.Equals(selectedModel, query, StringComparison.Ordinal))
        {
            return;
        }

        var selectionStart = editor.SelectionStart;
        var selectionLength = editor.SelectionLength;
        _updatingProviderModelText = true;
        try
        {
            // A filtered ICollectionView can move its current item. Keep that
            // movement from becoming a ComboBox selection and replacing the query.
            ProviderModelComboBox.SelectedItem = null;
            _providerModelFilter = query.Trim();
            _providerModelView?.Refresh();
            ProviderModelComboBox.Text = query;
            if (!string.Equals(editor.Text, query, StringComparison.Ordinal))
            {
                editor.Text = query;
            }

            var clampedStart = Math.Min(selectionStart, editor.Text.Length);
            var clampedLength = Math.Min(selectionLength, editor.Text.Length - clampedStart);
            editor.Select(clampedStart, clampedLength);
            ProviderModelComboBox.IsDropDownOpen = _providerModelView is { IsEmpty: false };
        }
        finally
        {
            _updatingProviderModelText = false;
        }
    }

    private void ProviderConcurrencyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _populatingProvider ||
            ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected)
        {
            return;
        }

        selected.MaxConcurrency = int.Parse(SelectedTag(ProviderConcurrencyComboBox));
        try
        {
            var translation = CreateTranslationSnapshot(
                _providers,
                _configuration.Translation.ActiveProviderId,
                SelectedTag(TargetLanguageComboBox));
            ApplyRuntimeTranslationSettings(
                translation,
                $"更新 Provider {selected.DisplayName} 最大并发");
            SaveConfigurationSafely($"Provider {selected.DisplayName} 最大并发");
            UpdateActiveProviderSummary();
        }
        catch (Exception exception)
        {
            _log.Error($"[configuration] 更新 Provider 并发失败：{selected.DisplayName}。", exception);
            ShowConfigurationError(exception.Message);
        }
    }

    private async void RefreshProviderButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSelectedProviderModelsAsync();
    }

    private async Task RefreshSelectedProviderModelsAsync()
    {
        if (ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected || selected.IsMock)
        {
            return;
        }

        UpdateSelectedProviderFromControls();
        RefreshProviderButton.IsEnabled = false;
        ProviderConnectionText.Text = T("Provider.Connection.Connecting");
        ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
        try
        {
            var models = await _providerClient.GetModelsAsync(selected.BaseUrl, selected.ApiKey, CancellationToken.None);
            var previousModel = selected.Model;
            _populatingProvider = true;
            _providerModelCatalog[selected.Id] = models.ToList();
            BindProviderModels(selected, models, previousModel, selectFirstWhenMissing: true);
            selected.Model = ProviderModelComboBox.SelectedItem?.ToString() ?? string.Empty;
            _populatingProvider = false;
            ProviderListBox.Items.Refresh();
            UpdateActiveProviderSummary();
            ProviderConnectionText.Text = AppLocalization.Format("Provider.Connection.Success", models.Count);
            ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(8, 126, 114));
            _log.Info($"[provider] 模型发现成功：BaseUrl={selected.BaseUrl}，数量={models.Count}，模型={selected.Model}");
        }
        catch (Exception exception)
        {
            _populatingProvider = false;
            ProviderConnectionText.Text = exception.Message;
            ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(177, 47, 52));
            _log.Error($"[provider] 模型发现失败：BaseUrl={selected.BaseUrl}", exception);
        }
        finally
        {
            RefreshProviderButton.IsEnabled = _runtime is null;
        }
    }

    private void AsrVariantComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateAsrInstallStatus();
        }
    }

    private void AsrEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || AsrEngineComboBox.SelectedItem is not AsrEngineOption selected)
        {
            return;
        }

        _log.Info(
            $"[configuration] SenseVoice 引擎选择：{selected.Backend}，" +
            $"设备={selected.DeviceIndex?.ToString() ?? "None"}/{selected.DeviceName ?? "CPU"}。");
    }

    private async void DownloadAsrButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallSenseVoiceBundleAsync(
            SelectedTag(AsrVariantComboBox),
            HfMirrorCheckBox.IsChecked == true);
    }

    private async Task<bool> EnsureSenseVoiceInstalledForStartAsync()
    {
        var selectedEngine = AsrEngineComboBox.SelectedItem as AsrEngineOption ?? AsrEngineOption.Cpu;
        var selectedConfiguration = new SpeechConfiguration
        {
            SenseVoiceExecutablePath = SenseVoiceExecutableTextBox.Text.Trim(),
            SenseVoiceVulkanExecutablePath = SenseVoiceVulkanExecutableTextBox.Text.Trim(),
            SenseVoiceBackend = selectedEngine.Backend,
            SenseVoiceModelPath = SenseVoiceModelTextBox.Text.Trim(),
            SenseVoiceVadModelPath = NullIfWhiteSpace(SenseVoiceVadTextBox.Text)
        };
        if (SenseVoiceInstallation.IsConfiguredInstallationAvailable(
                selectedConfiguration,
                AppContext.BaseDirectory))
        {
            return true;
        }

        var variant = SelectedTag(AsrVariantComboBox);
        var statuses = _senseVoiceDownloads.GetStatuses(variant);
        var missingAssets = statuses
            .Where(status => !status.Installed)
            .Select(LocalizeAsrAssetName)
            .ToList();
        if (missingAssets.Count == 0)
        {
            missingAssets.AddRange(GetMissingConfiguredSenseVoiceAssets(selectedConfiguration));
        }

        var modelDisplayName = (AsrVariantComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                               ?? variant;
        var dialog = new SenseVoiceSetupDialog(
            modelDisplayName,
            missingAssets,
            HfMirrorCheckBox.IsChecked == true)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            _log.Info("[startup] 用户暂不安装 SenseVoice，本次启动已取消。");
            return false;
        }

        HfMirrorCheckBox.IsChecked = dialog.UseMirror;
        _log.Info(
            $"[startup] 用户确认安装 SenseVoice：量化={variant}，" +
            $"下载源={(dialog.UseMirror ? "hf-mirror" : "official")}。");
        return await InstallSenseVoiceBundleAsync(variant, dialog.UseMirror);
    }

    private static IReadOnlyList<string> GetMissingConfiguredSenseVoiceAssets(
        SpeechConfiguration configuration)
    {
        var missing = new List<string>();
        var useVulkan = string.Equals(
            configuration.SenseVoiceBackend,
            "vulkan",
            StringComparison.OrdinalIgnoreCase);
        var runtime = useVulkan
            ? configuration.SenseVoiceVulkanExecutablePath
            : configuration.SenseVoiceExecutablePath;
        if (!SenseVoiceInstallation.FileExists(runtime, AppContext.BaseDirectory))
        {
            missing.Add(T(useVulkan ? "Validation.AsrVulkanRuntime" : "Validation.AsrCpuRuntime"));
        }

        if (!SenseVoiceInstallation.FileExists(
                configuration.SenseVoiceModelPath,
                AppContext.BaseDirectory))
        {
            missing.Add(T("Validation.AsrModel"));
        }

        if (!string.IsNullOrWhiteSpace(configuration.SenseVoiceVadModelPath) &&
            !SenseVoiceInstallation.FileExists(
                configuration.SenseVoiceVadModelPath,
                AppContext.BaseDirectory))
        {
            missing.Add(T("Validation.AsrVadModel"));
        }

        return missing;
    }

    private async Task<bool> InstallSenseVoiceBundleAsync(string variant, bool useMirror)
    {
        DownloadAsrButton.IsEnabled = false;
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = T("Download.Downloading");
        try
        {
            await _senseVoiceDownloads.DownloadBundleAsync(
                variant,
                useMirror);
            SenseVoiceExecutableTextBox.Text = "runtimes/llama-funasr-sensevoice.exe";
            SenseVoiceVulkanExecutableTextBox.Text =
                "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe";
            SenseVoiceModelTextBox.Text = variant == "q5_0"
                ? "models/sensevoice-small-q5_0.gguf"
                : "models/sensevoice-small-q8.gguf";
            SenseVoiceVadTextBox.Text = "models/fsmn-vad.gguf";
            UpdateAsrInstallStatus();
            _configuration.Speech.SenseVoiceExecutablePath = SenseVoiceExecutableTextBox.Text;
            _configuration.Speech.SenseVoiceVulkanExecutablePath =
                SenseVoiceVulkanExecutableTextBox.Text;
            CopySelectedAsrEngineTo(_configuration.Speech);
            _configuration.Speech.SenseVoiceModelPath = SenseVoiceModelTextBox.Text;
            _configuration.Speech.SenseVoiceVadModelPath = SenseVoiceVadTextBox.Text;
            _configuration.VrChatVoiceInput.DownloadSource =
                useMirror ? "hf-mirror" : "official";
            _configurationStore.Save(_configuration);
            _log.Info(
                $"[download] SenseVoice {variant} 的 CPU/Vulkan 运行时、模型和 VAD " +
                "已下载、启用并保存配置。");
            return true;
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = T("Download.Cancelled");
            _log.Info("[download] SenseVoice 下载已取消。");
            return false;
        }
        catch (Exception exception)
        {
            _log.Error("[download] SenseVoice 下载失败。", exception);
            MessageBox.Show(this, exception.Message, T("Dialog.DownloadFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            DownloadAsrButton.IsEnabled = _runtime is null && !_startingRuntime;
            await Task.Delay(500);
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e) =>
        CancelActiveDownloads();

    private void CancelActiveDownloads()
    {
        _senseVoiceDownloads.Cancel();
        _subtitleModelDownloads.Cancel();
        _androidMirrorRuntime.Cancel();
    }

    private void OnSenseVoiceDownloadProgressChanged(object? sender, SenseVoiceDownloadProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            DownloadPanel.Visibility = Visibility.Visible;
            DownloadStatusText.Text = progress.Message;
            DownloadProgressBar.Value = progress.TotalBytes == 0
                ? 0
                : progress.BytesDownloaded * 100d / progress.TotalBytes;
        });
    }

    private void OnSubtitleModelDownloadProgressChanged(
        object? sender,
        SubtitleModelDownloadProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            DownloadPanel.Visibility = Visibility.Visible;
            DownloadStatusText.Text = progress.State == "complete"
                ? T("Subtitle.Diarization.Downloaded")
                : AppLocalization.Format("Subtitle.Diarization.Downloading", progress.AssetName);
            DownloadProgressBar.Value = progress.TotalBytes == 0
                ? 0
                : progress.BytesDownloaded * 100d / progress.TotalBytes;
        });
    }

    private void OnAndroidMirrorDownloadProgressChanged(
        object? sender,
        AndroidMirrorDownloadProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            DownloadPanel.Visibility = Visibility.Visible;
            DownloadStatusText.Text = progress.TotalBytes > 0 &&
                                      progress.BytesDownloaded >= progress.TotalBytes
                ? T("AndroidMirror.Runtime.Installed")
                : T("AndroidMirror.Status.Downloading");
            DownloadProgressBar.Value = progress.TotalBytes == 0
                ? 0
                : progress.BytesDownloaded * 100d / progress.TotalBytes;
        });
    }

    private async Task StopThenCloseAsync()
    {
        try
        {
            await StopRuntimeAsync();
        }
        catch (Exception exception)
        {
            _log.Error("关闭 SteamVR 模块时发生错误。", exception);
        }
        finally
        {
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private async Task StopRuntimeAsync()
    {
        var runtime = _runtime;
        if (runtime is null || _stoppingRuntime)
        {
            return;
        }

        _stoppingRuntime = true;
        StartButton.IsEnabled = false;
        try
        {
            _log.Info("正在停止 SteamVR 模块。");
            _desktopVoiceHotKey.Apply(
                false,
                _configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey);
            runtime.SetDesktopVoiceInputPressed(false);
            if (_subtitleSession is not null)
            {
                await _subtitleSession.StopListeningAsync();
            }
            await StopAndroidMirrorAsync(closeOverlay: true, runtime);
            if (ReferenceEquals(_runtime, runtime))
            {
                _runtime = null;
            }
            runtime.StatusChanged -= OnRuntimeStatusChanged;
            runtime.ControlPanelStateChanged -= OnControlPanelStateChanged;
            runtime.SubtitlePanelRequested -= OnSubtitlePanelRequested;
            runtime.SubtitleControlRequested -= OnSubtitleControlRequested;
            runtime.AndroidMirrorControlRequested -= OnAndroidMirrorControlRequested;
            await runtime.DisposeAsync();
            StartButtonText.Text = T("Action.StartService");
            StartButtonIcon.Data = MaterialIconPaths.Play;
            StateText.Text = "IDLE";
            StatusText.Visibility = Visibility.Collapsed;
            RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(157, 165, 159));
            TestSelectionButton.IsEnabled = false;
            BindingsButton.IsEnabled = false;
            WpfOverlayButton.IsEnabled = false;
            OpenSubtitleHistoryVrButton.IsEnabled = false;
            WpfOverlayButtonText.Text = T("Action.ShowManagerVr");
            _managerOverlayId = null;
            _subtitleOverlayId = null;
            SetConfigurationControlsEnabled(true);
            UpdateAndroidMirrorStartAvailability();
            _log.Info("SteamVR 模块已停止。");
        }
        finally
        {
            _stoppingRuntime = false;
            StartButton.IsEnabled = true;
        }
    }

    private void OnRuntimeStatusChanged(object? sender, SteamVrRuntimeEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = e.Message;
            StatusText.Visibility = e.IsError ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Foreground = e.IsError
                ? new SolidColorBrush(Color.FromRgb(177, 47, 52))
                : new SolidColorBrush(Color.FromRgb(82, 96, 90));
            RuntimeDot.Fill = e.IsError
                ? new SolidColorBrush(Color.FromRgb(177, 47, 52))
                : new SolidColorBrush(Color.FromRgb(8, 126, 114));
            StateText.Text = e.State.ToString().ToUpperInvariant();
        });
    }

    private void OnControlPanelStateChanged(object? sender, VrControlPanelStateChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _applyingVrControlPanelSettings = true;
            try
            {
                var state = e.State;
                _configuration.VrChatVoiceInput.Enabled = state.VoiceEnabled;
                _configuration.VrChatVoiceInput.TranslationEnabled = state.TranslationEnabled;
                _configuration.VrChatVoiceInput.SendImmediately = state.SendImmediately;
                _configuration.VrChatVoiceInput.TranslationDisplayMode =
                    VoiceTranslationDisplayModes.Normalize(state.DisplayMode);
                _configuration.VrChatVoiceInput.TranslationTargetLanguage = state.TargetLanguage;
                _configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds =
                    state.ChunkIntervalMilliseconds;
                _configuration.StereoCompositionMode = NormalizeCaptureEye(state.CaptureEye);
                _configuration.ShowPointerRay = state.PointerRayEnabled;
                _configuration.PointerSmoothingStrength =
                    AppConfiguration.NormalizePointerSmoothingStrength(
                        state.PointerSmoothingStrength);

                VoiceInputEnabledCheckBox.IsChecked = state.VoiceEnabled;
                VoiceTranslationEnabledCheckBox.IsChecked = state.TranslationEnabled;
                OscSendImmediatelyCheckBox.IsChecked = state.SendImmediately;
                SelectByTag(VoiceTranslationDisplayModeComboBox, state.DisplayMode);
                SelectByTag(VoiceTranslationTargetLanguageComboBox, state.TargetLanguage);
                OscChunkIntervalTextBox.Text = state.ChunkIntervalMilliseconds.ToString();
                SelectByTag(StereoCompositionComboBox, _configuration.StereoCompositionMode);
                ShowPointerRayCheckBox.IsChecked = state.PointerRayEnabled;
                SelectByTag(
                    PointerSmoothingComboBox,
                    PointerSmoothingPreset(_configuration.PointerSmoothingStrength).ToString());
                SaveConfigurationSafely("VR 控制面板设置");
            }
            finally
            {
                _applyingVrControlPanelSettings = false;
            }
        });
    }

    private void OnAndroidMirrorControlRequested(
        object? sender,
        VrAndroidMirrorControlRequestEventArgs e)
    {
        if (e.Kind == VrAndroidMirrorControlRequestKind.ConfigurationChanged)
        {
            QueueAndroidMirrorConfiguration(e.State);
            return;
        }

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (!_configuration.AndroidMirror.Enabled)
                {
                    _log.Warning("[android-mirror] 已忽略未启用实验性功能的 VR 控制请求。");
                    return;
                }

                switch (e.Kind)
                {
                    case VrAndroidMirrorControlRequestKind.RefreshDevices:
                        await RefreshAndroidDevicesAsync();
                        break;
                    case VrAndroidMirrorControlRequestKind.ToggleConnection:
                        ApplyAndroidMirrorControlPanelConfiguration(e.State);
                        await ToggleAndroidMirrorAsync(showDialog: false);
                        break;
                    case VrAndroidMirrorControlRequestKind.DownloadRuntime:
                        await DownloadAndroidMirrorRuntimeAsync(showDialog: false);
                        break;
                }
            }
            catch (Exception exception)
            {
                _log.Error("[android-mirror] VR 控制面板操作失败。", exception);
                AndroidMirrorStatusText.Text = exception.Message;
                PushAndroidMirrorControlState();
            }
        }));
    }

    private void QueueAndroidMirrorConfiguration(VrAndroidMirrorControlState state)
    {
        _androidMirrorConfigurationDebounce?.Cancel();
        _androidMirrorConfigurationDebounce?.Dispose();
        var cancellation = new CancellationTokenSource();
        _androidMirrorConfigurationDebounce = cancellation;
        _ = ApplyAndroidMirrorConfigurationAfterVisualFeedbackAsync(state, cancellation);
    }

    private async Task ApplyAndroidMirrorConfigurationAfterVisualFeedbackAsync(
        VrAndroidMirrorControlState state,
        CancellationTokenSource cancellation)
    {
        try
        {
            // The WPF source can now submit the selected state to SteamVR before
            // configuration persistence and mirror reconfiguration run.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_configuration.AndroidMirror.Enabled)
                {
                    _log.Warning("[android-mirror] 已忽略未启用实验性功能的 VR 控制请求。");
                    return;
                }

                try
                {
                    ApplyAndroidMirrorControlPanelConfiguration(state);
                }
                catch (Exception exception)
                {
                    _log.Error("[android-mirror] VR 控制面板配置失败。", exception);
                    AndroidMirrorStatusText.Text = exception.Message;
                    PushAndroidMirrorControlState();
                }
            }, DispatcherPriority.Background, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_androidMirrorConfigurationDebounce, cancellation))
            {
                _androidMirrorConfigurationDebounce = null;
            }
            cancellation.Dispose();
        }
    }

    private void ApplyAndroidMirrorControlPanelConfiguration(VrAndroidMirrorControlState state)
    {
        var widthMeters = AndroidMirrorConfiguration.WindowWidthFromScale(state.WindowScale);
        if (_androidMirrorSession is not null)
        {
            _configuration.AndroidMirror.WindowWidthMeters = widthMeters;
            _androidMirrorWindow?.ApplyPhysicalScale(widthMeters);
            if (_runtime is not null &&
                _androidMirrorOverlayId is { } overlayId &&
                _androidMirrorWindow is { } window)
            {
                _runtime.ResizeWindow(overlayId, (float)window.RecommendedWidthMeters);
            }
            SaveConfigurationSafely("VR 控制面板手机镜像缩放");
            PushAndroidMirrorControlState();
            return;
        }

        if (AndroidDeviceComboBox.ItemsSource is IEnumerable<AndroidDeviceInfo> devices)
        {
            AndroidDeviceComboBox.SelectedItem = devices.FirstOrDefault(device => string.Equals(
                device.Serial,
                state.DeviceSerial,
                StringComparison.OrdinalIgnoreCase));
        }
        SelectByTag(
            AndroidMirrorMaxSizeComboBox,
            AndroidMirrorConfiguration.NormalizeMaximumSize(state.MaximumSize).ToString());
        SelectByTag(
            AndroidMirrorMaxFpsComboBox,
            AndroidMirrorConfiguration.NormalizeMaximumFramesPerSecond(
                state.MaximumFramesPerSecond).ToString());
        SelectByTag(
            AndroidMirrorBitRateComboBox,
            NormalizeAndroidMirrorBitRate(state.VideoBitRateMbps).ToString());
        _configuration.AndroidMirror.WindowWidthMeters = widthMeters;
        _configuration.AndroidMirror = ReadAndroidMirrorControls();
        SaveConfigurationSafely("VR 控制面板手机镜像设置");
        PushAndroidMirrorControlState();
    }

    private void PushAndroidMirrorControlState()
    {
        _runtime?.UpdateAndroidMirrorControlState(CurrentAndroidMirrorControlState());
    }

    private VrAndroidMirrorControlState CurrentAndroidMirrorControlState()
    {
        var devices = (AndroidDeviceComboBox.ItemsSource as IEnumerable<AndroidDeviceInfo>)?
            .Select(device => new VrAndroidMirrorDeviceOption(
                device.Serial,
                device.DisplayName,
                device.IsOnline))
            .ToArray() ?? [];
        var selectedSerial = (AndroidDeviceComboBox.SelectedItem as AndroidDeviceInfo)?.Serial
            ?? _configuration.AndroidMirror.DeviceSerial;
        var size = SelectedTag(AndroidMirrorMaxSizeComboBox);
        var fps = SelectedTag(AndroidMirrorMaxFpsComboBox);
        var bitRate = SelectedTag(AndroidMirrorBitRateComboBox);
        return new VrAndroidMirrorControlState(
            _androidMirrorRuntime.IsInstalled,
            devices,
            selectedSerial,
            AndroidMirrorConfiguration.NormalizeMaximumSize(
                int.TryParse(size, out var parsedSize)
                    ? parsedSize
                    : _configuration.AndroidMirror.MaximumSize),
            AndroidMirrorConfiguration.NormalizeMaximumFramesPerSecond(
                int.TryParse(fps, out var parsedFps)
                    ? parsedFps
                    : _configuration.AndroidMirror.MaximumFramesPerSecond),
            NormalizeAndroidMirrorBitRate(
                int.TryParse(bitRate, out var parsedBitRate)
                    ? parsedBitRate
                    : _configuration.AndroidMirror.VideoBitRateMbps),
            AndroidMirrorConfiguration.WindowScaleFromWidth(
                _configuration.AndroidMirror.WindowWidthMeters),
            _androidMirrorSession is not null,
            _androidMirrorControlBusy || _stoppingAndroidMirror,
            AndroidMirrorStatusText.Text ?? string.Empty);
    }

    private void OnLogMessageWritten(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogTextBox.AppendText(message + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void PopulateControls()
    {
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        SelectByTag(UiLanguageComboBox, _configuration.UiLanguage);
        SelectByTag(StereoCompositionComboBox, NormalizeCaptureEye(_configuration.StereoCompositionMode));
        InvertResultScrollCheckBox.IsChecked = _configuration.InvertResultScroll;
        ShowPointerRayCheckBox.IsChecked = _configuration.ShowPointerRay;
        SelectByTag(
            PointerSmoothingComboBox,
            PointerSmoothingPreset(_configuration.PointerSmoothingStrength).ToString());
        SelectByTag(TargetLanguageComboBox, _configuration.Translation.TargetLanguage);
        _providers = _configuration.Translation.Providers
            .Select(CloneProvider)
            .ToList();
        if (_providers.Count == 0)
        {
            _providers.Add(TranslationProviderConfiguration.CreateMock());
        }
        RefreshProviderList(_providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, _configuration.Translation.ActiveProviderId, StringComparison.OrdinalIgnoreCase)) ?? _providers[0]);
        PopulatePromptControls();

        PopulateMicrophones();
        PopulateAsrEngines();
        VoiceInputEnabledCheckBox.IsChecked = _configuration.VrChatVoiceInput.Enabled;
        DesktopVoiceHotKeyEnabledCheckBox.IsChecked =
            _configuration.VrChatVoiceInput.DesktopHotKeyEnabled;
        UpdateDesktopVoiceHotKeyButton();
        OscHostTextBox.Text = _configuration.VrChatVoiceInput.Host;
        OscPortTextBox.Text = _configuration.VrChatVoiceInput.Port.ToString();
        OscSendImmediatelyCheckBox.IsChecked = _configuration.VrChatVoiceInput.SendImmediately;
        OscChunkIntervalTextBox.Text =
            _configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds.ToString();
        VoiceTranslationEnabledCheckBox.IsChecked = _configuration.VrChatVoiceInput.TranslationEnabled;
        SelectByTag(
            VoiceTranslationTargetLanguageComboBox,
            _configuration.VrChatVoiceInput.TranslationTargetLanguage);
        SelectByTag(
            VoiceTranslationDisplayModeComboBox,
            VoiceTranslationDisplayModes.Normalize(_configuration.VrChatVoiceInput.TranslationDisplayMode));
        HfMirrorCheckBox.IsChecked = string.Equals(
            _configuration.VrChatVoiceInput.DownloadSource,
            "hf-mirror",
            StringComparison.OrdinalIgnoreCase);
        SenseVoiceExecutableTextBox.Text = _configuration.Speech.SenseVoiceExecutablePath;
        SenseVoiceVulkanExecutableTextBox.Text =
            _configuration.Speech.SenseVoiceVulkanExecutablePath;
        SenseVoiceModelTextBox.Text = _configuration.Speech.SenseVoiceModelPath;
        SenseVoiceVadTextBox.Text = _configuration.Speech.SenseVoiceVadModelPath ?? string.Empty;
        SelectByTag(AsrLanguageComboBox, _configuration.Speech.RecognitionLanguage);
        SelectByTag(
            AsrVariantComboBox,
            Path.GetFileName(_configuration.Speech.SenseVoiceModelPath).Contains("q5", StringComparison.OrdinalIgnoreCase)
                ? "q5_0"
                : "q8_0");
        SubtitleEnabledCheckBox.IsChecked = _configuration.Subtitles.Enabled;
        SelectByTag(SubtitleAsrBackendComboBox, _configuration.Subtitles.AsrBackend);
        VibeVoiceServiceUrlTextBox.Text = _configuration.Subtitles.VibeVoiceServiceUrl;
        VibeVoiceApiKeyPasswordBox.Password = _configuration.Subtitles.VibeVoiceApiKey;
        UpdateSubtitleAsrPanel();
        SubtitleShowOriginalCheckBox.IsChecked = _configuration.Subtitles.ShowOriginalText;
        SubtitleTranslateCheckBox.IsChecked = _configuration.Subtitles.TranslateText;
        SubtitleSpeakerColorsCheckBox.IsChecked = _configuration.Subtitles.UseSpeakerColors;
        SelectByTag(SubtitleTargetLanguageComboBox, _configuration.Subtitles.TargetLanguage);
        SubtitleHistoryEntriesTextBox.Text = _configuration.Subtitles.MaximumHistoryEntries.ToString();
        SubtitleHistoryCharactersTextBox.Text = _configuration.Subtitles.MaximumHistoryCharacters.ToString();
        SubtitleDiarizationEnabledCheckBox.IsChecked = _configuration.Subtitles.Diarization.Enabled;
        SelectByTag(
            SubtitleDiarizationThreadsComboBox,
            NormalizeSubtitleThreadSelection(_configuration.Subtitles.Diarization.CpuThreadCount));
        SubtitleDiarizationThresholdTextBox.Text =
            _configuration.Subtitles.Diarization.ClusteringThreshold.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        _configuration.AndroidMirror.MaximumSize =
            AndroidMirrorConfiguration.NormalizeMaximumSize(_configuration.AndroidMirror.MaximumSize);
        _configuration.AndroidMirror.MaximumFramesPerSecond =
            AndroidMirrorConfiguration.NormalizeMaximumFramesPerSecond(
                _configuration.AndroidMirror.MaximumFramesPerSecond);
        AndroidMirrorEnabledCheckBox.IsChecked = _configuration.AndroidMirror.Enabled;
        SelectByTag(AndroidMirrorMaxSizeComboBox, _configuration.AndroidMirror.MaximumSize.ToString());
        SelectByTag(
            AndroidMirrorMaxFpsComboBox,
            _configuration.AndroidMirror.MaximumFramesPerSecond.ToString());
        SelectByTag(
            AndroidMirrorBitRateComboBox,
            _configuration.AndroidMirror.VideoBitRateMbps.ToString());
        SelectByTag(
            AndroidMirrorDecoderComboBox,
            AndroidVideoDecoders.Normalize(_configuration.AndroidMirror.VideoDecoder));
        TestSelectionButton.IsEnabled = false;
        BindingsButton.IsEnabled = false;
    }

    private void PopulatePromptControls()
    {
        _updatingPromptControls = true;
        try
        {
            var prompts = _configuration.GetPromptSet();
            MarkdownSystemPromptTextBox.Text = prompts.MarkdownSystemPrompt;
            MarkdownTranslationPromptTextBox.Text = prompts.MarkdownTranslationPrompt;
            LayoutSystemPromptTextBox.Text = prompts.LayoutSystemPrompt;
            LayoutTranslationPromptTextBox.Text = prompts.LayoutTranslationPrompt;
            CustomCommandSystemPromptTextBox.Text = prompts.CustomCommandSystemPrompt;
            CustomCommandPromptTextBox.Text = prompts.CustomCommandPrompt;
        VoiceTranslationSystemPromptTextBox.Text = prompts.VoiceTranslationSystemPrompt;
        VoiceTranslationPromptTextBox.Text = prompts.VoiceTranslationPrompt;
        SubtitleTranslationSystemPromptTextBox.Text = prompts.SubtitleTranslationSystemPrompt;
        SubtitleTranslationPromptTextBox.Text = prompts.SubtitleTranslationPrompt;
            PopulatePromptProviderControls();
        }
        finally
        {
            _updatingPromptControls = false;
        }
    }

    private void PopulatePromptProviderControls()
    {
        if (DirectPromptProviderComboBox is null || _providers.Count == 0)
        {
            return;
        }

        var wasUpdating = _updatingPromptControls;
        _updatingPromptControls = true;
        try
        {
            PopulatePromptProviderComboBox(DirectPromptProviderComboBox, PromptProviderPurpose.DirectTranslation);
            PopulatePromptProviderComboBox(LayoutPromptProviderComboBox, PromptProviderPurpose.LayoutTranslation);
            PopulatePromptProviderComboBox(CustomPromptProviderComboBox, PromptProviderPurpose.CustomCommand);
            PopulatePromptProviderComboBox(VoicePromptProviderComboBox, PromptProviderPurpose.VoiceTranslation);
            PopulatePromptProviderComboBox(
                SubtitlePromptProviderComboBox,
                PromptProviderPurpose.SubtitleTranslation);
        }
        finally
        {
            _updatingPromptControls = wasUpdating;
        }
    }

    private void PopulatePromptProviderComboBox(ComboBox comboBox, PromptProviderPurpose purpose)
    {
        var activeProvider = _providers.FirstOrDefault(provider =>
            string.Equals(
                provider.Id,
                _configuration.Translation.ActiveProviderId,
                StringComparison.OrdinalIgnoreCase));
        var options = new List<PromptProviderOption>
        {
            new(null, AppLocalization.Format(
                "Prompt.Provider.Global",
                activeProvider?.DisplayName ?? T("Provider.Active.None")))
        };
        options.AddRange(_providers.Select(provider =>
            new PromptProviderOption(provider.Id, provider.DisplayName)));
        var configuredProviderId = _configuration.Translation.PromptProviders.GetProviderId(purpose);
        comboBox.ItemsSource = options;
        comboBox.SelectedItem = options.FirstOrDefault(option =>
            string.Equals(option.ProviderId, configuredProviderId, StringComparison.OrdinalIgnoreCase))
            ?? options[0];
    }

    private void VoiceTranslationPreference_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingVrControlPanelSettings)
        {
            return;
        }

        try
        {
            CopyVoiceTranslationControlsTo(_configuration.VrChatVoiceInput);
            var translation = CreateTranslationSnapshot(
                _configuration.Translation.Providers,
                _configuration.Translation.ActiveProviderId,
                SelectedTag(TargetLanguageComboBox));
            CopyPromptEditorsTo(translation);
            ApplyRuntimeTranslationSettings(translation, "更新语音翻译设置");
            SaveConfigurationSafely("语音翻译设置");
        }
        catch (Exception exception)
        {
            _log.Error("[configuration] 更新语音翻译设置失败。", exception);
            ShowConfigurationError(exception.Message);
        }
    }

    private void DesktopVoiceHotKey_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            _configuration.VrChatVoiceInput.DesktopHotKeyEnabled =
                DesktopVoiceHotKeyEnabledCheckBox.IsChecked == true;
            ApplyDesktopVoiceHotKeyHook();
            SaveConfigurationSafely("VRChat 桌面 PTT 热键");
        }
        catch (Exception exception)
        {
            _log.Error("[voice-input] 更新桌面 PTT 热键失败。", exception);
            ShowConfigurationError(exception.Message);
        }
    }

    private void DesktopVoiceHotKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingDesktopVoiceHotKey)
        {
            return;
        }

        _capturingDesktopVoiceHotKey = true;
        _desktopVoiceHotKey.Apply(false, _configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey);
        DesktopVoiceHotKeyButton.Content = T("Voice.DesktopHotKey.PressAnyKey");
        DesktopVoiceHotKeyButton.Focus();
        Keyboard.Focus(DesktopVoiceHotKeyButton);
    }

    private void DesktopVoiceHotKeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingDesktopVoiceHotKey)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey is not (>= 1 and <= 254))
        {
            return;
        }

        _capturingDesktopVoiceHotKey = false;
        _configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey = virtualKey;
        UpdateDesktopVoiceHotKeyButton();
        try
        {
            ApplyDesktopVoiceHotKeyHook();
            SaveConfigurationSafely("VRChat 桌面 PTT 按键绑定");
        }
        catch (Exception exception)
        {
            _log.Error("[voice-input] 更新桌面 PTT 按键绑定失败。", exception);
            ShowConfigurationError(exception.Message);
        }
    }

    private void DesktopVoiceHotKeyButton_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (!_capturingDesktopVoiceHotKey)
        {
            return;
        }

        _capturingDesktopVoiceHotKey = false;
        UpdateDesktopVoiceHotKeyButton();
        try
        {
            ApplyDesktopVoiceHotKeyHook();
        }
        catch (Exception exception)
        {
            _log.Error("[voice-input] 恢复桌面 PTT 按键监听失败。", exception);
            ShowConfigurationError(exception.Message);
        }
    }

    private void UpdateDesktopVoiceHotKeyButton()
    {
        DesktopVoiceHotKeyButton.Content = _capturingDesktopVoiceHotKey
            ? T("Voice.DesktopHotKey.PressAnyKey")
            : DesktopPushToTalkHotKey.GetDisplayName(
                _configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey);
    }

    private void ApplyDesktopVoiceHotKeyHook()
    {
        _desktopVoiceHotKey.Apply(
            _runtime is not null &&
            _configuration.VrChatVoiceInput.Enabled &&
            _configuration.VrChatVoiceInput.DesktopHotKeyEnabled &&
            !_capturingDesktopVoiceHotKey,
            _configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey);
    }

    private void OnDesktopVoiceHotKeyPressedChanged(object? sender, bool pressed)
    {
        _runtime?.SetDesktopVoiceInputPressed(pressed);
        _log.Info($"[voice-input] 桌面 PTT {(pressed ? "按下" : "松开")}。");
    }

    private AppConfiguration ReadControls()
    {
        CopyPromptEditorsTo(_configuration.GetPromptSet());
        _configuration.ApplyPromptLanguage();
        UpdateSelectedProviderFromControls();
        var translation = CreateTranslationSnapshot(
            _providers,
            _configuration.Translation.ActiveProviderId,
            SelectedTag(TargetLanguageComboBox));
        CopyPromptEditorsTo(translation);
        ValidateActiveProvider(translation);

        if (!int.TryParse(OscPortTextBox.Text, out var oscPort) || oscPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(T("Validation.OscPort"));
        }
        var oscChunkIntervalMilliseconds = ReadOscChunkIntervalMilliseconds();

        var voiceEnabled = VoiceInputEnabledCheckBox.IsChecked == true;
        var asrEngine = AsrEngineComboBox.SelectedItem as AsrEngineOption ?? AsrEngineOption.Cpu;
        if (voiceEnabled)
        {
            RequireExistingFile(
                asrEngine.IsVulkan
                    ? SenseVoiceVulkanExecutableTextBox.Text
                    : SenseVoiceExecutableTextBox.Text,
                T(asrEngine.IsVulkan ? "Validation.AsrVulkanRuntime" : "Validation.AsrCpuRuntime"));
            RequireExistingFile(SenseVoiceModelTextBox.Text, T("Validation.AsrModel"));
            if (!string.IsNullOrWhiteSpace(SenseVoiceVadTextBox.Text))
            {
                RequireExistingFile(SenseVoiceVadTextBox.Text, T("Validation.AsrVadModel"));
            }
        }

        return new AppConfiguration
        {
            UiLanguage = ApplicationLanguages.Normalize(SelectedTag(UiLanguageComboBox)),
            HotKeyVirtualKey = _configuration.HotKeyVirtualKey,
            SelectionTimeoutSeconds = _configuration.SelectionTimeoutSeconds,
            CaptureDirectory = _configuration.CaptureDirectory,
            StereoCompositionMode = SelectedTag(StereoCompositionComboBox),
            InvertResultScroll = InvertResultScrollCheckBox.IsChecked == true,
            ShowPointerRay = ShowPointerRayCheckBox.IsChecked == true,
            PointerSmoothingStrength = SelectedPointerSmoothingStrength(),
            Prompts = _configuration.Prompts.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase),
            Translation = translation,
            Speech = new SpeechConfiguration
            {
                DeviceId = SelectedTag(MicrophoneComboBox),
                HoldThresholdMilliseconds = _configuration.Speech.HoldThresholdMilliseconds,
                MinimumDurationMilliseconds = _configuration.Speech.MinimumDurationMilliseconds,
                MaximumDurationSeconds = _configuration.Speech.MaximumDurationSeconds,
                SenseVoiceExecutablePath = SenseVoiceExecutableTextBox.Text.Trim(),
                SenseVoiceVulkanExecutablePath = SenseVoiceVulkanExecutableTextBox.Text.Trim(),
                SenseVoiceBackend = asrEngine.Backend,
                SenseVoiceVulkanDeviceIndex = asrEngine.DeviceIndex,
                SenseVoiceVulkanDeviceName = asrEngine.DeviceName,
                SenseVoiceModelPath = SenseVoiceModelTextBox.Text.Trim(),
                SenseVoiceVadModelPath = NullIfWhiteSpace(SenseVoiceVadTextBox.Text),
                RecognitionLanguage = SpeechRecognitionLanguages.Normalize(
                    SelectedTag(AsrLanguageComboBox)),
                EffectiveRecognitionLanguage = SpeechRecognitionLanguages.Resolve(
                    SelectedTag(AsrLanguageComboBox),
                    SelectedTag(UiLanguageComboBox)),
                CustomCommandSystemPrompt = CustomCommandSystemPromptTextBox.Text,
                CustomCommandPrompt = CustomCommandPromptTextBox.Text
            },
            VrChatVoiceInput = new VrChatVoiceInputConfiguration
            {
                Enabled = voiceEnabled,
                DesktopHotKeyEnabled = DesktopVoiceHotKeyEnabledCheckBox.IsChecked == true,
                DesktopHotKeyVirtualKey = _configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey,
                Host = OscHostTextBox.Text.Trim(),
                Port = oscPort,
                SendImmediately = OscSendImmediatelyCheckBox.IsChecked == true,
                MaxChatboxCharacters = _configuration.VrChatVoiceInput.MaxChatboxCharacters,
                StreamingChunkIntervalMilliseconds = oscChunkIntervalMilliseconds,
                DownloadSource = HfMirrorCheckBox.IsChecked == true ? "hf-mirror" : "official",
                TranslationEnabled = VoiceTranslationEnabledCheckBox.IsChecked == true,
                TranslationDisplayMode = VoiceTranslationDisplayModes.Normalize(
                    SelectedTag(VoiceTranslationDisplayModeComboBox)),
                TranslationTargetLanguage = SelectedTag(VoiceTranslationTargetLanguageComboBox),
                TranslationSystemPrompt = VoiceTranslationSystemPromptTextBox.Text,
                TranslationPrompt = VoiceTranslationPromptTextBox.Text
            },
            Subtitles = ReadSubtitleControls(),
            AndroidMirror = ReadAndroidMirrorControls()
        };
    }

    private AndroidMirrorConfiguration ReadAndroidMirrorControls() => new()
    {
        Enabled = AndroidMirrorEnabledCheckBox.IsChecked == true,
        DeviceSerial = (AndroidDeviceComboBox.SelectedItem as AndroidDeviceInfo)?.Serial
            ?? _configuration.AndroidMirror.DeviceSerial,
        MaximumSize = AndroidMirrorConfiguration.NormalizeMaximumSize(
            int.Parse(SelectedTag(AndroidMirrorMaxSizeComboBox))),
        MaximumFramesPerSecond = AndroidMirrorConfiguration.NormalizeMaximumFramesPerSecond(
            int.Parse(SelectedTag(AndroidMirrorMaxFpsComboBox))),
        VideoBitRateMbps = NormalizeAndroidMirrorBitRate(
            int.Parse(SelectedTag(AndroidMirrorBitRateComboBox))),
        VideoDecoder = AndroidVideoDecoders.Normalize(SelectedTag(AndroidMirrorDecoderComboBox)),
        WindowWidthMeters = AndroidMirrorConfiguration.NormalizeWindowWidthMeters(
            _configuration.AndroidMirror.WindowWidthMeters)
    };

    private static int NormalizeAndroidMirrorBitRate(int value) => value switch
    {
        >= 10 => 12,
        >= 6 => 8,
        _ => 4
    };

    private void SetConfigurationControlsEnabled(bool enabled)
    {
        UiLanguageComboBox.IsEnabled = true;
        StereoCompositionComboBox.IsEnabled = true;
        InvertResultScrollCheckBox.IsEnabled = true;
        ShowPointerRayCheckBox.IsEnabled = true;
        PointerSmoothingComboBox.IsEnabled = true;
        ProviderListBox.IsEnabled = true;
        AddProviderButton.IsEnabled = enabled;
        DeleteProviderButton.IsEnabled = enabled &&
            ProviderListBox.SelectedItem is TranslationProviderConfiguration { IsMock: false };
        ProviderNameTextBox.IsEnabled = true;
        BaseUrlTextBox.IsEnabled = enabled;
        ApiKeyPasswordBox.IsEnabled = enabled;
        ProviderModelComboBox.IsEnabled = enabled;
        ProviderConcurrencyComboBox.IsEnabled = true;
        RefreshProviderButton.IsEnabled = enabled;
        ProviderActiveCheckBox.IsEnabled = true;
        TargetLanguageComboBox.IsEnabled = true;
        VoiceInputEnabledCheckBox.IsEnabled = enabled;
        DesktopVoiceHotKeyEnabledCheckBox.IsEnabled = true;
        DesktopVoiceHotKeyButton.IsEnabled = true;
        MicrophoneComboBox.IsEnabled = enabled;
        OscHostTextBox.IsEnabled = enabled;
        OscPortTextBox.IsEnabled = enabled;
        OscSendImmediatelyCheckBox.IsEnabled = enabled;
        OscChunkIntervalTextBox.IsEnabled = true;
        VoiceTranslationEnabledCheckBox.IsEnabled = true;
        VoiceTranslationTargetLanguageComboBox.IsEnabled = true;
        VoiceTranslationDisplayModeComboBox.IsEnabled = true;
        AsrVariantComboBox.IsEnabled = enabled;
        AsrEngineComboBox.IsEnabled = enabled;
        AsrLanguageComboBox.IsEnabled = enabled;
        HfMirrorCheckBox.IsEnabled = true;
        DownloadAsrButton.IsEnabled = enabled;
        SenseVoiceExecutableTextBox.IsEnabled = enabled;
        SenseVoiceVulkanExecutableTextBox.IsEnabled = enabled;
        SenseVoiceModelTextBox.IsEnabled = enabled;
        SenseVoiceVadTextBox.IsEnabled = enabled;
        SubtitleEnabledCheckBox.IsEnabled = true;
        AndroidMirrorEnabledCheckBox.IsEnabled = true;
        SubtitleAsrBackendComboBox.IsEnabled = true;
        VibeVoiceServiceUrlTextBox.IsEnabled = true;
        VibeVoiceApiKeyPasswordBox.IsEnabled = true;
        TestVibeVoiceServiceButton.IsEnabled = true;
        OpenVibeVoiceManagerButton.IsEnabled = true;
        SubtitleTargetLanguageComboBox.IsEnabled = true;
        SubtitleShowOriginalCheckBox.IsEnabled = true;
        SubtitleTranslateCheckBox.IsEnabled = true;
        SubtitleSpeakerColorsCheckBox.IsEnabled = true;
        SubtitleHistoryEntriesTextBox.IsEnabled = true;
        SubtitleHistoryCharactersTextBox.IsEnabled = true;
        SubtitleDiarizationEnabledCheckBox.IsEnabled = true;
        SubtitleDiarizationThreadsComboBox.IsEnabled = true;
        SubtitleDiarizationThresholdTextBox.IsEnabled = true;
        DownloadSubtitleModelsButton.IsEnabled = true;
        SetAndroidMirrorControlsEnabled(_androidMirrorSession is null);
        UpdateExperimentalFeatureUi();
        UpdateProviderActiveState();
    }

    private void AndroidDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAndroidMirrorStartAvailability();
        PushAndroidMirrorControlState();
    }

    private async void AndroidMirrorEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingExperimentalFeatures)
        {
            return;
        }

        _updatingExperimentalFeatures = true;
        var requested = AndroidMirrorEnabledCheckBox.IsChecked == true;
        try
        {
            _configuration.AndroidMirror = ReadAndroidMirrorControls();
            if (requested)
            {
                ValidateAndroidMirrorExperimentalEnvironment(_configuration.AndroidMirror);
                await RefreshAndroidDevicesAsync();
                AndroidMirrorStatusText.Text = T("AndroidMirror.Experimental.Ready");
            }
            else
            {
                await StopAndroidMirrorAsync(closeOverlay: true);
                AndroidMirrorStatusText.Text = T("AndroidMirror.Status.Disabled");
            }

            SaveConfigurationSafely("手机镜像实验性功能开关");
            ApplyExperimentalFeatureAvailability();
        }
        catch (Exception exception)
        {
            _configuration.AndroidMirror.Enabled = false;
            AndroidMirrorEnabledCheckBox.IsChecked = false;
            _log.Error("[android-mirror] 启用实验性手机镜像失败。", exception);
            SaveConfigurationSafely("手机镜像实验性功能验证失败");
            ShowExperimentalFeatureError(exception.Message);
            ApplyExperimentalFeatureAvailability();
        }
        finally
        {
            _updatingExperimentalFeatures = false;
            UpdateExperimentalFeatureUi();
        }
    }

    private void ValidateAndroidMirrorExperimentalEnvironment(
        AndroidMirrorConfiguration configuration)
    {
        if (!_androidMirrorRuntime.IsInstalled)
        {
            throw new InvalidOperationException(T("AndroidMirror.Experimental.RuntimeRequired"));
        }

        if (AndroidVideoDecoders.Normalize(configuration.VideoDecoder) == AndroidVideoDecoders.D3D11 &&
            !_androidMirrorRuntime.IsHardwareDecoderInstalled)
        {
            throw new InvalidOperationException(T("AndroidMirror.Experimental.HardwareRequired"));
        }

        if (_androidMirrorRuntime.ResolveAvailableAdbPath() is null)
        {
            throw new InvalidOperationException(T("AndroidMirror.Experimental.AdbRequired"));
        }
    }

    private void SetAndroidMirrorControlsEnabled(bool enabled)
    {
        var canEdit = enabled && _androidMirrorSession is null;
        AndroidDeviceComboBox.IsEnabled = canEdit;
        RefreshAndroidDevicesButton.IsEnabled = canEdit;
        AndroidMirrorMaxSizeComboBox.IsEnabled = canEdit;
        AndroidMirrorMaxFpsComboBox.IsEnabled = canEdit;
        AndroidMirrorBitRateComboBox.IsEnabled = canEdit;
        AndroidMirrorDecoderComboBox.IsEnabled = canEdit;
        DownloadAndroidMirrorRuntimeButton.IsEnabled = canEdit &&
            (!_androidMirrorRuntime.IsInstalled ||
             !_androidMirrorRuntime.IsHardwareDecoderInstalled);
    }

    private void UpdateAndroidMirrorRuntimeStatus()
    {
        var installed = _androidMirrorRuntime.IsInstalled;
        var hardwareInstalled = _androidMirrorRuntime.IsHardwareDecoderInstalled;
        AndroidMirrorRuntimeStatusText.Text = T(!installed
            ? "AndroidMirror.Runtime.Missing"
            : hardwareInstalled
                ? "AndroidMirror.Runtime.Installed"
                : "AndroidMirror.Runtime.SoftwareOnly");
        AndroidMirrorRuntimeStatusText.Foreground = installed && hardwareInstalled
            ? new SolidColorBrush(Color.FromRgb(8, 126, 114))
            : new SolidColorBrush(Color.FromRgb(154, 90, 0));
        DownloadAndroidMirrorRuntimeButton.IsEnabled =
            (!installed || !hardwareInstalled) && _androidMirrorSession is null;
        UpdateAndroidMirrorStartAvailability();
        PushAndroidMirrorControlState();
    }

    private void UpdateAndroidMirrorStartAvailability()
    {
        if (StartStopAndroidMirrorButton is null)
        {
            return;
        }

        StartStopAndroidMirrorButton.IsEnabled = _androidMirrorSession is not null ||
            (_configuration.AndroidMirror.Enabled &&
             _runtime is not null &&
             _androidMirrorRuntime.IsInstalled &&
             (AndroidVideoDecoders.Normalize(SelectedTag(AndroidMirrorDecoderComboBox)) !=
                  AndroidVideoDecoders.D3D11 ||
              _androidMirrorRuntime.IsHardwareDecoderInstalled) &&
             AndroidDeviceComboBox.SelectedItem is AndroidDeviceInfo { IsOnline: true });
    }

    private void RefreshProviderList(TranslationProviderConfiguration selected)
    {
        _populatingProvider = true;
        ProviderListBox.ItemsSource = null;
        ProviderListBox.ItemsSource = _providers;
        ProviderListBox.SelectedItem = selected;
        _populatingProvider = false;
        PopulateSelectedProvider();
        UpdateActiveProviderSummary();
        PopulatePromptProviderControls();
    }

    private void PopulateSelectedProvider()
    {
        if (ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected)
        {
            return;
        }

        _populatingProvider = true;
        var isMock = selected.IsMock;
        ProviderEditorTitleText.Text = T(isMock ? "Provider.Mock.Name" : "Provider.Compatible.Title");
        ProviderEditorDescriptionText.Text = isMock
            ? T("Provider.Mock.EditorDescription")
            : T("Provider.Compatible.Description");
        MockProviderPanel.Visibility = isMock ? Visibility.Visible : Visibility.Collapsed;
        CompatibleProviderPanel.Visibility = isMock ? Visibility.Collapsed : Visibility.Visible;
        ProviderNameTextBox.Text = selected.Name;
        BaseUrlTextBox.Text = selected.BaseUrl;
        ApiKeyPasswordBox.Password = selected.ApiKey;
        var models = _providerModelCatalog.TryGetValue(selected.Id, out var discoveredModels)
            ? discoveredModels
            : string.IsNullOrWhiteSpace(selected.Model)
                ? []
                : [selected.Model];
        BindProviderModels(selected, models, selected.Model, selectFirstWhenMissing: false);
        SelectByTag(ProviderConcurrencyComboBox, selected.MaxConcurrency.ToString());
        _populatingProvider = false;
        if (!isMock)
        {
            ProviderConnectionText.Text = T("Provider.NotTested");
            ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
        }
        DeleteProviderButton.IsEnabled = !isMock && _runtime is null;
        UpdateProviderActiveState();
    }

    private void UpdateSelectedProviderFromControls()
    {
        if (_populatingProvider ||
            ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected)
        {
            return;
        }

        selected.MaxConcurrency = int.Parse(SelectedTag(ProviderConcurrencyComboBox));
        selected.Name = ProviderNameTextBox.Text.Trim();
        if (selected.IsMock)
        {
            return;
        }

        selected.BaseUrl = BaseUrlTextBox.Text.Trim();
        selected.ApiKey = ApiKeyPasswordBox.Password;
        selected.Model = ProviderModelComboBox.Text.Trim();
    }

    private void BindProviderModels(
        TranslationProviderConfiguration provider,
        IEnumerable<string> models,
        string? preferredModel,
        bool selectFirstWhenMissing)
    {
        var available = models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _providerModelCatalog[provider.Id] = available;
        _providerModelFilter = string.Empty;
        _providerModelView = new ListCollectionView(available)
        {
            Filter = value => ProviderModelSearch.Matches(value?.ToString(), _providerModelFilter)
        };
        ProviderModelComboBox.ItemsSource = _providerModelView;

        var selectedModel = available.FirstOrDefault(model =>
            string.Equals(model, preferredModel, StringComparison.OrdinalIgnoreCase));
        if (selectedModel is null && selectFirstWhenMissing)
        {
            selectedModel = available.FirstOrDefault();
        }

        ProviderModelComboBox.SelectedItem = selectedModel;
        ProviderModelComboBox.Text = selectedModel ?? preferredModel ?? string.Empty;
    }

    private void UpdateProviderActiveState()
    {
        var isActive = ProviderListBox.SelectedItem is TranslationProviderConfiguration selected &&
                       string.Equals(selected.Id, _configuration.Translation.ActiveProviderId, StringComparison.OrdinalIgnoreCase);
        ProviderActiveCheckBox.IsChecked = isActive;
        UpdateActiveProviderSummary();
    }

    private void UpdateActiveProviderSummary()
    {
        ProviderListBox.Tag = _configuration.Translation.ActiveProviderId;
        ProviderListBox.Items.Refresh();
    }

    private void UpdateAsrInstallStatus()
    {
        if (AsrVariantComboBox.SelectedItem is null)
        {
            return;
        }

        var statuses = _senseVoiceDownloads.GetStatuses(SelectedTag(AsrVariantComboBox));
        var installed = statuses.Count(status => status.Installed);
        var separator = ApplicationLanguages.Normalize(_configuration.UiLanguage) == ApplicationLanguages.English
            ? ", "
            : "、";
        AsrInstallStatusText.Text = installed == statuses.Count
            ? AppLocalization.Format(
                "Asr.Installed",
                string.Join(separator, statuses.Select(LocalizeAsrAssetName)))
            : AppLocalization.Format(
                "Asr.Missing",
                string.Join(separator, statuses.Where(status => !status.Installed).Select(LocalizeAsrAssetName)));
        AsrInstallStatusText.Foreground = installed == statuses.Count
            ? new SolidColorBrush(Color.FromRgb(8, 126, 114))
            : new SolidColorBrush(Color.FromRgb(154, 90, 0));
    }

    private static string LocalizeAsrAssetName(SenseVoiceAssetStatus status) => status.Id switch
    {
        "runtime-cpu" => T("Asr.Asset.CpuRuntime"),
        "runtime-vulkan" => T("Asr.Asset.VulkanRuntime"),
        "fsmn-vad" => T("Asr.Asset.Vad"),
        _ => status.DisplayName
    };

    private void PopulateMicrophones()
    {
        MicrophoneComboBox.Items.Clear();
        MicrophoneComboBox.Items.Add(new ComboBoxItem { Content = T("Voice.Microphone.Default"), Tag = string.Empty });
        try
        {
            foreach (var device in WasapiCommandRecorder.ListCaptureDevices())
            {
                MicrophoneComboBox.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
            }
        }
        catch (Exception exception)
        {
            _log.Error("枚举麦克风失败，将使用 Windows 默认通讯设备。", exception);
        }

        SelectByTag(MicrophoneComboBox, _configuration.Speech.DeviceId);
    }

    private void PopulateAsrEngines()
    {
        List<AsrEngineOption> options = [AsrEngineOption.Cpu];
        try
        {
            options.AddRange(VulkanDeviceInspector.ListDevices().Select(device =>
                new AsrEngineOption(
                    "vulkan",
                    device.Index,
                    device.Name,
                    $"Vulkan · {device.Name}（{device.DeviceType}）")));
        }
        catch (Exception exception)
        {
            _log.Error("枚举 Vulkan GPU 失败，本次仅提供 CPU 引擎。", exception);
        }

        AsrEngineComboBox.ItemsSource = options;
        AsrEngineOption selected = options[0];
        if (string.Equals(
                _configuration.Speech.SenseVoiceBackend,
                "vulkan",
                StringComparison.OrdinalIgnoreCase))
        {
            selected = options.FirstOrDefault(option =>
                           option.IsVulkan &&
                           option.DeviceIndex == _configuration.Speech.SenseVoiceVulkanDeviceIndex)
                       ?? options.FirstOrDefault(option =>
                           option.IsVulkan &&
                           string.Equals(
                               option.DeviceName,
                               _configuration.Speech.SenseVoiceVulkanDeviceName,
                               StringComparison.OrdinalIgnoreCase))
                       ?? options[0];
            if (!selected.IsVulkan)
            {
                _log.Warning(
                    "[configuration] 已配置的 SenseVoice Vulkan GPU 当前不可用，界面回退为 CPU。");
            }
        }

        AsrEngineComboBox.SelectedItem = selected;
        _log.Info(
            $"[configuration] SenseVoice 可用引擎：CPU + {options.Count - 1} 个 Vulkan GPU；" +
            $"当前={selected.DisplayName}。");
    }

    private void CopySelectedAsrEngineTo(SpeechConfiguration configuration)
    {
        var selected = AsrEngineComboBox.SelectedItem as AsrEngineOption ?? AsrEngineOption.Cpu;
        configuration.SenseVoiceBackend = selected.Backend;
        configuration.SenseVoiceVulkanDeviceIndex = selected.DeviceIndex;
        configuration.SenseVoiceVulkanDeviceName = selected.DeviceName;
    }

    private static TranslationProviderConfiguration CloneProvider(TranslationProviderConfiguration provider) =>
        new()
        {
            Id = provider.Id,
            Name = provider.Name,
            Type = provider.Type,
            BaseUrl = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            Model = provider.Model,
            MaxConcurrency = provider.MaxConcurrency
        };

    private TranslationConfiguration CreateTranslationSnapshot(
        IEnumerable<TranslationProviderConfiguration> providers,
        string activeProviderId,
        string targetLanguage) =>
        new()
        {
            ActiveProviderId = activeProviderId,
            Providers = providers.Select(CloneProvider).ToList(),
            TargetLanguage = targetLanguage,
            EnableStreaming = _configuration.Translation.EnableStreaming,
            DisableThinking = _configuration.Translation.DisableThinking,
            PromptProviders = _configuration.Translation.PromptProviders.Clone(),
            SystemPrompt = _configuration.Translation.SystemPrompt,
            MarkdownTranslationPrompt = _configuration.Translation.MarkdownTranslationPrompt,
            LayoutTranslationSystemPrompt = _configuration.Translation.LayoutTranslationSystemPrompt,
            LayoutTranslationPrompt = _configuration.Translation.LayoutTranslationPrompt
        };

    private void CopyPromptEditorsTo(TranslationConfiguration translation)
    {
        translation.SystemPrompt = MarkdownSystemPromptTextBox.Text;
        translation.MarkdownTranslationPrompt = MarkdownTranslationPromptTextBox.Text;
        translation.LayoutTranslationSystemPrompt = LayoutSystemPromptTextBox.Text;
        translation.LayoutTranslationPrompt = LayoutTranslationPromptTextBox.Text;
        translation.PromptProviders.SetProviderId(
            PromptProviderPurpose.DirectTranslation,
            SelectedPromptProviderId(DirectPromptProviderComboBox));
        translation.PromptProviders.SetProviderId(
            PromptProviderPurpose.LayoutTranslation,
            SelectedPromptProviderId(LayoutPromptProviderComboBox));
        translation.PromptProviders.SetProviderId(
            PromptProviderPurpose.CustomCommand,
            SelectedPromptProviderId(CustomPromptProviderComboBox));
        translation.PromptProviders.SetProviderId(
            PromptProviderPurpose.VoiceTranslation,
            SelectedPromptProviderId(VoicePromptProviderComboBox));
        translation.PromptProviders.SetProviderId(
            PromptProviderPurpose.SubtitleTranslation,
            SelectedPromptProviderId(SubtitlePromptProviderComboBox));
    }

    private void CopyPromptEditorsTo(LocalizedPromptConfiguration prompts)
    {
        prompts.MarkdownSystemPrompt = MarkdownSystemPromptTextBox.Text;
        prompts.MarkdownTranslationPrompt = MarkdownTranslationPromptTextBox.Text;
        prompts.LayoutSystemPrompt = LayoutSystemPromptTextBox.Text;
        prompts.LayoutTranslationPrompt = LayoutTranslationPromptTextBox.Text;
        prompts.CustomCommandSystemPrompt = CustomCommandSystemPromptTextBox.Text;
        prompts.CustomCommandPrompt = CustomCommandPromptTextBox.Text;
        prompts.VoiceTranslationSystemPrompt = VoiceTranslationSystemPromptTextBox.Text;
        prompts.VoiceTranslationPrompt = VoiceTranslationPromptTextBox.Text;
        prompts.SubtitleTranslationSystemPrompt = SubtitleTranslationSystemPromptTextBox.Text;
        prompts.SubtitleTranslationPrompt = SubtitleTranslationPromptTextBox.Text;
    }

    private static string? SelectedPromptProviderId(ComboBox comboBox) =>
        (comboBox.SelectedItem as PromptProviderOption)?.ProviderId;

    private void CopyVoiceTranslationControlsTo(VrChatVoiceInputConfiguration voice)
    {
        voice.TranslationEnabled = VoiceTranslationEnabledCheckBox.IsChecked == true;
        voice.TranslationDisplayMode = VoiceTranslationDisplayModes.Normalize(
            SelectedTag(VoiceTranslationDisplayModeComboBox));
        voice.TranslationTargetLanguage = SelectedTag(VoiceTranslationTargetLanguageComboBox);
        voice.TranslationSystemPrompt = VoiceTranslationSystemPromptTextBox.Text;
        voice.TranslationPrompt = VoiceTranslationPromptTextBox.Text;
    }

    private void ApplyRuntimeTranslationSettings(
        TranslationConfiguration translation,
        string reason)
    {
        var stereoCompositionMode = SelectedTag(StereoCompositionComboBox);
        var invertResultScroll = InvertResultScrollCheckBox.IsChecked == true;
        _runtime?.ApplyLiveSettings(
            stereoCompositionMode,
            invertResultScroll,
            translation,
            _configuration.Speech.CustomCommandSystemPrompt,
            _configuration.Speech.CustomCommandPrompt);
        _runtime?.ApplyPointerRaySetting(ShowPointerRayCheckBox.IsChecked == true);
        _runtime?.ApplyPointerSmoothingStrength(SelectedPointerSmoothingStrength());
        _configuration.StereoCompositionMode = stereoCompositionMode;
        _configuration.InvertResultScroll = invertResultScroll;
        _configuration.ShowPointerRay = ShowPointerRayCheckBox.IsChecked == true;
        _configuration.PointerSmoothingStrength = SelectedPointerSmoothingStrength();
        _configuration.Translation = translation;
        _log.Info(
            $"[configuration] {reason}：捕获眼睛={stereoCompositionMode}，" +
            $"滚动反转={invertResultScroll}，Provider={DescribeActiveProvider(translation)}，" +
            $"最大并发={translation.GetActiveProvider()?.MaxConcurrency}，" +
            $"目标语言={translation.TargetLanguage}。");
    }

    private bool SaveConfigurationSafely(string reason)
    {
        try
        {
            _configurationStore.Save(CloneConfiguration(_configuration));
            _log.Info($"[configuration] 已保存：{reason}，文件={_configurationStore.FilePath}。");
            return true;
        }
        catch (Exception exception)
        {
            _log.Error($"[configuration] 保存失败：{reason}。", exception);
            ShowConfigurationError(AppLocalization.Format("Configuration.SaveFailed", exception.Message));
            return false;
        }
    }

    private static AppConfiguration CloneConfiguration(AppConfiguration configuration) =>
        new()
        {
            UiLanguage = configuration.UiLanguage,
            HotKeyVirtualKey = configuration.HotKeyVirtualKey,
            SelectionTimeoutSeconds = configuration.SelectionTimeoutSeconds,
            CaptureDirectory = configuration.CaptureDirectory,
            StereoCompositionMode = configuration.StereoCompositionMode,
            InvertResultScroll = configuration.InvertResultScroll,
            ShowPointerRay = configuration.ShowPointerRay,
            PointerSmoothingStrength = configuration.PointerSmoothingStrength,
            Prompts = configuration.Prompts.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase),
            Translation = new TranslationConfiguration
            {
                ActiveProviderId = configuration.Translation.ActiveProviderId,
                Providers = configuration.Translation.Providers.Select(CloneProvider).ToList(),
                TargetLanguage = configuration.Translation.TargetLanguage,
                EnableStreaming = configuration.Translation.EnableStreaming,
                DisableThinking = configuration.Translation.DisableThinking,
                PromptProviders = configuration.Translation.PromptProviders.Clone(),
                SystemPrompt = configuration.Translation.SystemPrompt,
                MarkdownTranslationPrompt = configuration.Translation.MarkdownTranslationPrompt,
                LayoutTranslationSystemPrompt = configuration.Translation.LayoutTranslationSystemPrompt,
                LayoutTranslationPrompt = configuration.Translation.LayoutTranslationPrompt
            },
            Speech = new SpeechConfiguration
            {
                DeviceId = configuration.Speech.DeviceId,
                HoldThresholdMilliseconds = configuration.Speech.HoldThresholdMilliseconds,
                MinimumDurationMilliseconds = configuration.Speech.MinimumDurationMilliseconds,
                MaximumDurationSeconds = configuration.Speech.MaximumDurationSeconds,
                SenseVoiceExecutablePath = configuration.Speech.SenseVoiceExecutablePath,
                SenseVoiceVulkanExecutablePath = configuration.Speech.SenseVoiceVulkanExecutablePath,
                SenseVoiceBackend = configuration.Speech.SenseVoiceBackend,
                SenseVoiceVulkanDeviceIndex = configuration.Speech.SenseVoiceVulkanDeviceIndex,
                SenseVoiceVulkanDeviceName = configuration.Speech.SenseVoiceVulkanDeviceName,
                SenseVoiceModelPath = configuration.Speech.SenseVoiceModelPath,
                SenseVoiceVadModelPath = configuration.Speech.SenseVoiceVadModelPath,
                RecognitionLanguage = configuration.Speech.RecognitionLanguage,
                EffectiveRecognitionLanguage = configuration.Speech.EffectiveRecognitionLanguage,
                CustomCommandSystemPrompt = configuration.Speech.CustomCommandSystemPrompt,
                CustomCommandPrompt = configuration.Speech.CustomCommandPrompt
            },
            VrChatVoiceInput = new VrChatVoiceInputConfiguration
            {
                Enabled = configuration.VrChatVoiceInput.Enabled,
                DesktopHotKeyEnabled = configuration.VrChatVoiceInput.DesktopHotKeyEnabled,
                DesktopHotKeyVirtualKey =
                    configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey,
                Host = configuration.VrChatVoiceInput.Host,
                Port = configuration.VrChatVoiceInput.Port,
                SendImmediately = configuration.VrChatVoiceInput.SendImmediately,
                MaxChatboxCharacters = configuration.VrChatVoiceInput.MaxChatboxCharacters,
                StreamingChunkIntervalMilliseconds =
                    configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds,
                DownloadSource = configuration.VrChatVoiceInput.DownloadSource,
                TranslationEnabled = configuration.VrChatVoiceInput.TranslationEnabled,
                TranslationDisplayMode = configuration.VrChatVoiceInput.TranslationDisplayMode,
                TranslationTargetLanguage = configuration.VrChatVoiceInput.TranslationTargetLanguage,
                TranslationSystemPrompt = configuration.VrChatVoiceInput.TranslationSystemPrompt,
                TranslationPrompt = configuration.VrChatVoiceInput.TranslationPrompt
            },
            Subtitles = new SubtitleConfiguration
            {
                Enabled = configuration.Subtitles.Enabled,
                AsrBackend = configuration.Subtitles.AsrBackend,
                VibeVoiceServiceUrl = configuration.Subtitles.VibeVoiceServiceUrl,
                VibeVoiceApiKey = configuration.Subtitles.VibeVoiceApiKey,
                ShowOriginalText = configuration.Subtitles.ShowOriginalText,
                TranslateText = configuration.Subtitles.TranslateText,
                TargetLanguage = configuration.Subtitles.TargetLanguage,
                MaximumHistoryEntries = configuration.Subtitles.MaximumHistoryEntries,
                MaximumHistoryCharacters = configuration.Subtitles.MaximumHistoryCharacters,
                WindowWidthMeters = configuration.Subtitles.WindowWidthMeters,
                WindowDistanceMeters = configuration.Subtitles.WindowDistanceMeters,
                WindowOpacity = configuration.Subtitles.WindowOpacity,
                UseSpeakerColors = configuration.Subtitles.UseSpeakerColors,
                TranslationSystemPrompt = configuration.Subtitles.TranslationSystemPrompt,
                TranslationPrompt = configuration.Subtitles.TranslationPrompt,
                Diarization = new SubtitleDiarizationConfiguration
                {
                    Enabled = configuration.Subtitles.Diarization.Enabled,
                    CpuThreadCount = configuration.Subtitles.Diarization.CpuThreadCount,
                    ClusteringThreshold = configuration.Subtitles.Diarization.ClusteringThreshold,
                    SegmentationModelPath = configuration.Subtitles.Diarization.SegmentationModelPath,
                    EmbeddingModelPath = configuration.Subtitles.Diarization.EmbeddingModelPath
                }
            },
            AndroidMirror = new AndroidMirrorConfiguration
            {
                Enabled = configuration.AndroidMirror.Enabled,
                DeviceSerial = configuration.AndroidMirror.DeviceSerial,
                MaximumSize = configuration.AndroidMirror.MaximumSize,
                MaximumFramesPerSecond = configuration.AndroidMirror.MaximumFramesPerSecond,
                VideoBitRateMbps = configuration.AndroidMirror.VideoBitRateMbps,
                VideoDecoder = AndroidVideoDecoders.Normalize(
                    configuration.AndroidMirror.VideoDecoder),
                WindowWidthMeters = AndroidMirrorConfiguration.NormalizeWindowWidthMeters(
                    configuration.AndroidMirror.WindowWidthMeters)
            }
        };

    private void SubtitlePreference_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingVrControlPanelSettings)
        {
            return;
        }

        try
        {
            ApplySubtitleControlsToConfiguration();
            _subtitleSession?.ApplyConfiguration(_configuration);
            SaveConfigurationSafely("实验性字幕设置");
        }
        catch (Exception exception)
        {
            _log.Error("[subtitles] 保存字幕设置失败。", exception);
            SubtitleReplayStatusText.Text = exception.Message;
            SubtitleReplayStatusText.Foreground = new SolidColorBrush(Color.FromRgb(177, 47, 52));
        }
    }

    private async void SubtitleExperimentalEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingExperimentalFeatures)
        {
            return;
        }

        _updatingExperimentalFeatures = true;
        var requested = SubtitleEnabledCheckBox.IsChecked == true;
        try
        {
            ApplySubtitleControlsToConfiguration();
            if (requested)
            {
                await ValidateSubtitleExperimentalEnvironmentAsync();
                SubtitleReplayStatusText.Text = T("Subtitle.Experimental.Ready");
                SubtitleReplayStatusText.Foreground = new SolidColorBrush(Color.FromRgb(8, 126, 114));
            }
            else
            {
                await DisableSubtitleFeatureAsync();
            }

            _subtitleSession?.ApplyConfiguration(_configuration);
            SaveConfigurationSafely("实时字幕实验性功能开关");
            ApplyExperimentalFeatureAvailability();
        }
        catch (Exception exception)
        {
            _configuration.Subtitles.Enabled = false;
            SubtitleEnabledCheckBox.IsChecked = false;
            _log.Error("[subtitles] 启用实验性实时字幕失败。", exception);
            SaveConfigurationSafely("实时字幕实验性功能验证失败");
            SubtitleReplayStatusText.Text = exception.Message;
            SubtitleReplayStatusText.Foreground = new SolidColorBrush(Color.FromRgb(177, 47, 52));
            ShowExperimentalFeatureError(exception.Message);
            ApplyExperimentalFeatureAvailability();
        }
        finally
        {
            _updatingExperimentalFeatures = false;
            UpdateExperimentalFeatureUi();
        }
    }

    private async Task ValidateSubtitleExperimentalEnvironmentAsync()
    {
        if (string.Equals(
                _configuration.Subtitles.AsrBackend,
                SubtitleAsrBackends.VibeVoiceApi,
                StringComparison.OrdinalIgnoreCase))
        {
            var description = await ProbeVibeVoiceServiceAsync(CancellationToken.None);
            VibeVoiceServiceStatusText.Text = AppLocalization.Format(
                "Subtitle.Asr.Connected",
                description);
            VibeVoiceServiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(8, 126, 114));
            return;
        }

        var engine = AsrEngineComboBox.SelectedItem as AsrEngineOption ?? AsrEngineOption.Cpu;
        RequireExistingFile(
            engine.IsVulkan
                ? SenseVoiceVulkanExecutableTextBox.Text
                : SenseVoiceExecutableTextBox.Text,
            T(engine.IsVulkan ? "Validation.AsrVulkanRuntime" : "Validation.AsrCpuRuntime"));
        RequireExistingFile(SenseVoiceModelTextBox.Text, T("Validation.AsrModel"));
        if (!string.IsNullOrWhiteSpace(SenseVoiceVadTextBox.Text))
        {
            RequireExistingFile(SenseVoiceVadTextBox.Text, T("Validation.AsrVadModel"));
        }

        if (_configuration.Subtitles.Diarization.Enabled)
        {
            var missing = _subtitleModelDownloads.GetStatuses()
                .Where(status => !status.Installed)
                .Select(status => status.DisplayName)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(AppLocalization.Format(
                    "Subtitle.Experimental.DiarizationRequired",
                    string.Join(", ", missing)));
            }
        }
    }

    private async Task DisableSubtitleFeatureAsync()
    {
        _subtitleReplayCancellation?.Cancel();
        var overlayId = _subtitleOverlayId;
        _subtitleOverlayId = null;
        if (_runtime is not null && overlayId is { } id)
        {
            await _runtime.CloseWindowAsync(id);
        }
        _subtitleHistoryWindow?.Hide();
    }

    private void ApplyExperimentalFeatureAvailability()
    {
        _runtime?.UpdateExperimentalFeatureAvailability(
            _configuration.Subtitles.Enabled,
            _configuration.AndroidMirror.Enabled);
        UpdateExperimentalFeatureUi();
    }

    private void UpdateExperimentalFeatureUi()
    {
        if (SubtitleEnabledCheckBox is null || AndroidMirrorEnabledCheckBox is null)
        {
            return;
        }

        var subtitlesEnabled = SubtitleEnabledCheckBox.IsChecked == true;
        OpenSubtitleHistoryButton.IsEnabled = subtitlesEnabled;
        OpenSubtitleHistoryVrButton.IsEnabled = subtitlesEnabled && _runtime is not null;
        ReplaySubtitleFileButton.IsEnabled = subtitlesEnabled &&
            CancelSubtitleReplayButton.IsEnabled != true;
        UpdateAndroidMirrorStartAvailability();
    }

    private void ShowExperimentalFeatureError(string message)
    {
        MessageBox.Show(
            this,
            message,
            T("Experimental.EnableFailed"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async Task ValidateEnabledExperimentalFeaturesForStartAsync()
    {
        if (_configuration.Subtitles.Enabled)
        {
            await ValidateSubtitleExperimentalEnvironmentAsync();
        }

        if (_configuration.AndroidMirror.Enabled)
        {
            ValidateAndroidMirrorExperimentalEnvironment(_configuration.AndroidMirror);
        }
    }

    private void SubtitleAsrBackendComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSubtitleAsrPanel();
        SubtitlePreference_Changed(sender, e);
    }

    private void UpdateSubtitleAsrPanel()
    {
        if (VibeVoiceApiPanel is null || SubtitleAsrBackendComboBox is null)
        {
            return;
        }

        var useApi = string.Equals(
            SelectedTag(SubtitleAsrBackendComboBox),
            SubtitleAsrBackends.VibeVoiceApi,
            StringComparison.OrdinalIgnoreCase);
        VibeVoiceApiPanel.Visibility = useApi ? Visibility.Visible : Visibility.Collapsed;
        VibeVoiceServiceStatusText.Visibility = useApi ? Visibility.Visible : Visibility.Collapsed;
        if (SubtitleDiarizationPanel is not null)
        {
            SubtitleDiarizationPanel.IsEnabled = !useApi;
        }
    }

    private async void TestVibeVoiceServiceButton_Click(object sender, RoutedEventArgs e)
    {
        TestVibeVoiceServiceButton.IsEnabled = false;
        VibeVoiceServiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(82, 96, 90));
        VibeVoiceServiceStatusText.Text = T("Subtitle.Asr.Testing");
        try
        {
            ApplySubtitleControlsToConfiguration();
            var description = await ProbeVibeVoiceServiceAsync(CancellationToken.None);
            VibeVoiceServiceStatusText.Text = AppLocalization.Format(
                "Subtitle.Asr.Connected",
                description);
            VibeVoiceServiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(8, 126, 114));
            SaveConfigurationSafely("VibeVoice API 设置");
        }
        catch (Exception exception)
        {
            VibeVoiceServiceStatusText.Text = AppLocalization.Format(
                "Subtitle.Asr.ConnectionFailed",
                exception.Message);
            VibeVoiceServiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(177, 47, 52));
            _log.Error("[subtitles] VibeVoice API 连接测试失败。", exception);
        }
        finally
        {
            TestVibeVoiceServiceButton.IsEnabled = true;
        }
    }

    private async void OpenVibeVoiceManagerButton_Click(object sender, RoutedEventArgs e)
    {
        OpenVibeVoiceManagerButton.IsEnabled = false;
        try
        {
            ApplySubtitleControlsToConfiguration();
            var serviceUri = new Uri(_configuration.Subtitles.VibeVoiceServiceUrl);
            if (serviceUri.IsLoopback)
            {
                if (!await IsVibeVoiceServiceReachableAsync(CancellationToken.None))
                {
                    StartBundledVibeVoiceService(serviceUri.ToString());
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    while (!await IsVibeVoiceServiceReachableAsync(timeout.Token))
                    {
                        await Task.Delay(400, timeout.Token);
                    }
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = serviceUri.ToString(),
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            VibeVoiceServiceStatusText.Text = AppLocalization.Format(
                "Subtitle.Asr.ManagerFailed",
                exception.Message);
            VibeVoiceServiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(177, 47, 52));
            _log.Error("[subtitles] 打开 VibeVoice 服务管理器失败。", exception);
        }
        finally
        {
            OpenVibeVoiceManagerButton.IsEnabled = true;
        }
    }

    private async Task<string> ProbeVibeVoiceServiceAsync(CancellationToken cancellationToken)
    {
        var baseUrl = ValidateVibeVoiceServiceUrl(VibeVoiceServiceUrlTextBox.Text);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseUrl.TrimEnd('/') + "/api/v1/status"));
        if (!string.IsNullOrWhiteSpace(VibeVoiceApiKeyPasswordBox.Password))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                VibeVoiceApiKeyPasswordBox.Password.Trim());
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _providerHttpClient.SendAsync(request, timeout.Token);
        var payload = await response.Content.ReadAsStringAsync(timeout.Token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(payload);
        var status = document.RootElement.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString() ?? "unknown"
            : "unknown";
        var backend = document.RootElement.TryGetProperty("backend", out var backendElement)
            ? backendElement.GetString() ?? "unknown"
            : "unknown";
        return $"VibeVoice · {backend} · {status}";
    }

    private async Task<bool> IsVibeVoiceServiceReachableAsync(CancellationToken cancellationToken)
    {
        var baseUrl = ValidateVibeVoiceServiceUrl(VibeVoiceServiceUrlTextBox.Text);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _providerHttpClient.GetAsync(
                new Uri(baseUrl.TrimEnd('/') + "/health"),
                timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static void StartBundledVibeVoiceService(string listenUrl)
    {
        var serviceDirectory = Path.Combine(AppContext.BaseDirectory, "vibevoice-service");
        var executable = Path.Combine(
            serviceDirectory,
            "SteamVRTranslator.VibeVoice.Server.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "The bundled VibeVoice service is missing. Rebuild or reinstall the complete application package.",
                executable);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = serviceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--VibeVoiceService:ListenUrl");
        startInfo.ArgumentList.Add(listenUrl);
        Process.Start(startInfo);
    }

    private async void DownloadSubtitleModelsButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadSubtitleModelsButton.IsEnabled = false;
        DownloadPanel.Visibility = Visibility.Visible;
        try
        {
            await _subtitleModelDownloads.DownloadAsync(HfMirrorCheckBox.IsChecked == true);
            UpdateSubtitleModelStatus();
            _log.Info("[subtitles] 说话人分段和声纹模型已下载并校验。");
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = T("Download.Cancelled");
            _log.Info("[subtitles] 说话人模型下载已取消。");
        }
        catch (Exception exception)
        {
            _log.Error("[subtitles] 说话人模型下载失败。", exception);
            MessageBox.Show(
                this,
                exception.Message,
                T("Dialog.DownloadFailed"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            DownloadSubtitleModelsButton.IsEnabled = true;
            await Task.Delay(500);
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void ReplaySubtitleFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_configuration.Subtitles.Enabled)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = T("Subtitle.Replay.Select"),
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ApplySubtitleControlsToConfiguration();
            _configuration.ApplyPromptLanguage();
            SaveConfigurationSafely("实验性字幕文件重放");
            var session = EnsureSubtitleSession();
            var window = EnsureSubtitleHistoryWindow();
            window.Show();
            window.Activate();
            _subtitleReplayCancellation?.Dispose();
            _subtitleReplayCancellation = new CancellationTokenSource();
            ReplaySubtitleFileButton.IsEnabled = false;
            CancelSubtitleReplayButton.IsEnabled = true;
            SubtitleReplayProgressBar.Value = 0;
            SubtitleReplayProgressBar.Visibility = Visibility.Visible;
            SubtitleReplayStatusText.Foreground = new SolidColorBrush(Color.FromRgb(82, 96, 90));
            SubtitleReplayStatusText.Text = AppLocalization.Format(
                "Subtitle.Replay.Running",
                Path.GetFileName(dialog.FileName));
            var progress = new Progress<double>(value =>
                SubtitleReplayProgressBar.Value = Math.Clamp(value * 100, 0, 100));
            var result = await session.ReplayFileAsync(
                dialog.FileName,
                progress,
                _subtitleReplayCancellation.Token);
            SubtitleReplayStatusText.Text = AppLocalization.Format(
                "Subtitle.Replay.Complete",
                result.RecognizedCount,
                result.SegmentCount,
                result.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            SubtitleReplayStatusText.Text = T("Subtitle.Replay.Cancelled");
        }
        catch (Exception exception)
        {
            _log.Error("[subtitles] 音频文件重放失败。", exception);
            SubtitleReplayStatusText.Foreground = new SolidColorBrush(Color.FromRgb(177, 47, 52));
            SubtitleReplayStatusText.Text = exception.Message;
            MessageBox.Show(
                this,
                exception.Message,
                T("Subtitle.Replay.Failed"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _subtitleReplayCancellation?.Dispose();
            _subtitleReplayCancellation = null;
            ReplaySubtitleFileButton.IsEnabled = _configuration.Subtitles.Enabled;
            CancelSubtitleReplayButton.IsEnabled = false;
            SubtitleReplayProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelSubtitleReplayButton_Click(object sender, RoutedEventArgs e) =>
        _subtitleReplayCancellation?.Cancel();

    private void OpenSubtitleHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_configuration.Subtitles.Enabled)
        {
            return;
        }

        var window = EnsureSubtitleHistoryWindow();
        window.Show();
        window.Activate();
    }

    private async void OpenSubtitleHistoryVrButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || !_configuration.Subtitles.Enabled)
        {
            return;
        }

        OpenSubtitleHistoryVrButton.IsEnabled = false;
        try
        {
            if (_subtitleOverlayId is { } overlayId)
            {
                await _runtime.CloseWindowAsync(overlayId);
                _subtitleOverlayId = null;
                return;
            }

            await ShowSubtitleHistoryVrAsync(WpfSpatialOverlayPlacement.Head);
        }
        catch (Exception exception)
        {
            _log.Error("[subtitles] 切换 VR 字幕历史窗口失败。", exception);
            MessageBox.Show(
                this,
                exception.Message,
                T("Dialog.SpatialWindowFailed"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            OpenSubtitleHistoryVrButton.IsEnabled =
                _runtime is not null && _configuration.Subtitles.Enabled;
        }
    }

    private void OnSubtitlePanelRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (!_configuration.Subtitles.Enabled)
                {
                    _log.Warning("[subtitles] 已忽略未启用实验性功能的 VR 控制请求。");
                    return;
                }

                EnsureSubtitleSession().ApplyConfiguration(_configuration);
                await ShowSubtitleHistoryVrAsync(WpfSpatialOverlayPlacement.LeftHand);
            }
            catch (Exception exception)
            {
                _log.Error("[subtitles] VR 控制面板打开字幕窗口失败。", exception);
                StatusText.Text = exception.Message;
                StatusText.Visibility = Visibility.Visible;
            }
        }));
    }

    private void OnSubtitleControlRequested(
        object? sender,
        VrSubtitleControlRequestEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (!_configuration.Subtitles.Enabled)
                {
                    _log.Warning("[subtitles] 已忽略未启用实验性功能的 VR 控制请求。");
                    return;
                }

                if (e.Kind == VrSubtitleControlRequestKind.ConfigurationChanged)
                {
                    await ApplySubtitleControlPanelConfigurationAsync(e.State);
                    return;
                }

                if (e.Kind == VrSubtitleControlRequestKind.OpenWindow)
                {
                    EnsureSubtitleSession().ApplyConfiguration(_configuration);
                    await ShowSubtitleHistoryVrAsync(WpfSpatialOverlayPlacement.LeftHand);
                    PushSubtitleControlState();
                }
            }
            catch (Exception exception)
            {
                _log.Error("[subtitles] VR 控制面板操作失败。", exception);
                StatusText.Text = exception.Message;
                StatusText.Visibility = Visibility.Visible;
                PushSubtitleControlState(exception.Message);
            }
        }));
    }

    private async Task ApplySubtitleControlPanelConfigurationAsync(VrSubtitleControlState state)
    {
        var session = EnsureSubtitleSession();
        var restartListening = session.IsListening;
        if (restartListening)
        {
            await session.StopListeningAsync();
        }

        var subtitles = _configuration.Subtitles;
        subtitles.AsrBackend = SubtitleAsrBackends.Normalize(state.AsrBackend);
        subtitles.ShowOriginalText = state.ShowOriginalText;
        subtitles.TranslateText = state.TranslateText;
        subtitles.TargetLanguage = NormalizeSubtitleTargetLanguage(state.TargetLanguage);
        subtitles.UseSpeakerColors = state.UseSpeakerColors;
        subtitles.Diarization.Enabled = state.DiarizationEnabled;
        subtitles.Diarization.CpuThreadCount = int.Parse(
            NormalizeSubtitleThreadSelection(state.DiarizationCpuThreadCount));

        _applyingVrControlPanelSettings = true;
        try
        {
            SelectByTag(SubtitleAsrBackendComboBox, subtitles.AsrBackend);
            SubtitleShowOriginalCheckBox.IsChecked = subtitles.ShowOriginalText;
            SubtitleTranslateCheckBox.IsChecked = subtitles.TranslateText;
            SubtitleSpeakerColorsCheckBox.IsChecked = subtitles.UseSpeakerColors;
            SelectByTag(SubtitleTargetLanguageComboBox, subtitles.TargetLanguage);
            SubtitleDiarizationEnabledCheckBox.IsChecked = subtitles.Diarization.Enabled;
            SelectByTag(
                SubtitleDiarizationThreadsComboBox,
                subtitles.Diarization.CpuThreadCount.ToString());
            UpdateSubtitleAsrPanel();
        }
        finally
        {
            _applyingVrControlPanelSettings = false;
        }
        session.ApplyConfiguration(_configuration);
        SaveConfigurationSafely("VR 控制面板字幕设置");

        if (restartListening && _runtime is not null)
        {
            await ValidateSubtitleExperimentalEnvironmentAsync();
            await session.StartListeningAsync(() => _runtime?.CurrentSceneProcessId ?? 0);
        }
        PushSubtitleControlState();
    }

    private async Task ShowSubtitleHistoryVrAsync(WpfSpatialOverlayPlacement placement)
    {
        if (_runtime is null || _subtitleOverlayId is not null)
        {
            return;
        }

        var window = EnsureSubtitleHistoryWindow();
        _subtitleOverlayId = await _runtime.ShowWindowAsync(
            window,
            CreateSubtitleOverlayOptions(_configuration.Subtitles, placement));
        PushSubtitleControlState();
    }

    internal static WpfSpatialOverlayOptions CreateSubtitleOverlayOptions(
        SubtitleConfiguration configuration,
        WpfSpatialOverlayPlacement placement) => new()
        {
            Name = "SteamVR Translator Subtitle History",
            WidthMeters = (float)configuration.WindowWidthMeters,
            DistanceMeters = (float)configuration.WindowDistanceMeters,
            Placement = placement,
            CanGrab = true,
            ShowToolbarWhenGrabbed = false,
            MaximumFramesPerSecond = 60
        };

    private SubtitleSessionController EnsureSubtitleSession()
    {
        if (_subtitleSession is null)
        {
            _subtitleSession = new SubtitleSessionController(
                _configuration,
                _log,
                _providerHttpClient);
            _subtitleSession.ListeningStateChanged += OnSubtitleListeningStateChanged;
        }
        return _subtitleSession;
    }

    private SubtitleHistoryWindow EnsureSubtitleHistoryWindow()
    {
        var session = EnsureSubtitleSession();
        if (_subtitleHistoryWindow is null)
        {
            _subtitleHistoryWindow = new SubtitleHistoryWindow(session.History)
            {
                Owner = this,
                Opacity = _configuration.Subtitles.WindowOpacity
            };
            _subtitleHistoryWindow.CloseRequested += OnSubtitleHistoryCloseRequested;
            _subtitleHistoryWindow.StartStopListeningRequested +=
                OnSubtitleStartStopListeningRequested;
            _subtitleHistoryWindow.ApplyListeningState(
                session.ListeningState,
                SubtitleListeningStatusText(session.ListeningState));
        }

        return _subtitleHistoryWindow;
    }

    private async void OnSubtitleStartStopListeningRequested(object? sender, EventArgs e)
    {
        try
        {
            var session = EnsureSubtitleSession();
            if (session.IsListening)
            {
                await session.StopListeningAsync();
            }
            else
            {
                if (_runtime is null)
                {
                    throw new InvalidOperationException(T("Dialog.NotStarted.Message"));
                }
                ApplySubtitleControlsToConfiguration();
                session.ApplyConfiguration(_configuration);
                SaveConfigurationSafely("开始实时字幕监听");
                await session.StartListeningAsync(() => _runtime?.CurrentSceneProcessId ?? 0);
            }
            PushSubtitleControlState();
        }
        catch (Exception exception)
        {
            _log.Error("[subtitles] 切换实时监听失败。", exception);
            _subtitleHistoryWindow?.ApplyListeningState(
                SubtitleListeningState.Error,
                exception.Message);
            PushSubtitleControlState(exception.Message);
        }
    }

    private void OnSubtitleListeningStateChanged(
        object? sender,
        SubtitleListeningStateChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var displayMessage = e.State == SubtitleListeningState.Error
                ? e.Message
                : SubtitleListeningStatusText(e.State);
            _subtitleHistoryWindow?.ApplyListeningState(e.State, displayMessage);
            SubtitleReplayStatusText.Text = displayMessage;
            PushSubtitleControlState(displayMessage);
        });
    }

    private void PushSubtitleControlState(string? status = null)
    {
        var runtime = _runtime;
        if (runtime is null)
        {
            return;
        }
        var session = _subtitleSession;
        runtime.UpdateSubtitleControlState(new VrSubtitleControlState(
            _configuration.Subtitles.AsrBackend,
            _configuration.Subtitles.ShowOriginalText,
            _configuration.Subtitles.TranslateText,
            _configuration.Subtitles.TargetLanguage,
            _configuration.Subtitles.UseSpeakerColors,
            _configuration.Subtitles.Diarization.Enabled,
            _configuration.Subtitles.Diarization.CpuThreadCount,
            _subtitleOverlayId is not null,
            session?.IsListening == true,
            status ?? SubtitleListeningStatusText(
                session?.ListeningState ?? SubtitleListeningState.Stopped)));
    }

    private static string SubtitleListeningStatusText(SubtitleListeningState state) => state switch
    {
        SubtitleListeningState.WaitingForProcess =>
            AppLocalization.Text("Subtitle.Listening.Waiting"),
        SubtitleListeningState.Starting =>
            AppLocalization.Text("Subtitle.Listening.Starting"),
        SubtitleListeningState.Listening =>
            AppLocalization.Text("Subtitle.Listening.Active"),
        SubtitleListeningState.Error =>
            AppLocalization.Text("Subtitle.Listening.Error"),
        _ => AppLocalization.Text("Subtitle.Listening.Stopped")
    };

    private static string NormalizeSubtitleTargetLanguage(string? language) =>
        string.Equals(language, "ja-JP", StringComparison.OrdinalIgnoreCase)
            ? "ja-JP"
            : string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase)
                ? "en-US"
                : "zh-CN";

    private async void OnSubtitleHistoryCloseRequested(object? sender, EventArgs e)
    {
        var overlayId = _subtitleOverlayId;
        _subtitleOverlayId = null;
        try
        {
            if (_runtime is not null && overlayId is { } id)
            {
                await _runtime.CloseWindowAsync(id);
            }
        }
        catch (Exception exception)
        {
            _log.Error("[subtitles] 从字幕窗口关闭 VR Overlay 失败。", exception);
        }
        finally
        {
            _subtitleHistoryWindow?.Hide();
            PushSubtitleControlState();
        }
    }

    private void ApplySubtitleControlsToConfiguration()
    {
        var updated = ReadSubtitleControls();
        var target = _configuration.Subtitles;
        target.Enabled = updated.Enabled;
        target.AsrBackend = updated.AsrBackend;
        target.VibeVoiceServiceUrl = updated.VibeVoiceServiceUrl;
        target.VibeVoiceApiKey = updated.VibeVoiceApiKey;
        target.ShowOriginalText = updated.ShowOriginalText;
        target.TranslateText = updated.TranslateText;
        target.TargetLanguage = updated.TargetLanguage;
        target.MaximumHistoryEntries = updated.MaximumHistoryEntries;
        target.MaximumHistoryCharacters = updated.MaximumHistoryCharacters;
        target.UseSpeakerColors = updated.UseSpeakerColors;
        target.Diarization.Enabled = updated.Diarization.Enabled;
        target.Diarization.CpuThreadCount = updated.Diarization.CpuThreadCount;
        target.Diarization.ClusteringThreshold = updated.Diarization.ClusteringThreshold;
        target.TranslationSystemPrompt = SubtitleTranslationSystemPromptTextBox.Text;
        target.TranslationPrompt = SubtitleTranslationPromptTextBox.Text;
        if (_subtitleHistoryWindow is not null)
        {
            _subtitleHistoryWindow.Opacity = target.WindowOpacity;
        }
    }

    private SubtitleConfiguration ReadSubtitleControls()
    {
        if (!int.TryParse(SubtitleHistoryEntriesTextBox.Text, out var maximumEntries) ||
            maximumEntries is < 10 or > 1000)
        {
            throw new InvalidOperationException(T("Subtitle.Validation.HistoryEntries"));
        }
        if (!int.TryParse(SubtitleHistoryCharactersTextBox.Text, out var maximumCharacters) ||
            maximumCharacters is < 1000 or > 500000)
        {
            throw new InvalidOperationException(T("Subtitle.Validation.HistoryCharacters"));
        }
        if (!double.TryParse(
                SubtitleDiarizationThresholdTextBox.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var threshold) || threshold is < 0.1 or > 1.5)
        {
            throw new InvalidOperationException(T("Subtitle.Validation.Threshold"));
        }

        return new SubtitleConfiguration
        {
            Enabled = SubtitleEnabledCheckBox.IsChecked == true,
            AsrBackend = SubtitleAsrBackends.Normalize(SelectedTag(SubtitleAsrBackendComboBox)),
            VibeVoiceServiceUrl = ValidateVibeVoiceServiceUrl(VibeVoiceServiceUrlTextBox.Text),
            VibeVoiceApiKey = VibeVoiceApiKeyPasswordBox.Password,
            ShowOriginalText = SubtitleShowOriginalCheckBox.IsChecked == true,
            TranslateText = SubtitleTranslateCheckBox.IsChecked == true,
            TargetLanguage = SelectedTag(SubtitleTargetLanguageComboBox),
            MaximumHistoryEntries = maximumEntries,
            MaximumHistoryCharacters = maximumCharacters,
            WindowWidthMeters = _configuration.Subtitles.WindowWidthMeters,
            WindowDistanceMeters = _configuration.Subtitles.WindowDistanceMeters,
            WindowOpacity = _configuration.Subtitles.WindowOpacity,
            UseSpeakerColors = SubtitleSpeakerColorsCheckBox.IsChecked == true,
            TranslationSystemPrompt = SubtitleTranslationSystemPromptTextBox.Text,
            TranslationPrompt = SubtitleTranslationPromptTextBox.Text,
            Diarization = new SubtitleDiarizationConfiguration
            {
                Enabled = SubtitleDiarizationEnabledCheckBox.IsChecked == true,
                CpuThreadCount = int.Parse(SelectedTag(SubtitleDiarizationThreadsComboBox)),
                ClusteringThreshold = threshold,
                SegmentationModelPath = _configuration.Subtitles.Diarization.SegmentationModelPath,
                EmbeddingModelPath = _configuration.Subtitles.Diarization.EmbeddingModelPath
            }
        };
    }

    private void UpdateSubtitleModelStatus()
    {
        var statuses = _subtitleModelDownloads.GetStatuses();
        var installed = statuses.Count(status => status.Installed);
        SubtitleModelStatusText.Text = installed == statuses.Count
            ? T("Subtitle.Diarization.Installed")
            : AppLocalization.Format(
                "Subtitle.Diarization.Missing",
                string.Join(", ", statuses.Where(status => !status.Installed).Select(status => status.DisplayName)));
        SubtitleModelStatusText.Foreground = installed == statuses.Count
            ? new SolidColorBrush(Color.FromRgb(8, 126, 114))
            : new SolidColorBrush(Color.FromRgb(154, 90, 0));
    }

    private static string NormalizeSubtitleThreadSelection(int value)
    {
        int[] available = [1, 2, 4, 6, 8, 12, 16];
        return available.OrderBy(candidate => Math.Abs(candidate - value)).First().ToString();
    }

    private static string ValidateVibeVoiceServiceUrl(string? value)
    {
        var normalized = value?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(T("Subtitle.Validation.VibeVoiceUrl"));
        }

        return uri.ToString().TrimEnd('/');
    }

    private void ShowConfigurationError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(177, 47, 52));
        RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(177, 47, 52));
    }

    private static void ValidateActiveProvider(TranslationConfiguration translation)
    {
        var activeProvider = translation.GetActiveProvider();
        if (activeProvider is null)
        {
            throw new InvalidOperationException(T("Validation.ProviderRequired"));
        }

        if (string.Equals(
                activeProvider.Type,
                TranslationProviderConfiguration.OpenAiCompatibleType,
                StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(activeProvider.BaseUrl) || string.IsNullOrWhiteSpace(activeProvider.Model))
            {
                throw new InvalidOperationException(T("Validation.ProviderEndpoint"));
            }

            return;
        }

        if (!activeProvider.IsMock)
        {
            throw new InvalidOperationException(AppLocalization.Format("Validation.ProviderUnsupported", activeProvider.Type));
        }
    }

    private static string DescribeActiveProvider(TranslationConfiguration translation)
    {
        var provider = translation.GetActiveProvider();
        return provider is null ? "<缺失>" : $"{provider.DisplayName} ({provider.Type})";
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items[0];
    }

    private static string SelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
        ?? throw new InvalidOperationException(T("Validation.ComboSelection"));

    private int SelectedPointerSmoothingStrength() =>
        int.TryParse(SelectedTag(PointerSmoothingComboBox), out var strength)
            ? AppConfiguration.NormalizePointerSmoothingStrength(strength)
            : AppConfiguration.DefaultPointerSmoothingStrength;

    private static int PointerSmoothingPreset(int strength) =>
        new[] { 0, 25, 50, 75, 100 }
            .OrderBy(value => Math.Abs(value - strength))
            .First();

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCaptureEye(string value) =>
        string.Equals(value, "right-eye", StringComparison.OrdinalIgnoreCase) ? "right-eye" : "left-eye";

    private static void RequireExistingFile(string configuredPath, string description)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(AppLocalization.Format("Validation.PathMissing", description));
        }

        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
        if (!File.Exists(Path.GetFullPath(path)))
        {
            throw new InvalidOperationException(AppLocalization.Format("Validation.FileMissing", description));
        }
    }

    private static string ResolveCaptureDirectory(AppConfiguration configuration) =>
        ApplicationDataPaths.ResolveCaptureDirectory(configuration.CaptureDirectory);

    private static string T(string key) => AppLocalization.Text(key);
}

internal static class ProviderModelSearch
{
    public static bool Matches(string? model, string? filter) =>
        !string.IsNullOrWhiteSpace(model) &&
        (string.IsNullOrWhiteSpace(filter) ||
         model.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));
}

internal sealed record PromptProviderOption(string? ProviderId, string DisplayName);

internal sealed record AsrEngineOption(
    string Backend,
    int? DeviceIndex,
    string? DeviceName,
    string DisplayName)
{
    public static AsrEngineOption Cpu { get; } = new("cpu", null, null, "CPU");

    public bool IsVulkan => string.Equals(Backend, "vulkan", StringComparison.OrdinalIgnoreCase);
}

public sealed class ProviderActiveVisibilityConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        System.Globalization.CultureInfo culture) =>
        values.Length >= 2 &&
        string.Equals(
            values[0]?.ToString(),
            values[1]?.ToString(),
            StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
