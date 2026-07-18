using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Input;
using SteamVRTranslator.App.Speech;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Translation;

namespace SteamVRTranslator.App;

public partial class MainWindow : Window
{
    private readonly ConfigurationStore _configurationStore = new();
    private readonly GlobalHotKey _hotKey = new();
    private readonly AppLog _log;
    private readonly HttpClient _providerHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly OpenAiCompatibleProviderClient _providerClient;
    private readonly SenseVoiceDownloadService _senseVoiceDownloads;
    private AppConfiguration _configuration;
    private List<TranslationProviderConfiguration> _providers = [];
    private SteamVrTranslationRuntime? _runtime;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _populatingProvider;

    public MainWindow()
    {
        InitializeComponent();
        _log = new AppLog(AppContext.BaseDirectory);
        _providerClient = new OpenAiCompatibleProviderClient(_providerHttpClient);
        _senseVoiceDownloads = new SenseVoiceDownloadService(AppContext.BaseDirectory);
        try
        {
            _configuration = _configurationStore.Load();
        }
        catch (Exception exception)
        {
            _configuration = new AppConfiguration();
            _log.Error("配置文件无法读取，已回退到默认配置。", exception);
        }

        _log.MessageWritten += OnLogMessageWritten;
        _senseVoiceDownloads.ProgressChanged += OnSenseVoiceDownloadProgressChanged;
        _hotKey.Pressed += (_, _) => _runtime?.RequestButtonDown("桌面全局热键");
        _hotKey.Released += (_, _) => _runtime?.RequestButtonUp("桌面全局热键");
        PopulateControls();
        Loaded += OnLoaded;
        _log.Info($"控制窗口已启动。版本={typeof(MainWindow).Assembly.GetName().Version}");
        _log.Info($"程序目录：{AppContext.BaseDirectory}");
        _log.Info($"配置文件：{_configurationStore.FilePath}");
        _log.Info($"日志文件：{_log.FilePath}");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose || _runtime is null)
        {
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
        _hotKey.Dispose();
        _providerHttpClient.Dispose();
        _senseVoiceDownloads.ProgressChanged -= OnSenseVoiceDownloadProgressChanged;
        _senseVoiceDownloads.Dispose();
        _log.MessageWritten -= OnLogMessageWritten;
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetActivePage("capture");
        UpdateAsrInstallStatus();
        _log.Info("主窗口已完成加载，准备注册桌面热键。");
        try
        {
            _hotKey.Register(this, _configuration.HotKeyVirtualKey);
            _log.Info($"桌面热键注册成功：VK=0x{_configuration.HotKeyVirtualKey:X2}");
        }
        catch (Exception exception)
        {
            _log.Error("注册桌面翻译热键失败。", exception);
            MessageBox.Show(this, exception.Message, "热键不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_runtime is not null)
            {
                await StopRuntimeAsync();
                return;
            }

            _configuration = ReadControls();
            _configurationStore.Save(_configuration);
            _log.Info(
                $"启动请求：捕获源=SteamVR Compositor 单眼纹理，" +
                $"Provider={DescribeActiveProvider(_configuration.Translation)}，" +
                $"VRChat语音={_configuration.VrChatVoiceInput.Enabled}，" +
                $"截图目录={ResolveCaptureDirectory(_configuration)}");
            _hotKey.Register(this, _configuration.HotKeyVirtualKey);
            _runtime = new SteamVrTranslationRuntime(_configuration, _log);
            _runtime.StatusChanged += OnRuntimeStatusChanged;
            await _runtime.StartAsync();
            StartButtonText.Text = "停止服务";
            StartButtonIcon.Data = MaterialIconPaths.Stop;
            TestSelectionButton.IsEnabled = true;
            BindingsButton.IsEnabled = true;
            SetConfigurationControlsEnabled(false);
        }
        catch (Exception exception)
        {
            _log.Error("启动失败。", exception);
            MessageBox.Show(this, exception.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TestSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            MessageBox.Show(this, "请先启动 SteamVR 模块。", "尚未启动", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _runtime.RequestToggle("控制窗口模拟左摇杆");
    }

    private void BindingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            MessageBox.Show(this, "请先启动并连接 SteamVR。", "尚未连接", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _runtime.RequestOpenBindings();
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
        CapturePage.Visibility = page == "capture" ? Visibility.Visible : Visibility.Collapsed;
        ProvidersPage.Visibility = page == "providers" ? Visibility.Visible : Visibility.Collapsed;
        VoicePage.Visibility = page == "voice" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;

        var buttons = new[] { CaptureNavButton, ProviderNavButton, VoiceNavButton, DiagnosticsNavButton };
        foreach (var button in buttons)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = new SolidColorBrush(Color.FromRgb(201, 210, 204));
        }

        var active = page switch
        {
            "providers" => ProviderNavButton,
            "voice" => VoiceNavButton,
            "diagnostics" => DiagnosticsNavButton,
            _ => CaptureNavButton
        };
        active.Background = new SolidColorBrush(Color.FromRgb(49, 67, 59));
        active.Foreground = Brushes.White;

        (PageTitleText.Text, PageContextText.Text) = page switch
        {
            "providers" => ("模型提供商", "模拟提供商、兼容端点、模型发现与启用切换"),
            "voice" => ("VRChat 语音", "SteamVR PTT、SenseVoice 与 OSC Chatbox"),
            "diagnostics" => ("诊断", "运行状态、下载和本地日志"),
            _ => ("图像翻译", "SteamVR 单眼捕获、空间框选与结果交互")
        };
    }

    private void AddProviderButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedProviderFromControls();
        var provider = new TranslationProviderConfiguration
        {
            Id = Guid.NewGuid().ToString("N"),
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
            MessageBox.Show(this, "内置模拟提供商不能删除。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var index = _providers.IndexOf(selected);
        _providers.Remove(selected);
        if (string.Equals(_configuration.Translation.ActiveProviderId, selected.Id, StringComparison.OrdinalIgnoreCase))
        {
            _configuration.Translation.ActiveProviderId = _providers[Math.Min(index, _providers.Count - 1)].Id;
        }

        RefreshProviderList(_providers[Math.Min(index, _providers.Count - 1)]);
    }

    private void ActivateProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected)
        {
            return;
        }

        UpdateSelectedProviderFromControls();
        _configuration.Translation.ActiveProviderId = selected.Id;
        ProviderListBox.Items.Refresh();
        UpdateProviderActiveState();
        if (!selected.IsMock)
        {
            ProviderConnectionText.Text = $"已启用 {selected.DisplayName}";
            ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(8, 126, 114));
        }
        UpdateActiveProviderSummary();
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
        ProviderConnectionText.Text = "配置已修改，请重新测试连接";
        ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
    }

    private void ProviderField_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_populatingProvider)
        {
            return;
        }

        UpdateSelectedProviderFromControls();
        ProviderConnectionText.Text = "密钥已修改，请重新测试连接";
        ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
    }

    private void ProviderModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingProvider || ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected || selected.IsMock)
        {
            return;
        }

        selected.Model = ProviderModelComboBox.SelectedItem?.ToString() ?? string.Empty;
        ProviderListBox.Items.Refresh();
        UpdateActiveProviderSummary();
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
        ProviderConnectionText.Text = "正在连接 GET /models...";
        ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
        try
        {
            var models = await _providerClient.GetModelsAsync(selected.BaseUrl, selected.ApiKey, CancellationToken.None);
            var previousModel = selected.Model;
            _populatingProvider = true;
            ProviderModelComboBox.ItemsSource = models;
            ProviderModelComboBox.SelectedItem = models.FirstOrDefault(model =>
                string.Equals(model, previousModel, StringComparison.OrdinalIgnoreCase)) ?? models[0];
            selected.Model = ProviderModelComboBox.SelectedItem?.ToString() ?? string.Empty;
            _populatingProvider = false;
            ProviderListBox.Items.Refresh();
            UpdateActiveProviderSummary();
            ProviderConnectionText.Text = $"连接成功，已获取 {models.Count} 个模型";
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

    private async void DownloadAsrButton_Click(object sender, RoutedEventArgs e)
    {
        var variant = SelectedTag(AsrVariantComboBox);
        DownloadAsrButton.IsEnabled = false;
        DownloadPanel.Visibility = Visibility.Visible;
        try
        {
            await _senseVoiceDownloads.DownloadBundleAsync(
                variant,
                HfMirrorCheckBox.IsChecked == true);
            SenseVoiceExecutableTextBox.Text = "runtimes/llama-funasr-sensevoice.exe";
            SenseVoiceModelTextBox.Text = variant == "q5_0"
                ? "models/sensevoice-small-q5_0.gguf"
                : "models/sensevoice-small-q8.gguf";
            SenseVoiceVadTextBox.Text = "models/fsmn-vad.gguf";
            UpdateAsrInstallStatus();
            _configuration.Speech.SenseVoiceExecutablePath = SenseVoiceExecutableTextBox.Text;
            _configuration.Speech.SenseVoiceModelPath = SenseVoiceModelTextBox.Text;
            _configuration.Speech.SenseVoiceVadModelPath = SenseVoiceVadTextBox.Text;
            _configuration.VrChatVoiceInput.DownloadSource =
                HfMirrorCheckBox.IsChecked == true ? "hf-mirror" : "official";
            _configurationStore.Save(_configuration);
            _log.Info($"[download] SenseVoice {variant} 已下载、启用并保存配置。");
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = "下载已取消";
            _log.Info("[download] SenseVoice 下载已取消。");
        }
        catch (Exception exception)
        {
            _log.Error("[download] SenseVoice 下载失败。", exception);
            MessageBox.Show(this, exception.Message, "下载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DownloadAsrButton.IsEnabled = _runtime is null;
            await Task.Delay(500);
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e) =>
        _senseVoiceDownloads.Cancel();

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
        if (_runtime is null)
        {
            return;
        }

        var runtime = _runtime;
        _log.Info("正在停止 SteamVR 模块。");
        _runtime = null;
        runtime.StatusChanged -= OnRuntimeStatusChanged;
        await runtime.DisposeAsync();
        StartButtonText.Text = "启动服务";
        StartButtonIcon.Data = MaterialIconPaths.Play;
        StateText.Text = "IDLE";
        StatusText.Text = "已停止";
        RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(157, 165, 159));
        TestSelectionButton.IsEnabled = false;
        BindingsButton.IsEnabled = false;
        SetConfigurationControlsEnabled(true);
        _log.Info("SteamVR 模块已停止。");
    }

    private void OnRuntimeStatusChanged(object? sender, SteamVrRuntimeEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = e.Message;
            StatusText.Foreground = e.IsError
                ? new SolidColorBrush(Color.FromRgb(177, 47, 52))
                : new SolidColorBrush(Color.FromRgb(82, 96, 90));
            RuntimeDot.Fill = e.IsError
                ? new SolidColorBrush(Color.FromRgb(177, 47, 52))
                : new SolidColorBrush(Color.FromRgb(8, 126, 114));
            StateText.Text = e.State.ToString().ToUpperInvariant();
        });
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
        CaptureSourceTextBox.Text = "SteamVR Compositor（单眼）";
        SelectByTag(StereoCompositionComboBox, NormalizeCaptureEye(_configuration.StereoCompositionMode));
        SelectByTag(HotKeyComboBox, _configuration.HotKeyVirtualKey.ToString());
        InvertResultScrollCheckBox.IsChecked = _configuration.InvertResultScroll;
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

        PopulateMicrophones();
        VoiceInputEnabledCheckBox.IsChecked = _configuration.VrChatVoiceInput.Enabled;
        OscHostTextBox.Text = _configuration.VrChatVoiceInput.Host;
        OscPortTextBox.Text = _configuration.VrChatVoiceInput.Port.ToString();
        OscSendImmediatelyCheckBox.IsChecked = _configuration.VrChatVoiceInput.SendImmediately;
        HfMirrorCheckBox.IsChecked = string.Equals(
            _configuration.VrChatVoiceInput.DownloadSource,
            "hf-mirror",
            StringComparison.OrdinalIgnoreCase);
        SenseVoiceExecutableTextBox.Text = _configuration.Speech.SenseVoiceExecutablePath;
        SenseVoiceModelTextBox.Text = _configuration.Speech.SenseVoiceModelPath;
        SenseVoiceVadTextBox.Text = _configuration.Speech.SenseVoiceVadModelPath ?? string.Empty;
        SelectByTag(
            AsrVariantComboBox,
            Path.GetFileName(_configuration.Speech.SenseVoiceModelPath).Contains("q5", StringComparison.OrdinalIgnoreCase)
                ? "q5_0"
                : "q8_0");
        TestSelectionButton.IsEnabled = false;
        BindingsButton.IsEnabled = false;
    }

    private AppConfiguration ReadControls()
    {
        UpdateSelectedProviderFromControls();
        var activeProvider = _providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, _configuration.Translation.ActiveProviderId, StringComparison.OrdinalIgnoreCase));
        if (activeProvider is null)
        {
            throw new InvalidOperationException("请先选择并启用一个模型提供商。");
        }

        if (string.Equals(
                activeProvider.Type,
                TranslationProviderConfiguration.OpenAiCompatibleType,
                StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(activeProvider.BaseUrl) || string.IsNullOrWhiteSpace(activeProvider.Model))
            {
                throw new InvalidOperationException("当前 Provider 需要填写 Base URL，并通过 /models 选择视觉模型。");
            }
        }
        else if (!activeProvider.IsMock)
        {
            throw new InvalidOperationException($"不支持的翻译提供商类型：{activeProvider.Type}");
        }

        if (!int.TryParse(OscPortTextBox.Text, out var oscPort) || oscPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("VRChat OSC 端口必须介于 1 和 65535 之间。");
        }

        var voiceEnabled = VoiceInputEnabledCheckBox.IsChecked == true;
        if (voiceEnabled)
        {
            RequireExistingFile(SenseVoiceExecutableTextBox.Text, "SenseVoice CPU 运行时");
            RequireExistingFile(SenseVoiceModelTextBox.Text, "SenseVoice 模型");
            if (!string.IsNullOrWhiteSpace(SenseVoiceVadTextBox.Text))
            {
                RequireExistingFile(SenseVoiceVadTextBox.Text, "FSMN VAD 模型");
            }
        }

        return new AppConfiguration
        {
            HotKeyVirtualKey = int.Parse(SelectedTag(HotKeyComboBox)),
            SelectionTimeoutSeconds = _configuration.SelectionTimeoutSeconds,
            CaptureDirectory = _configuration.CaptureDirectory,
            StereoCompositionMode = SelectedTag(StereoCompositionComboBox),
            InvertResultScroll = InvertResultScrollCheckBox.IsChecked == true,
            Translation = new TranslationConfiguration
            {
                ActiveProviderId = _configuration.Translation.ActiveProviderId,
                Providers = _providers.Select(CloneProvider).ToList(),
                TargetLanguage = SelectedTag(TargetLanguageComboBox),
                EnableStreaming = _configuration.Translation.EnableStreaming,
                DisableThinking = _configuration.Translation.DisableThinking,
                SystemPrompt = _configuration.Translation.SystemPrompt
            },
            Speech = new SpeechConfiguration
            {
                DeviceId = SelectedTag(MicrophoneComboBox),
                HoldThresholdMilliseconds = _configuration.Speech.HoldThresholdMilliseconds,
                MinimumDurationMilliseconds = _configuration.Speech.MinimumDurationMilliseconds,
                MaximumDurationSeconds = _configuration.Speech.MaximumDurationSeconds,
                SenseVoiceExecutablePath = SenseVoiceExecutableTextBox.Text.Trim(),
                SenseVoiceModelPath = SenseVoiceModelTextBox.Text.Trim(),
                SenseVoiceVadModelPath = NullIfWhiteSpace(SenseVoiceVadTextBox.Text),
                CustomCommandSystemPrompt = _configuration.Speech.CustomCommandSystemPrompt
            },
            VrChatVoiceInput = new VrChatVoiceInputConfiguration
            {
                Enabled = voiceEnabled,
                Host = OscHostTextBox.Text.Trim(),
                Port = oscPort,
                SendImmediately = OscSendImmediatelyCheckBox.IsChecked == true,
                MaxChatboxCharacters = _configuration.VrChatVoiceInput.MaxChatboxCharacters,
                DownloadSource = HfMirrorCheckBox.IsChecked == true ? "hf-mirror" : "official"
            }
        };
    }

    private void SetConfigurationControlsEnabled(bool enabled)
    {
        HotKeyComboBox.IsEnabled = enabled;
        StereoCompositionComboBox.IsEnabled = enabled;
        InvertResultScrollCheckBox.IsEnabled = enabled;
        ProviderListBox.IsEnabled = enabled;
        AddProviderButton.IsEnabled = enabled;
        DeleteProviderButton.IsEnabled = enabled &&
            ProviderListBox.SelectedItem is TranslationProviderConfiguration { IsMock: false };
        BaseUrlTextBox.IsEnabled = enabled;
        ApiKeyPasswordBox.IsEnabled = enabled;
        ProviderModelComboBox.IsEnabled = enabled;
        RefreshProviderButton.IsEnabled = enabled;
        ActivateProviderButton.IsEnabled = enabled;
        TargetLanguageComboBox.IsEnabled = enabled;
        VoiceInputEnabledCheckBox.IsEnabled = enabled;
        MicrophoneComboBox.IsEnabled = enabled;
        OscHostTextBox.IsEnabled = enabled;
        OscPortTextBox.IsEnabled = enabled;
        OscSendImmediatelyCheckBox.IsEnabled = enabled;
        AsrVariantComboBox.IsEnabled = enabled;
        HfMirrorCheckBox.IsEnabled = enabled;
        DownloadAsrButton.IsEnabled = enabled;
        SenseVoiceExecutableTextBox.IsEnabled = enabled;
        SenseVoiceModelTextBox.IsEnabled = enabled;
        SenseVoiceVadTextBox.IsEnabled = enabled;
        UpdateProviderActiveState();
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
    }

    private void PopulateSelectedProvider()
    {
        if (ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected)
        {
            return;
        }

        _populatingProvider = true;
        var isMock = selected.IsMock;
        ProviderEditorTitleText.Text = isMock ? "模拟提供商" : "OpenAI 兼容 Provider";
        ProviderEditorDescriptionText.Text = isMock
            ? "内置离线测试提供商，无需配置网络接口。"
            : "只需填写 Base URL 与 API Key，模型从 GET /models 获取。";
        MockProviderPanel.Visibility = isMock ? Visibility.Visible : Visibility.Collapsed;
        CompatibleProviderPanel.Visibility = isMock ? Visibility.Collapsed : Visibility.Visible;
        BaseUrlTextBox.Text = selected.BaseUrl;
        ApiKeyPasswordBox.Password = selected.ApiKey;
        ProviderModelComboBox.ItemsSource = string.IsNullOrWhiteSpace(selected.Model)
            ? Array.Empty<string>()
            : new[] { selected.Model };
        ProviderModelComboBox.SelectedItem = selected.Model;
        _populatingProvider = false;
        if (!isMock)
        {
            ProviderConnectionText.Text = "尚未测试连接";
            ProviderConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 106));
        }
        DeleteProviderButton.IsEnabled = !isMock && _runtime is null;
        UpdateProviderActiveState();
    }

    private void UpdateSelectedProviderFromControls()
    {
        if (_populatingProvider ||
            ProviderListBox.SelectedItem is not TranslationProviderConfiguration selected ||
            selected.IsMock)
        {
            return;
        }

        selected.BaseUrl = BaseUrlTextBox.Text.Trim();
        selected.ApiKey = ApiKeyPasswordBox.Password;
        selected.Model = ProviderModelComboBox.SelectedItem?.ToString() ?? selected.Model;
    }

    private void UpdateProviderActiveState()
    {
        var isActive = ProviderListBox.SelectedItem is TranslationProviderConfiguration selected &&
                       string.Equals(selected.Id, _configuration.Translation.ActiveProviderId, StringComparison.OrdinalIgnoreCase);
        ActivateProviderButtonText.Text = isActive ? "当前启用" : "设为启用";
        ActivateProviderButton.IsEnabled = !isActive && _runtime is null;
    }

    private void UpdateActiveProviderSummary()
    {
        var active = _providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, _configuration.Translation.ActiveProviderId, StringComparison.OrdinalIgnoreCase));
        ActiveProviderSummaryText.Text = active is null
            ? "当前启用：未配置"
            : $"当前启用：{active.DisplayName}";
    }

    private void UpdateAsrInstallStatus()
    {
        if (AsrVariantComboBox.SelectedItem is null)
        {
            return;
        }

        var statuses = _senseVoiceDownloads.GetStatuses(SelectedTag(AsrVariantComboBox));
        var installed = statuses.Count(status => status.Installed);
        AsrInstallStatusText.Text = installed == statuses.Count
            ? $"已安装：{string.Join("、", statuses.Select(status => status.DisplayName))}"
            : $"缺少：{string.Join("、", statuses.Where(status => !status.Installed).Select(status => status.DisplayName))}";
        AsrInstallStatusText.Foreground = installed == statuses.Count
            ? new SolidColorBrush(Color.FromRgb(8, 126, 114))
            : new SolidColorBrush(Color.FromRgb(154, 90, 0));
    }

    private void PopulateMicrophones()
    {
        MicrophoneComboBox.Items.Clear();
        MicrophoneComboBox.Items.Add(new ComboBoxItem { Content = "Windows 默认通讯设备", Tag = string.Empty });
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

    private static TranslationProviderConfiguration CloneProvider(TranslationProviderConfiguration provider) =>
        new()
        {
            Id = provider.Id,
            Type = provider.Type,
            BaseUrl = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            Model = provider.Model
        };

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
        ?? throw new InvalidOperationException("下拉选项未选择。");

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCaptureEye(string value) =>
        string.Equals(value, "right-eye", StringComparison.OrdinalIgnoreCase) ? "right-eye" : "left-eye";

    private static void RequireExistingFile(string configuredPath, string description)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException($"{description}尚未配置。");
        }

        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
        if (!File.Exists(Path.GetFullPath(path)))
        {
            throw new InvalidOperationException($"{description}尚未安装，请在 VRChat 语音页面下载后再启动。");
        }
    }

    private static string ResolveCaptureDirectory(AppConfiguration configuration) =>
        Path.IsPathRooted(configuration.CaptureDirectory)
            ? configuration.CaptureDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuration.CaptureDirectory));
}
