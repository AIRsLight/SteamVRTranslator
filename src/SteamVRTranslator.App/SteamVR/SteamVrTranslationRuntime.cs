using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Interaction;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.Output;
using SteamVRTranslator.App.Speech;
using SteamVRTranslator.App.Translation;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Valve.VR;

namespace SteamVRTranslator.App.SteamVR;

public sealed class SteamVrTranslationRuntime : IAsyncDisposable, IWpfSpatialOverlayHost
{
    private const string GlobalActionSetPath = "/actions/global";
    private const string SelectionActionSetPath = "/actions/selection";
    private const string ResultsActionSetPath = "/actions/results";
    private const string VoiceInputActionSetPath = "/actions/voiceinput";
    private const string ToggleActionPath = "/actions/global/in/toggle";
    private const string LeftTriggerActionPath = "/actions/selection/in/left_trigger";
    private const string RightTriggerActionPath = "/actions/selection/in/right_trigger";
    private const string LeftHapticActionPath = "/actions/selection/out/left_haptic";
    private const string RightHapticActionPath = "/actions/selection/out/right_haptic";
    private const string ResultScrollActionPath = "/actions/results/in/scroll";
    private const string OverlayToggleActionPath = "/actions/global/in/toggle_visibility";
    private const string LeftGripActionPath = "/actions/results/in/left_grip";
    private const string RightGripActionPath = "/actions/results/in/right_grip";
    private const string LeftPointerClickActionPath = "/actions/results/in/left_pointer_click";
    private const string RightPointerClickActionPath = "/actions/results/in/right_pointer_click";
    private const string VoiceInputPttActionPath = "/actions/voiceinput/in/ptt";
    private const int VoiceInputReleaseDebounceMilliseconds = 60;
    private const string OverlayCommandButtonSource = "截图工具栏录音按钮";
    private const string SelectionOverlayKey = "io.steamvrtranslator.selection";
    private const float InstructionPlaneDistance = 1.0f;
    private const float ResultPlaneDistance = 0.6f;
    private const float InstructionPlaneWidth = 1.1f;
    private const float VideoLayerOffset = -0.0002f;
    private const float ChromeLayerOffset = 0.0004f;
    private const float ProgressLayerOffset = 0.0008f;
    private const float PointerLayerOffset = 0.0012f;
    private const int ReconnectDelayMilliseconds = 2000;
    private const int RuntimeProbeDelayMilliseconds = 500;
    private const int RuntimeFramesPerSecond = 120;
    private const int DirectPixelRuntimeFramesPerSecond = 180;
    private static readonly TimeSpan WpfFrameIntervalTolerance = TimeSpan.FromMilliseconds(2);
    internal static readonly TimeSpan SingleOverlayCloseHoldDuration = TimeSpan.FromSeconds(2d / 3d);
    private static readonly TimeSpan ControlPanelHoldDuration = TimeSpan.FromMilliseconds(650);

    internal const EVRApplicationType OpenVrApplicationType =
        EVRApplicationType.VRApplication_Background;

    private readonly AppConfiguration _configuration;
    private readonly AppLog _log;
    private readonly TranslationPipeline _pipeline;
    private readonly Lazy<HtmlResultRenderer> _htmlResultRenderer;
    private readonly OverlayRenderer _renderer = new();
    private readonly SelectionStateMachine _selection;
    private readonly List<InteractiveOverlay> _interactiveOverlays = [];
    private readonly ConcurrentQueue<ResultProgressUpdate> _resultUpdates = new();
    private readonly ConcurrentQueue<CommandButtonEvent> _commandButtonEvents = new();
    private readonly ConcurrentQueue<WpfOverlayCommand> _wpfOverlayCommands = new();
    private readonly ConcurrentQueue<DiagnosticWpfPointerCommand> _diagnosticWpfPointerCommands = new();
    private readonly ConcurrentQueue<DiagnosticWpfInteractionCommand> _diagnosticWpfInteractionCommands = new();
    private readonly ConcurrentQueue<DiagnosticResultOverlayCommand> _diagnosticResultOverlayCommands = new();
    private readonly HashSet<long> _diagnosticPressedOverlayIds = [];
    private readonly Dictionary<long, List<PendingDiagnosticWpfInteraction>> _pendingDiagnosticWpfInteractions = [];
    private readonly ConcurrentQueue<VrControlPanelState> _controlPanelSettings = new();
    private readonly ConcurrentQueue<VrControlPanelAction> _controlPanelActions = new();
    private readonly Dictionary<long, ActiveSubmission> _submissions = [];
    private readonly object _lifecycleSync = new();
    private readonly WasapiCommandRecorder _commandRecorder;
    private readonly WasapiCommandRecorder _voiceInputRecorder;
    private VrChatOscOutput? _vrChatOscOutput;
    private readonly CommandPressTracker _commandPress;
    private bool _invertResultScroll;
    private int _pointerSmoothingStrength;
    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;
    private Task<CapturedFrame>? _captureTask;
    private long? _activeCaptureOperationId;
    private long _nextOperationId;
    private SelectionSnapshot? _lastRenderedSnapshot;
    private DateTimeOffset _lastRenderAt;
    private DateTimeOffset _lastResultScrollAt;
    private long? _lastScrollTargetOverlayId;
    private ResultScrollBoundary _resultScrollBoundary;
    private double _wpfWheelAccumulator;
    private SpatialSelectionPlane? _currentPlane;
    private SpatialSelectionPlane? _lockedPlane;
    private SpatialSelectionPlane? _capturePlane;
    private bool _interactiveOverlaysSuppressed;
    private bool _spatialOverlaysVisible = true;
    private long _nextOverlayId;
    private long _nextDiagnosticInputRequestId;
    private long? _interactionTargetOverlayId;
    private ETrackedControllerRole? _interactionContactHand;
    private readonly HashSet<long> _contactOverlayIds = [];
    private long? _commandTargetOverlayId;
    private long? _toolbarCommandPressOverlayId;
    private readonly OverlayGrabState _grabs = new();
    private readonly OverlayPointerStabilizer _pointerStabilizer = new();
    private readonly OverlayPointerRayStabilizer _pointerRayStabilizer = new();
    private readonly OverlayCloseHoldTracker _overlayCloseHold = new(SingleOverlayCloseHoldDuration);
    private OverlayPointerCapture? _pointerCapture;
    private bool _lastSelectionPlaneUsable = true;
    private bool _openVrInitialized;
    private bool _connected;
    private bool _lastTogglePressed;
    private DateTimeOffset? _idleTogglePressedAt;
    private bool _idleToggleLongHoldActivated;
    private bool _lastResultClickPressed;
    private bool _suppressResultClickRelease;
    private bool _lastLeftGripPressed;
    private bool _lastRightGripPressed;
    private bool _lastLeftPointerPressed;
    private bool _lastRightPointerPressed;
    private bool _voiceInputPressed;
    private bool _voiceInputWasPhysicallyPressed;
    private bool _vrVoiceInputPressed;
    private bool _desktopVoiceInputPressed;
    private readonly object _voiceInputSync = new();
    private long? _voiceInputReleaseCandidateAt;
    private Task? _voiceInputTask;
    private Task? _toolbarOscTask;
    private Task? _controlPanelTask;
    private long? _controlPanelOverlayId;
    private VrControlPanelWindow? _controlPanelWindow;
    private VrAndroidMirrorControlState _androidMirrorControlState = VrAndroidMirrorControlState.Empty;
    private VrSubtitleControlState _subtitleControlState = VrSubtitleControlState.Empty;
    private int _currentSceneProcessId;
    private int _openBindingsRequested;
    private Exception? _commandRecordingError;
    private AssistantRequestMode _pendingSubmissionMode = AssistantRequestMode.Translate;
    private Task<SenseVoiceCommandTranscriber>? _speechWarmupTask;
    private string? _lastConnectionError;
    private string? _lastPassiveWaitStatus;
    private bool _waitForSteamVrShutdown;
    private ulong _selectionOverlayHandle = OpenVR.k_ulOverlayHandleInvalid;
    private ulong _globalActionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
    private ulong _selectionActionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
    private ulong _resultsActionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
    private ulong _voiceInputActionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
    private ulong _toggleActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _leftTriggerActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _rightTriggerActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _leftHapticActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _rightHapticActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _resultScrollActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _overlayToggleActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _leftGripActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _rightGripActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _leftPointerClickActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _rightPointerClickActionHandle = OpenVR.k_ulInvalidActionHandle;
    private ulong _voiceInputPttActionHandle = OpenVR.k_ulInvalidActionHandle;
    private D3D11OverlayTexture? _selectionOverlayTexture;
    private D3D11OverlayDevice? _overlayTextureDevice;
    private SpatialQuadOverlayManager? _spatialOverlayManager;

    public SteamVrTranslationRuntime(AppConfiguration configuration, AppLog log)
    {
        _configuration = configuration;
        _log = log;
        _pipeline = new TranslationPipeline(configuration, log);
        _htmlResultRenderer = new Lazy<HtmlResultRenderer>(
            () => new HtmlResultRenderer(log),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _selection = new SelectionStateMachine(TimeSpan.FromSeconds(configuration.SelectionTimeoutSeconds));
        _commandRecorder = new WasapiCommandRecorder(configuration.Speech, log);
        _voiceInputRecorder = new WasapiCommandRecorder(configuration.Speech, log);
        if (configuration.VrChatVoiceInput.Enabled)
        {
            _vrChatOscOutput = new VrChatOscOutput(configuration.VrChatVoiceInput);
        }
        _commandPress = new CommandPressTracker(
            TimeSpan.FromMilliseconds(configuration.Speech.HoldThresholdMilliseconds));
        _invertResultScroll = configuration.InvertResultScroll;
        _pointerSmoothingStrength = AppConfiguration.NormalizePointerSmoothingStrength(
            configuration.PointerSmoothingStrength);
        _androidMirrorControlState = VrAndroidMirrorControlState.Empty with
        {
            DeviceSerial = configuration.AndroidMirror.DeviceSerial,
            MaximumSize = AndroidMirrorConfiguration.NormalizeMaximumSize(
                configuration.AndroidMirror.MaximumSize),
            MaximumFramesPerSecond =
                AndroidMirrorConfiguration.NormalizeMaximumFramesPerSecond(
                    configuration.AndroidMirror.MaximumFramesPerSecond),
            VideoBitRateMbps = configuration.AndroidMirror.VideoBitRateMbps,
            WindowScale = AndroidMirrorConfiguration.WindowScaleFromWidth(
                configuration.AndroidMirror.WindowWidthMeters)
        };
        _subtitleControlState = new VrSubtitleControlState(
            configuration.Subtitles.AsrBackend,
            configuration.Subtitles.ShowOriginalText,
            configuration.Subtitles.TranslateText,
            configuration.Subtitles.TargetLanguage,
            configuration.Subtitles.UseSpeakerColors,
            configuration.Subtitles.Diarization.Enabled,
            configuration.Subtitles.Diarization.CpuThreadCount,
            false,
            false,
            string.Empty);
    }

    public event EventHandler<SteamVrRuntimeEventArgs>? StatusChanged;

    public event EventHandler<VrControlPanelStateChangedEventArgs>? ControlPanelStateChanged;

    public event EventHandler? SubtitlePanelRequested;

    public event EventHandler<VrSubtitleControlRequestEventArgs>? SubtitleControlRequested;

    public event EventHandler<VrAndroidMirrorControlRequestEventArgs>? AndroidMirrorControlRequested;

    internal event EventHandler<WpfOverlayFrameRenderedEventArgs>? WpfOverlayFrameRendered;

    internal event EventHandler<ResultOverlayFrameRenderedEventArgs>? ResultOverlayFrameRendered;

    internal event EventHandler<DiagnosticRuntimeFrameCompletedEventArgs>? DiagnosticRuntimeFrameCompleted;

    internal event EventHandler<DiagnosticWpfPointerProcessedEventArgs>? DiagnosticWpfPointerProcessed;

    internal event EventHandler<DiagnosticWpfInteractionCompletedEventArgs>? DiagnosticWpfInteractionCompleted;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleSync)
            {
                return _loopTask is not null;
            }
        }
    }

    public uint CurrentSceneProcessId =>
        unchecked((uint)Math.Max(0, Volatile.Read(ref _currentSceneProcessId)));

    public Task StartAsync() => StartAsyncCore(startSpeechWarmup: true);

    internal Task StartAsyncForDiagnostics() => StartAsyncCore(startSpeechWarmup: false);

    private Task StartAsyncCore(bool startSpeechWarmup)
    {
        lock (_lifecycleSync)
        {
            if (_loopTask is not null)
            {
                return Task.CompletedTask;
            }

            _cancellation = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunAsync(_cancellation.Token));
            if (startSpeechWarmup)
            {
                StartSpeechWarmup();
            }
        }

        _log.Info(
            $"[startup] SteamVR 运行循环已创建：PID={Environment.ProcessId}，" +
            $"捕获眼睛={_configuration.StereoCompositionMode}，" +
            $"结果滚动反转={_configuration.InvertResultScroll}，" +
            $"交互射线={_configuration.ShowPointerRay}，" +
            $"光标防抖={_pointerSmoothingStrength}%，" +
            $"按住阈值={_configuration.Speech.HoldThresholdMilliseconds} ms，" +
            $"录音范围={_configuration.Speech.MinimumDurationMilliseconds} ms-" +
            $"{_configuration.Speech.MaximumDurationSeconds} s，" +
            $"OSC分段间隔={_configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds} ms，" +
            $"SenseVoice={_configuration.Speech.SenseVoiceBackend}/" +
            $"{_configuration.Speech.SenseVoiceVulkanDeviceIndex?.ToString() ?? "CPU"}，" +
            $"翻译Provider={_configuration.Translation.GetActiveProvider()?.DisplayName ?? "<缺失>"}");
        Publish(L("Runtime.Listening"), _selection.Snapshot.State);
        return Task.CompletedTask;
    }

    public void ApplyLiveSettings(
        string stereoCompositionMode,
        bool invertResultScroll,
        TranslationConfiguration translationConfiguration,
        string customCommandSystemPrompt,
        string customCommandPrompt)
    {
        if (stereoCompositionMode is not ("left-eye" or "right-eye"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(stereoCompositionMode),
                stereoCompositionMode,
                "捕获眼睛必须为 left-eye 或 right-eye。");
        }

        ArgumentNullException.ThrowIfNull(translationConfiguration);
        _pipeline.ApplyLiveSettings(
            stereoCompositionMode,
            translationConfiguration,
            customCommandSystemPrompt,
            customCommandPrompt);
        Volatile.Write(ref _invertResultScroll, invertResultScroll);
        _configuration.StereoCompositionMode = stereoCompositionMode;
        _configuration.InvertResultScroll = invertResultScroll;
        _configuration.Translation = translationConfiguration;
        _configuration.Speech.CustomCommandSystemPrompt = customCommandSystemPrompt;
        _configuration.Speech.CustomCommandPrompt = customCommandPrompt;
        _log.Info(
            $"[configuration] 运行时设置已更新：捕获眼睛={stereoCompositionMode}，" +
            $"滚动反转={invertResultScroll}，" +
            $"Provider={translationConfiguration.GetActiveProvider()?.DisplayName ?? "<缺失>"}，" +
            $"最大并发={translationConfiguration.GetActiveProvider()?.MaxConcurrency}，" +
            $"目标语言={translationConfiguration.TargetLanguage}。");
    }

    public void ApplyOscStreamingChunkInterval(int milliseconds)
    {
        if (milliseconds is
            < VrChatVoiceInputConfiguration.MinimumStreamingChunkIntervalMilliseconds or
            > VrChatVoiceInputConfiguration.MaximumStreamingChunkIntervalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        _configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds = milliseconds;
        _vrChatOscOutput?.UpdateStreamingChunkInterval(milliseconds);
        _log.Info($"[configuration] OSC 超长文本分段间隔已热更新：{milliseconds} ms。");
    }

    public void ApplyPointerRaySetting(bool enabled)
    {
        _configuration.ShowPointerRay = enabled;
        _log.Info($"[configuration] 交互射线已热更新：启用={enabled}。");
    }

    public void ApplyPointerSmoothingStrength(int strength)
    {
        var normalized = AppConfiguration.NormalizePointerSmoothingStrength(strength);
        Volatile.Write(ref _pointerSmoothingStrength, normalized);
        _configuration.PointerSmoothingStrength = normalized;
        _pointerStabilizer.Reset();
        _pointerRayStabilizer.Reset();
        _log.Info($"[configuration] 光标防抖已热更新：强度={normalized}%。");
    }

    private VrControlPanelState CurrentControlPanelState() => new(
        _configuration.VrChatVoiceInput.Enabled,
        _configuration.VrChatVoiceInput.TranslationEnabled,
        _configuration.VrChatVoiceInput.SendImmediately,
        _configuration.VrChatVoiceInput.TranslationDisplayMode,
        _configuration.VrChatVoiceInput.TranslationTargetLanguage,
        _configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds,
        _configuration.StereoCompositionMode,
        _androidMirrorControlState,
        _configuration.Subtitles.Enabled,
        _configuration.AndroidMirror.Enabled,
        _configuration.ShowPointerRay,
        Volatile.Read(ref _pointerSmoothingStrength),
        _subtitleControlState);

    public void UpdateExperimentalFeatureAvailability(
        bool subtitlesAvailable,
        bool androidMirrorAvailable)
    {
        _configuration.Subtitles.Enabled = subtitlesAvailable;
        _configuration.AndroidMirror.Enabled = androidMirrorAvailable;
        var window = _controlPanelWindow;
        if (window is not null)
        {
            var state = CurrentControlPanelState();
            _ = window.Dispatcher.BeginInvoke(() => window.ApplyState(state));
        }
    }

    public void UpdateAndroidMirrorControlState(VrAndroidMirrorControlState state)
    {
        _androidMirrorControlState = state;
        var window = _controlPanelWindow;
        if (window is null)
        {
            return;
        }

        _ = window.Dispatcher.BeginInvoke(() => window.ApplyAndroidMirrorState(state));
    }

    public void UpdateSubtitleControlState(VrSubtitleControlState state)
    {
        _subtitleControlState = state;
        var window = _controlPanelWindow;
        if (window is null)
        {
            return;
        }

        _ = window.Dispatcher.BeginInvoke(() => window.ApplySubtitleState(state));
    }

    private async Task ToggleControlPanelAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_controlPanelOverlayId is { } existingOverlayId)
            {
                await CloseWindowAsync(existingOverlayId, cancellationToken);
                _controlPanelOverlayId = null;
                _controlPanelWindow = null;
                Publish(L("VrPanel.Closed"), SelectionState.Idle);
                return;
            }

            var window = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var created = new VrControlPanelWindow(CurrentControlPanelState())
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000
                };
                created.Show();
                return created;
            });
            window.SettingsChanged += (_, args) => _controlPanelSettings.Enqueue(args.State);
            window.CaptureRequested += (_, _) =>
                _controlPanelActions.Enqueue(VrControlPanelAction.StartCapture);
            window.SubtitleRequested += (_, args) =>
                SubtitleControlRequested?.Invoke(this, args);
            window.AndroidMirrorRequested += (_, args) =>
                AndroidMirrorControlRequested?.Invoke(this, args);
            window.CloseRequested += (_, _) => _ = CloseControlPanelAsync();
            long overlayId;
            try
            {
                overlayId = await ShowWindowAsync(
                    window,
                    new WpfSpatialOverlayOptions
                    {
                        Name = "SteamVR Translator Control Panel",
                        WidthMeters = 0.25f,
                        DistanceMeters = 0.72f,
                        Placement = WpfSpatialOverlayPlacement.LeftHand,
                        CanGrab = true,
                        ShowToolbarWhenGrabbed = false,
                        CloseWindowOnOverlayRemoval = true,
                        MaximumFramesPerSecond = 60
                    },
                    cancellationToken);
            }
            catch
            {
                await Application.Current.Dispatcher.InvokeAsync(window.Close);
                throw;
            }
            _controlPanelWindow = window;
            _controlPanelOverlayId = overlayId;
            Publish(L("VrPanel.Opened"), SelectionState.Idle);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.Error("[control-panel] 打开或关闭 VR 控制面板失败。", exception);
            Publish(LF("VrPanel.Failed", exception.Message), SelectionState.Idle, true);
        }
    }

    private async Task CloseControlPanelAsync()
    {
        if (_controlPanelOverlayId is not { } overlayId)
        {
            return;
        }

        try
        {
            await CloseWindowAsync(overlayId);
        }
        finally
        {
            _controlPanelOverlayId = null;
            _controlPanelWindow = null;
        }
    }

    private void DrainControlPanelSettings()
    {
        VrControlPanelState? latest = null;
        while (_controlPanelSettings.TryDequeue(out var state))
        {
            latest = state;
        }

        if (latest is not { } value)
        {
            return;
        }

        var voice = _configuration.VrChatVoiceInput;
        var wasVoiceEnabled = voice.Enabled;
        voice.Enabled = value.VoiceEnabled;
        voice.TranslationEnabled = value.TranslationEnabled;
        voice.SendImmediately = value.SendImmediately;
        voice.TranslationDisplayMode = VoiceTranslationDisplayModes.Normalize(value.DisplayMode);
        voice.TranslationTargetLanguage = value.TargetLanguage;
        ApplyPointerRaySetting(value.PointerRayEnabled);
        ApplyPointerSmoothingStrength(value.PointerSmoothingStrength);
        ApplyOscStreamingChunkInterval(value.ChunkIntervalMilliseconds);
        ApplyLiveSettings(
            value.CaptureEye,
            _invertResultScroll,
            _configuration.Translation,
            _configuration.Speech.CustomCommandSystemPrompt,
            _configuration.Speech.CustomCommandPrompt);
        if (voice.Enabled && _vrChatOscOutput is null)
        {
            _vrChatOscOutput = new VrChatOscOutput(voice);
        }
        if (wasVoiceEnabled && !voice.Enabled && _voiceInputPressed)
        {
            _voiceInputPressed = false;
            _voiceInputWasPhysicallyPressed = false;
            _voiceInputReleaseCandidateAt = null;
            _ = _voiceInputRecorder.CancelAsync(CancellationToken.None);
            _ = SetVrChatTypingSafeAsync(false, CancellationToken.None);
        }

        var applied = CurrentControlPanelState();
        _log.Info(
            $"[control-panel] VRChat 语音设置已热更新：启用={applied.VoiceEnabled}，" +
            $"翻译={applied.TranslationEnabled}，立即发送={applied.SendImmediately}，" +
            $"显示={applied.DisplayMode}，目标语言={applied.TargetLanguage}，" +
            $"分段间隔={applied.ChunkIntervalMilliseconds} ms，" +
            $"捕获眼睛={applied.CaptureEye}，交互射线={applied.PointerRayEnabled}，" +
            $"防抖={applied.PointerSmoothingStrength}%。");
        ControlPanelStateChanged?.Invoke(
            this,
            new VrControlPanelStateChangedEventArgs(applied));
    }

    private void DrainControlPanelActions(CancellationToken cancellationToken)
    {
        while (_controlPanelActions.TryDequeue(out var action))
        {
            switch (action)
            {
                case VrControlPanelAction.StartCapture:
                    if (_selection.Snapshot.State == SelectionState.Idle)
                    {
                        _log.Info("[control-panel] 请求开始空间框选。");
                        HandleTransition(_selection.Toggle(DateTimeOffset.Now), cancellationToken);
                    }
                    break;
                case VrControlPanelAction.OpenSubtitles:
                    if (_configuration.Subtitles.Enabled)
                    {
                        _log.Info("[control-panel] 请求打开实时字幕窗口。");
                        SubtitlePanelRequested?.Invoke(this, EventArgs.Empty);
                    }
                    break;
            }
        }
    }

    public async Task StopAsync()
    {
        Task? loopTask;
        lock (_lifecycleSync)
        {
            loopTask = _loopTask;
            _cancellation?.Cancel();
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_lifecycleSync)
        {
            _loopTask = null;
            _cancellation?.Dispose();
            _cancellation = null;
        }

        CancelPendingWpfOverlayCommands();
        CancelPendingDiagnosticResultOverlayCommands();

        Publish(L("Runtime.Stopped"), SelectionState.Idle);
    }

    public void RequestToggle(string source = "外部请求")
    {
        _log.Info($"收到翻译键请求：{source}");
        _commandButtonEvents.Enqueue(new CommandButtonEvent(CommandButtonEventKind.ShortPress, source));
    }

    public void RequestButtonDown(string source)
    {
        _commandButtonEvents.Enqueue(new CommandButtonEvent(CommandButtonEventKind.Down, source));
    }

    public void RequestButtonUp(string source)
    {
        _commandButtonEvents.Enqueue(new CommandButtonEvent(CommandButtonEventKind.Up, source));
    }

    public void SetDesktopVoiceInputPressed(bool pressed)
    {
        if (!_configuration.VrChatVoiceInput.Enabled)
        {
            return;
        }

        var cancellationToken = _cancellation?.Token ?? CancellationToken.None;
        lock (_voiceInputSync)
        {
            if (_desktopVoiceInputPressed == pressed)
            {
                return;
            }
            _desktopVoiceInputPressed = pressed;
            UpdateCombinedVrChatVoiceInput(cancellationToken);
        }

        if (!pressed)
        {
            _ = CompleteDesktopVoiceReleaseAfterDebounceAsync();
        }
    }

    public void RequestOpenBindings()
    {
        _log.Info("收到打开 SteamVR 按键绑定界面的请求。");
        Interlocked.Exchange(ref _openBindingsRequested, 1);
    }

    public Task<long> ShowWindowAsync(
        Window window,
        WpfSpatialOverlayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!IsRunning)
        {
            return Task.FromException<long>(
                new InvalidOperationException("SteamVR 服务尚未启动。"));
        }

        var completion = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _wpfOverlayCommands.Enqueue(
            new ShowWpfOverlayCommand(
                window,
                (options ?? new WpfSpatialOverlayOptions()).Validated(),
                completion));
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public Task<bool> CloseWindowAsync(
        long overlayId,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _wpfOverlayCommands.Enqueue(new CloseWpfOverlayCommand(overlayId, completion));
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public void InvalidateWindow(long overlayId)
    {
        if (IsRunning)
        {
            _wpfOverlayCommands.Enqueue(new InvalidateWpfOverlayCommand(overlayId));
        }
    }

    public void ResizeWindow(long overlayId, float widthMeters)
    {
        if (IsRunning)
        {
            _wpfOverlayCommands.Enqueue(new ResizeWpfOverlayCommand(
                overlayId,
                Math.Clamp(widthMeters, 0.06f, 2.5f)));
        }
    }

    internal long MoveWindowPointerForDiagnostics(
        long overlayId,
        NormalizedPoint texturePoint,
        bool isPressed,
        bool dispatchInput = false)
    {
        if (!IsRunning)
        {
            return 0;
        }

        var requestId = Interlocked.Increment(ref _nextDiagnosticInputRequestId);
        _diagnosticWpfPointerCommands.Enqueue(
            new DiagnosticWpfPointerCommand(
                requestId,
                overlayId,
                texturePoint.Clamp(),
                isPressed,
                dispatchInput,
                false,
                false,
                Stopwatch.GetTimestamp()));
        return requestId;
    }

    internal long MoveWindowPointerThroughOpenVrForDiagnostics(
        long overlayId,
        NormalizedPoint targetTexturePoint,
        bool isPressed = false,
        bool dispatchInput = false)
    {
        if (!IsRunning)
        {
            return 0;
        }

        var requestId = Interlocked.Increment(ref _nextDiagnosticInputRequestId);
        _diagnosticWpfPointerCommands.Enqueue(
            new DiagnosticWpfPointerCommand(
                requestId,
                overlayId,
                targetTexturePoint,
                isPressed,
                dispatchInput,
                true,
                false,
                Stopwatch.GetTimestamp()));
        return requestId;
    }

    internal long LeaveWindowPointerForDiagnostics(long overlayId)
    {
        if (!IsRunning)
        {
            return 0;
        }

        var requestId = Interlocked.Increment(ref _nextDiagnosticInputRequestId);
        _diagnosticWpfPointerCommands.Enqueue(
            new DiagnosticWpfPointerCommand(
                requestId,
                overlayId,
                default,
                false,
                false,
                false,
                true,
                Stopwatch.GetTimestamp()));
        return requestId;
    }

    internal long ClickWindowControlForDiagnostics(long overlayId, string controlName)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(controlName))
        {
            return 0;
        }

        var requestId = Interlocked.Increment(ref _nextDiagnosticInputRequestId);
        _diagnosticWpfInteractionCommands.Enqueue(
            new DiagnosticWpfInteractionCommand(
                requestId,
                overlayId,
                DiagnosticWpfInteractionKind.Click,
                controlName.Trim(),
                new NormalizedPoint(0.5f, 0.5f),
                0,
                Stopwatch.GetTimestamp()));
        return requestId;
    }

    internal long ScrollWindowForDiagnostics(
        long overlayId,
        NormalizedPoint texturePoint,
        int wheelDelta)
    {
        if (!IsRunning || wheelDelta == 0)
        {
            return 0;
        }

        var requestId = Interlocked.Increment(ref _nextDiagnosticInputRequestId);
        _diagnosticWpfInteractionCommands.Enqueue(
            new DiagnosticWpfInteractionCommand(
                requestId,
                overlayId,
                DiagnosticWpfInteractionKind.Scroll,
                null,
                texturePoint.Clamp(),
                wheelDelta,
                Stopwatch.GetTimestamp()));
        return requestId;
    }

    internal Task<long> ShowMarkdownResultForDiagnostics(
        string markdown,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return Task.FromException<long>(
                new InvalidOperationException("SteamVR 服务尚未启动。"));
        }
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Task.FromException<long>(
                new ArgumentException("Markdown 内容不能为空。", nameof(markdown)));
        }

        var completion = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticResultOverlayCommands.Enqueue(
            new ShowDiagnosticMarkdownOverlayCommand(markdown, completion));
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    internal Task<long> ShowChatResultForDiagnostics(
        AssistantConversationView chat,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return Task.FromException<long>(
                new InvalidOperationException("SteamVR 服务尚未启动。"));
        }

        var completion = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticResultOverlayCommands.Enqueue(
            new ShowDiagnosticChatOverlayCommand(chat, completion));
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    internal long UpdateChatResultForDiagnostics(
        long overlayId,
        AssistantConversationView chat)
    {
        if (!IsRunning)
        {
            return 0;
        }

        var commandId = Interlocked.Increment(ref _nextDiagnosticInputRequestId);
        _diagnosticResultOverlayCommands.Enqueue(
            new UpdateDiagnosticChatOverlayCommand(commandId, overlayId, chat));
        return commandId;
    }

    internal long ScrollResultForDiagnostics(long overlayId, double delta)
    {
        if (!IsRunning || Math.Abs(delta) < 0.1)
        {
            return 0;
        }

        var requestId = Interlocked.Increment(ref _nextDiagnosticInputRequestId);
        _diagnosticResultOverlayCommands.Enqueue(
            new ScrollDiagnosticResultOverlayCommand(requestId, overlayId, delta));
        return requestId;
    }

    internal Task<bool> CloseOverlayForDiagnostics(
        long overlayId,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticResultOverlayCommands.Enqueue(
            new CloseDiagnosticResultOverlayCommand(overlayId, completion));
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_voiceInputTask is not null)
        {
            try
            {
                await _voiceInputTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (_toolbarOscTask is not null)
        {
            await _toolbarOscTask;
        }
        _commandRecorder.Dispose();
        _voiceInputRecorder.Dispose();
        _vrChatOscOutput?.Dispose();
        foreach (var source in _interactiveOverlays
                     .Select(overlay => overlay.WindowSource)
                     .OfType<WpfWindowOverlaySource>())
        {
            source.Dispose();
        }
        _interactiveOverlays.Clear();
        if (_speechWarmupTask is not null)
        {
            try
            {
                (await _speechWarmupTask).Dispose();
            }
            catch (Exception)
            {
            }
        }
        if (_htmlResultRenderer.IsValueCreated)
        {
            await _htmlResultRenderer.Value.DisposeAsync();
        }
        _pipeline.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var timerFramesPerSecond = RuntimeFramesPerSecond;
        var frameTimer = CreateRuntimeFrameTimer(timerFramesPerSecond);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_connected)
                {
                    var runtimeProcesses = SteamVrRuntimeProcessProbe.Capture();
                    if (_waitForSteamVrShutdown)
                    {
                        if (runtimeProcesses.AnyRunning)
                        {
                            PublishPassiveWait(L("Runtime.WaitShutdown"));
                            await Task.Delay(RuntimeProbeDelayMilliseconds, cancellationToken);
                            continue;
                        }

                        _waitForSteamVrShutdown = false;
                    }

                    if (!runtimeProcesses.IsReady)
                    {
                        PublishPassiveWait(L("Runtime.WaitForSteamVr"));
                        await Task.Delay(RuntimeProbeDelayMilliseconds, cancellationToken);
                        continue;
                    }

                    try
                    {
                        if (!InitializeOpenVr())
                        {
                            PublishPassiveWait(L("Runtime.WaitForReady"));
                            await Task.Delay(RuntimeProbeDelayMilliseconds, cancellationToken);
                            continue;
                        }

                        _lastPassiveWaitStatus = null;
                        _lastConnectionError = null;
                    }
                    catch (Exception exception)
                    {
                        ShutdownOpenVr();
                        if (!string.Equals(_lastConnectionError, exception.Message, StringComparison.Ordinal))
                        {
                            _lastConnectionError = exception.Message;
                            _log.Error("SteamVR 连接失败。程序将继续等待运行时。", exception);
                            Publish(LF("Runtime.ConnectionFailed", exception.Message), SelectionState.Idle, true);
                        }

                        await Task.Delay(ReconnectDelayMilliseconds, cancellationToken);
                        continue;
                    }
                }

                try
                {
                    var diagnosticFrameStartedAt = DiagnosticRuntimeFrameCompleted is null
                        ? 0
                        : Stopwatch.GetTimestamp();
                    if (ConsumeRuntimeQuitEvent())
                    {
                        _selection.Cancel(DateTimeOffset.Now);
                        _waitForSteamVrShutdown = true;
                        ShutdownOpenVr();
                        PublishPassiveWait(L("Runtime.WaitShutdown"));
                        await Task.Delay(RuntimeProbeDelayMilliseconds, cancellationToken);
                        continue;
                    }

                    DrainWpfOverlayCommands();
                    DrainDiagnosticResultOverlayCommands();
                    DrainControlPanelSettings();
                    DrainControlPanelActions(cancellationToken);
                    PollFrame(cancellationToken);
                    DrainDiagnosticWpfPointerCommands();
                    DrainDiagnosticWpfInteractionCommands();
                    await CompleteCaptureIfReadyAsync();
                    await CompleteSubmissionsIfReadyAsync();
                    DrainResultUpdates();
                    RenderInteractiveOverlays();
                    if (diagnosticFrameStartedAt != 0)
                    {
                        var completedAt = Stopwatch.GetTimestamp();
                        DiagnosticRuntimeFrameCompleted?.Invoke(
                            this,
                            new DiagnosticRuntimeFrameCompletedEventArgs(
                                completedAt,
                                Stopwatch.GetElapsedTime(diagnosticFrameStartedAt, completedAt)));
                    }
                    var nextTimerFramesPerSecond = HasDirectPixelOverlay()
                        ? DirectPixelRuntimeFramesPerSecond
                        : RuntimeFramesPerSecond;
                    if (nextTimerFramesPerSecond != timerFramesPerSecond)
                    {
                        frameTimer.Dispose();
                        timerFramesPerSecond = nextTimerFramesPerSecond;
                        frameTimer = CreateRuntimeFrameTimer(timerFramesPerSecond);
                    }
                    if (!await frameTimer.WaitForNextTickAsync(cancellationToken))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _log.Error("SteamVR 运行循环发生错误，准备重新连接。", exception);
                    Publish(LF("Runtime.Error", exception.Message), _selection.Snapshot.State, true);
                    _selection.Cancel(DateTimeOffset.Now);
                    ShutdownOpenVr();
                    await Task.Delay(ReconnectDelayMilliseconds, cancellationToken);
                }
            }
        }
        finally
        {
            frameTimer.Dispose();
            ShutdownOpenVr();
            if (_captureTask is not null)
            {
                try
                {
                    await _captureTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _log.Error("停止时等待截图任务失败。", exception);
                }

                _captureTask = null;
            }
            foreach (var submission in _submissions.Values.ToArray())
            {
                submission.Cancellation.Cancel();
                try
                {
                    await submission.Task;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _log.Error(
                        $"停止时等待翻译任务失败：{DiagnosticTag(submission.OperationId, submission.RequestId)}。",
                        exception);
                }
                finally
                {
                    submission.Cancellation.Dispose();
                }
            }
            _submissions.Clear();
        }
    }

    private static PeriodicTimer CreateRuntimeFrameTimer(int framesPerSecond) =>
        new(TimeSpan.FromSeconds(1d / framesPerSecond));

    private bool HasDirectPixelOverlay() =>
        _interactiveOverlays.Any(overlay =>
            !overlay.IsPresentationDeferred &&
            overlay.WindowSource?.HasDirectPixelSource == true);

    private void PollFrame(CancellationToken cancellationToken)
    {
        UpdateActionState();
        UpdateCurrentSceneProcessId();

        var togglePressed = ReadDigital(_toggleActionHandle);
        var now = DateTimeOffset.Now;
        if (_configuration.VrChatVoiceInput.Enabled)
        {
            UpdateVrChatVoiceInput(ReadDigital(_voiceInputPttActionHandle), cancellationToken);
        }
        UpdateOverlayInteraction(cancellationToken);
        if (togglePressed && !_lastTogglePressed)
        {
            HandleCommandButtonDown("SteamVR 左手摇杆", now, cancellationToken);
        }
        else if (!togglePressed && _lastTogglePressed)
        {
            HandleCommandButtonUp("SteamVR 左手摇杆", now, cancellationToken);
        }
        _lastTogglePressed = togglePressed;

        while (_commandButtonEvents.TryDequeue(out var buttonEvent))
        {
            switch (buttonEvent.Kind)
            {
                case CommandButtonEventKind.Down:
                    HandleCommandButtonDown(buttonEvent.Source, now, cancellationToken);
                    break;
                case CommandButtonEventKind.Up:
                    HandleCommandButtonUp(buttonEvent.Source, now, cancellationToken);
                    break;
                case CommandButtonEventKind.ShortPress:
                    HandleCommandButtonDown(buttonEvent.Source, now, cancellationToken);
                    HandleCommandButtonUp(buttonEvent.Source, now, cancellationToken);
                    break;
            }
        }

        UpdateCommandHold(now, cancellationToken);
        UpdateControlPanelHold(now, cancellationToken);

        if (Interlocked.Exchange(ref _openBindingsRequested, 0) == 1)
        {
            var error = OpenVR.Input.OpenBindingUI(
                SteamVrManifestStore.ApplicationKey,
                _globalActionSetHandle,
                OpenVR.k_ulInvalidInputValueHandle,
                bShowOnDesktop: true);
            EnsureInput(error, "打开 SteamVR 动作绑定界面");
        }

        if (_selection.Snapshot.State is SelectionState.Armed or SelectionState.Sizing)
        {
            var leftPressed = ReadDigital(_leftTriggerActionHandle);
            var rightPressed = ReadDigital(_rightTriggerActionHandle);
            var planeBecameUnavailable = false;
            SpatialSelectionPlane? plane = null;
            if (TryReadSpatialPlane(out var measuredPlane))
            {
                plane = measuredPlane;
                if (leftPressed && rightPressed)
                {
                    _currentPlane = measuredPlane;
                    SetSpatialOverlayTransform(measuredPlane);
                }
            }
            else if (leftPressed && rightPressed && _currentPlane is not null)
            {
                _currentPlane = null;
                planeBecameUnavailable = true;
            }

            var transition = _selection.Update(
                leftPressed,
                rightPressed,
                plane?.LeftPointer,
                plane?.RightPointer,
                DateTimeOffset.Now,
                plane?.IsUsable ?? false);
            if (transition.Kind == SelectionTransitionKind.RegionLocked)
            {
                _lockedPlane = _currentPlane;
            }

            HandleTransition(transition, cancellationToken);
            if (transition.Kind == SelectionTransitionKind.RegionLocked)
            {
                HandleTransition(_selection.Toggle(now), cancellationToken);
            }
            UpdateSelectionManagerHint(plane, leftPressed && rightPressed, transition.Snapshot.State);
            RenderSelectionIfNeeded(
                force: transition.Kind != SelectionTransitionKind.None || planeBecameUnavailable);
        }

        UpdateResultControls();

        HandleTransition(_selection.Tick(now), cancellationToken);
    }

    private void UpdateCurrentSceneProcessId()
    {
        try
        {
            Volatile.Write(
                ref _currentSceneProcessId,
                unchecked((int)(OpenVR.Applications?.GetCurrentSceneProcessId() ?? 0)));
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _currentSceneProcessId) != 0)
            {
                _log.Warning($"[subtitles] 无法读取 SteamVR 当前场景进程：{exception.Message}");
            }
            Volatile.Write(ref _currentSceneProcessId, 0);
        }
    }

    private void HandleCommandButtonDown(
        string source,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _log.Info(
            $"{DiagnosticTag()} [input] 翻译键按下：来源={source}，" +
            $"当前状态={_selection.Snapshot.State}，录音中={_commandRecorder.IsRecording}");
        if (_selection.Snapshot.State != SelectionState.Idle)
        {
            HandleTransition(_selection.Toggle(now), cancellationToken);
            return;
        }

        if (_grabs.HasMultiple)
        {
            Publish(L("Interaction.MultipleNoAction"), SelectionState.Idle, true);
            return;
        }

        if (_captureTask is { IsCompleted: false })
        {
            Publish(L("Capture.InProgress"), SelectionState.Idle, true);
            return;
        }

        var target = ResolveInteractionTarget();
        if (target is null)
        {
            if (_interactionTargetOverlayId is not null)
            {
                Publish(L("Interaction.NeedCapture"), SelectionState.Idle, true);
                return;
            }

            _idleTogglePressedAt = now;
            _idleToggleLongHoldActivated = false;
            return;
        }

        var isCapture = target is { Kind: InteractiveOverlayKind.Capture, Capture: not null };
        var isConversation = target is
        {
            Kind: InteractiveOverlayKind.Result,
            Conversation: not null
        };
        if (!isCapture && !isConversation)
        {
            Publish(L("Interaction.NeedCaptureOrConversation"), SelectionState.Idle, true);
            return;
        }

        if (_submissions.Values.Any(submission => submission.OverlayId == target.Id))
        {
            Publish(L("Request.InProgress"), SelectionState.Idle, true);
            return;
        }

        if (_commandPress.IsPressed)
        {
            _log.Warning($"{DiagnosticTag()} [input] 忽略重复的翻译键按下事件：来源={source}");
            return;
        }

        _commandPress.Press(now);
        _commandTargetOverlayId = target.Id;
        _pendingSubmissionMode = isConversation
            ? AssistantRequestMode.CustomCommand
            : AssistantRequestMode.Translate;
        _commandRecordingError = null;
        try
        {
            _commandRecorder.Start();
            SetCommandRecording(target.Id, true);
            _log.Info(
                $"{DiagnosticTag()} [input] 已开始暂存语音命令；" +
                (isConversation
                    ? "松开后将在当前结果窗口内继续追问。"
                    : "短按松开时将丢弃录音并直接翻译。"));
            Publish(
                isConversation
                    ? L("Command.HoldFollowUp")
                    : L("Command.HoldCustom"),
                SelectionState.Idle);
        }
        catch (Exception exception)
        {
            _commandRecordingError = exception;
            _log.Error("无法开始语音命令录音；短按翻译仍可使用。", exception);
            Publish(
                isConversation
                    ? L("Command.MicrophoneFollowUpUnavailable")
                    : L("Command.MicrophoneTranslateAvailable"),
                SelectionState.Idle,
                true);
        }
    }

    private void HandleCommandButtonUp(
        string source,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_idleTogglePressedAt is not null)
        {
            var openedPanel = _idleToggleLongHoldActivated;
            _idleTogglePressedAt = null;
            _idleToggleLongHoldActivated = false;
            if (!openedPanel)
            {
                HandleTransition(_selection.Toggle(now), cancellationToken);
            }
            return;
        }

        var heldDuration = _commandPress.HeldDuration(now);
        var targetOverlayId = _commandTargetOverlayId;
        var kind = _commandPress.Release(now);
        if (kind == CommandPressKind.None)
        {
            var message =
                $"{DiagnosticTag()} [input] 翻译键松开未触发提交：来源={source}，" +
                $"当前状态={_selection.Snapshot.State}";
            if (_commandTargetOverlayId is not null)
            {
                _log.Warning(message + "，原因=没有对应的按下事件");
            }
            else
            {
                _log.Info(message + "，原因=本次按键用于切换选择状态");
            }
            return;
        }

        SetCommandRecording(targetOverlayId, false);

        _log.Info(
            $"{DiagnosticTag()} [input] 翻译键松开：来源={source}，" +
            $"持续={heldDuration.TotalMilliseconds:F0} ms，手势={kind}，" +
            $"长按阈值={_configuration.Speech.HoldThresholdMilliseconds} ms");
        var isConversationFollowUp = _commandTargetOverlayId is { } targetId &&
                                     FindOverlay(targetId)?.Conversation is not null;
        _pendingSubmissionMode = isConversationFollowUp || kind == CommandPressKind.CustomCommand
            ? AssistantRequestMode.CustomCommand
            : AssistantRequestMode.Translate;
        StartTranslationForCommandTarget(cancellationToken);
    }

    private void UpdateCommandHold(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_commandPress.IsPressed || _commandTargetOverlayId is null)
        {
            return;
        }

        if (_commandPress.ActivateLongHold(now))
        {
            _log.Info(
                $"{DiagnosticTag()} [input] 已越过长按阈值：" +
                $"持续={_commandPress.HeldDuration(now).TotalMilliseconds:F0} ms");
            PulseBoth(0.045f, 120f, 0.45f);
            var isFollowUp = _commandTargetOverlayId is { } targetId &&
                             FindOverlay(targetId)?.Conversation is not null;
            Publish(
                _commandRecordingError is null
                    ? isFollowUp
                        ? L("Command.ListeningFollowUp")
                        : L("Command.ListeningCustom")
                    : L("Command.MicrophoneErrorOnRelease"),
                SelectionState.Idle,
                _commandRecordingError is not null);
        }

        if (_commandPress.HeldDuration(now) >= TimeSpan.FromSeconds(_configuration.Speech.MaximumDurationSeconds))
        {
            _log.Warning($"语音命令达到 {_configuration.Speech.MaximumDurationSeconds} 秒上限，自动提交。");
            HandleCommandButtonUp("录音时长上限", now, cancellationToken);
        }
    }

    private void UpdateControlPanelHold(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_idleTogglePressedAt is not { } pressedAt ||
            _idleToggleLongHoldActivated ||
            now - pressedAt < ControlPanelHoldDuration)
        {
            return;
        }

        _idleToggleLongHoldActivated = true;
        PulseLeft(0.05f, 120f, 0.55f);
        _controlPanelTask = ToggleControlPanelAsync(cancellationToken);
    }

    private void HandleTransition(SelectionTransition transition, CancellationToken cancellationToken)
    {
        switch (transition.Kind)
        {
            case SelectionTransitionKind.None:
                return;
            case SelectionTransitionKind.Armed:
                _activeCaptureOperationId = Interlocked.Increment(ref _nextOperationId);
                _currentPlane = null;
                _lockedPlane = null;
                _lastSelectionPlaneUsable = true;
                _interactiveOverlaysSuppressed = true;
                HideSelectionOverlay();
                _lastRenderedSnapshot = null;
                HideInteractiveOverlays();
                _log.Info($"{DiagnosticTag()} [selection] 进入空间选择模式。");
                _log.Info($"{DiagnosticTag()} [overlay] 框选和截图期间暂时隐藏全部空间对象。");
                SetHeadLockedSelectionOverlayTransform();
                ResetSelectionOverlayImage(_renderer.RenderSelection(transition.Snapshot));
                _lastRenderedSnapshot = transition.Snapshot;
                _lastRenderAt = DateTimeOffset.Now;
                ShowSelectionOverlay();
                PulseBoth(0.06f, 80f, 0.45f);
                Publish(L("Selection.ArmedStatus"), transition.Snapshot.State);
                break;
            case SelectionTransitionKind.SizingStarted:
                _log.Info(
                    $"{DiagnosticTag()} [selection] 双扳机按下，开始调整空间平面。" +
                    DescribePlane(_currentPlane));
                if (_currentPlane is { } sizingPlane)
                {
                    SetSpatialOverlayTransform(sizingPlane);
                }
                PulseBoth(0.035f, 110f, 0.35f);
                Publish(L("Selection.SizingStatus"), transition.Snapshot.State);
                break;
            case SelectionTransitionKind.RegionUpdated:
                break;
            case SelectionTransitionKind.RegionLocked:
                if (transition.Snapshot.Region is { } region)
                {
                    _log.Info(
                        $"{DiagnosticTag()} [selection] 框选区域已锁定：" +
                        $"({region.Left:F4}, {region.Top:F4})-" +
                        $"({region.Right:F4}, {region.Bottom:F4})，" +
                        $"视线夹角={_lockedPlane?.ViewAngleDegrees:F1}°" +
                        DescribePlane(_lockedPlane));
                }
                RenderSelectionIfNeeded(force: true);
                PulseBoth(0.09f, 65f, 0.65f);
                Publish(L("Selection.LockedStatus"), transition.Snapshot.State);
                break;
            case SelectionTransitionKind.RegionRejected:
                ResetRejectedSelection(transition.Snapshot);
                PulseBoth(0.08f, 35f, 0.3f);
                Publish(L("Selection.TooSmall"), transition.Snapshot.State, true);
                break;
            case SelectionTransitionKind.OrientationRejected:
                PulseBoth(0.08f, 35f, 0.3f);
                var rejectionMessage = L("Selection.InvalidPlane");
                if (_currentPlane is { } rejectedPlane)
                {
                    if (!rejectedPlane.HasUsableDimensions)
                    {
                        _log.Warning(
                            $"{DiagnosticTag()} [selection] 框选尺寸无效：" +
                            $"当前={rejectedPlane.Width:F3}m x {rejectedPlane.Height:F3}m，" +
                            $"最低={SpatialSelectionPlane.MinimumWidthMeters:F2}m x " +
                            $"{SpatialSelectionPlane.MinimumHeightMeters:F2}m");
                        rejectionMessage = LF(
                            "Selection.MinimumSize",
                            SpatialSelectionPlane.MinimumWidthMeters * 100);
                    }
                    else
                    {
                        _log.Warning(
                            $"{DiagnosticTag()} [selection] 相框角度无效：" +
                            $"视线夹角={rejectedPlane.ViewAngleDegrees:F1}°/" +
                            $"上限={SpatialSelectionPlane.MaximumViewAngleDegrees:F0}°");
                        rejectionMessage = L("Selection.InvalidAngle");
                    }
                }

                ResetRejectedSelection(transition.Snapshot);
                Publish(rejectionMessage, transition.Snapshot.State, true);
                break;
            case SelectionTransitionKind.SubmissionRequested:
                if (_lockedPlane is not { } submittedPlane || !submittedPlane.IsUsable)
                {
                    HideSelectionOverlay();
                    _currentPlane = null;
                    _lockedPlane = null;
                    _selection.Complete(DateTimeOffset.Now);
                    _activeCaptureOperationId = null;
                    RestoreInteractiveOverlays("框选平面无效");
                    Publish(L("Selection.InvalidPlane"), SelectionState.Idle, true);
                    break;
                }

                HideSelectionOverlay();
                _lastRenderedSnapshot = null;
                var operationId = _activeCaptureOperationId ?? Interlocked.Increment(ref _nextOperationId);
                _activeCaptureOperationId = operationId;
                _log.Info(
                    $"{DiagnosticTag(operationId, null)} [capture] 松开扳机后立即捕获：" +
                    DescribePlane(submittedPlane));
                _capturePlane = submittedPlane;
                _captureTask = _pipeline.CaptureAsync(
                    submittedPlane,
                    cancellationToken,
                    DiagnosticTag(operationId, null));
                Publish(L("Selection.Capturing"), transition.Snapshot.State);
                break;
            case SelectionTransitionKind.Cancelled:
                _log.Info($"{DiagnosticTag()} [selection] 操作已取消。");
                _activeCaptureOperationId = null;
                _lastResultClickPressed = true;
                HideSelectionOverlay();
                _currentPlane = null;
                _lockedPlane = null;
                RestoreInteractiveOverlays("取消框选");
                Publish(L("Selection.Cancelled"), transition.Snapshot.State);
                break;
            case SelectionTransitionKind.TimedOut:
                _log.Warning($"{DiagnosticTag()} [selection] 操作超时。");
                _activeCaptureOperationId = null;
                HideSelectionOverlay();
                _currentPlane = null;
                _lockedPlane = null;
                RestoreInteractiveOverlays("框选超时");
                Publish(L("Selection.TimedOut"), transition.Snapshot.State, true);
                break;
            case SelectionTransitionKind.Completed:
                HideSelectionOverlay();
                _currentPlane = null;
                _lockedPlane = null;
                _activeCaptureOperationId = null;
                Publish(
                    _submissions.Count > 0
                        ? L("Selection.ReadyWithRequests")
                        : _interactiveOverlays.Any(item => item.Kind == InteractiveOverlayKind.Capture)
                        ? L("Selection.ReadyWithCapture")
                        : L("Selection.Ready"),
                    transition.Snapshot.State);
                break;
        }
    }

    private void StartTranslationForCommandTarget(CancellationToken cancellationToken)
    {
        if (_grabs.HasMultiple)
        {
            CancelSingleOverlayCommand("双手同时抓取空间对象");
            Publish(L("Translation.MultipleNoAction"), SelectionState.Idle, true);
            return;
        }

        var source = _commandTargetOverlayId is { } targetId
            ? FindOverlay(targetId)
            : null;
        SetCommandRecording(_commandTargetOverlayId, false);
        _commandTargetOverlayId = null;
        var sourceCapture = source?.Capture ?? source?.Conversation?.Capture;
        if (source is null || sourceCapture is null)
        {
            _ = _commandRecorder.CancelAsync(CancellationToken.None);
            Publish(L("Translation.TargetMissing"), SelectionState.Idle, true);
            return;
        }

        if (_submissions.Values.Any(submission => submission.OverlayId == source.Id))
        {
            _ = _commandRecorder.CancelAsync(CancellationToken.None);
            Publish(L("Request.InProgress"), SelectionState.Idle, true);
            return;
        }

        var mode = source.Conversation is not null
            ? AssistantRequestMode.CustomCommand
            : _pendingSubmissionMode;
        var operationId = Interlocked.Increment(ref _nextOperationId);
        var conversation = source.Conversation ??
                           (mode == AssistantRequestMode.CustomCommand
                               ? new AssistantConversation(sourceCapture)
                               : null);
        var initialStatus = mode switch
        {
            AssistantRequestMode.CustomCommand when conversation is not null =>
                conversation.FormatPending(null, L("Translation.RecognizingCommand")),
            AssistantRequestMode.LayoutTranslate => L("Translation.LayoutPending"),
            _ => L("Translation.Pending")
        };
        var statusMessage = mode switch
        {
            AssistantRequestMode.CustomCommand when source.Conversation is not null =>
                L("Translation.RecognizingFollowUp"),
            AssistantRequestMode.CustomCommand => L("Translation.RecognizingCommand"),
            AssistantRequestMode.LayoutTranslate => L("Translation.LayoutPending"),
            _ => L("Translation.Pending")
        };
        var streamingFormat = mode == AssistantRequestMode.LayoutTranslate
            ? ResultContentFormat.PlainText
            : ResultContentFormat.Markdown;
        var previousResult = source.Conversation is not null
            ? source.Result?.Snapshot()
            : null;
        InteractiveOverlay resultOverlay;
        ResultOverlayState resultState;
        if (source is { Kind: InteractiveOverlayKind.Result, Result: { } existingResult })
        {
            resultOverlay = source;
            resultState = existingResult;
        }
        else
        {
            resultState = new ResultOverlayState();
            var resultPlane = CreateResultPlaneInFront(source.Plane, mode);
            resultOverlay = AddInteractiveOverlay(
                InteractiveOverlayKind.Result,
                resultPlane,
                capture: null,
                resultState,
                conversation: conversation);
            resultOverlay.IsPresentationDeferred = mode == AssistantRequestMode.CustomCommand;
        }
        var requestId = resultState.Begin(
            initialStatus,
            streamingFormat,
            mode == AssistantRequestMode.CustomCommand
                ? conversation?.CreateView(pendingAnswer: initialStatus)
                : null);
        resultOverlay.IsDirty = true;
        resultOverlay.SynchronizeTextureBuffers = true;
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var submission = new ActiveSubmission
        {
            OperationId = operationId,
            RequestId = requestId,
            SourceOverlayId = source.Id,
            OverlayId = resultOverlay.Id,
            Mode = mode,
            Conversation = conversation,
            ConversationHistory = conversation?.Snapshot() ?? Array.Empty<AssistantConversationTurn>(),
            PreviousResult = previousResult,
            Stopwatch = Stopwatch.StartNew(),
            Cancellation = requestCancellation
        };
        _log.Info(
            $"{DiagnosticTag(operationId, requestId)} [submit] 从截图对象启动请求：" +
            $"源Overlay={source.Id}，结果Overlay={resultOverlay.Id}，模式={mode}，" +
            $"历史轮数={submission.ConversationHistory.Count}");
        submission.Task = ExecuteTranslationAsync(
            sourceCapture,
            submission,
            requestCancellation.Token);
        _submissions.Add(operationId, submission);
        _pendingSubmissionMode = AssistantRequestMode.Translate;
        Publish(statusMessage, SelectionState.Idle);
    }

    private async Task<TranslationResult> ExecuteTranslationAsync(
        CapturedFrame capture,
        ActiveSubmission submission,
        CancellationToken cancellationToken)
    {
        var diagnosticTag = DiagnosticTag(submission.OperationId, submission.RequestId);
        _log.Info($"{diagnosticTag} [submit] 使用已有截图，开始处理命令。");
        string? customCommand = null;
        try
        {
            if (submission.Mode == AssistantRequestMode.CustomCommand)
            {
                if (_commandRecordingError is not null)
                {
                    throw new InvalidOperationException(
                        $"无法录制自定义命令：{_commandRecordingError.Message}",
                        _commandRecordingError);
                }

                using var audio = await _commandRecorder.StopAsync(cancellationToken);
                _log.Info(
                    $"{diagnosticTag} [audio] 录音文件可用：" +
                    $"持续={audio.Duration.TotalMilliseconds:F0} ms，" +
                    $"字节={new FileInfo(audio.FilePath).Length:N0}");
                var transcriber = await GetSpeechTranscriberAsync();
                var transcription = await transcriber.TranscribeAsync(
                    audio,
                    diagnosticTag,
                    cancellationToken);
                customCommand = transcription.Text.Trim();
                submission.CustomCommand = customCommand;
                var analyzingStatus = L("Translation.AnalyzingImage");
                QueueResultUpdate(
                    submission,
                    submission.Conversation?.FormatPending(
                        customCommand,
                        analyzingStatus) ??
                    LF("Translation.CommandPending", customCommand),
                    submission.Conversation?.CreateView(customCommand, analyzingStatus));
                Publish(LF("Translation.CommandRecognized", customCommand), SelectionState.Idle);
            }
            else
            {
                if (_commandRecorder.IsRecording)
                {
                    _log.Info($"{diagnosticTag} [audio] 短按模式，取消并删除暂存录音。");
                    await _commandRecorder.CancelAsync(cancellationToken);
                }
                else
                {
                    _log.Info($"{diagnosticTag} [audio] 直接翻译模式，无需语音命令。");
                }
            }
        }
        catch (Exception exception)
        {
            _log.Error($"{diagnosticTag} [submit] 命令处理阶段失败。", exception);
            throw;
        }

        _log.Info(
            $"{diagnosticTag} [submit] 进入后端阶段：" +
            $"截图={Path.GetFileName(capture.Path)}，命令字符={customCommand?.Length ?? 0}");
        void ReportPartial(string partial)
        {
            var display = submission.Conversation is not null && customCommand is not null
                ? submission.Conversation.FormatPending(customCommand, partial)
                : partial;
            QueueResultUpdate(
                submission,
                display,
                submission.Conversation?.CreateView(customCommand, partial));
        }
        return await _pipeline.ExecuteAsync(
            capture,
            submission.Mode,
            customCommand,
            submission.ConversationHistory,
            ReportPartial,
            cancellationToken,
            diagnosticTag);
    }

    private async Task CompleteCaptureIfReadyAsync()
    {
        if (_captureTask is null || !_captureTask.IsCompleted)
        {
            return;
        }

        try
        {
            var capture = await _captureTask;
            if (_capturePlane is not { } plane)
            {
                throw new InvalidOperationException("截图完成时缺少空间平面。");
            }

            var overlay = AddInteractiveOverlay(
                InteractiveOverlayKind.Capture,
                plane,
                capture,
                resultState: null);
            _spatialOverlaysVisible = true;
            RenderInteractiveOverlay(overlay, force: true);
            _log.Info(
                $"{DiagnosticTag()} [capture] 截图对象已创建：Overlay={overlay.Id}，" +
                $"文件={Path.GetFileName(capture.Path)}；纹理已在恢复显示前准备。");
            Publish(L("Capture.Ready"), SelectionState.Idle);
        }
        catch (Exception exception)
        {
            _log.Error($"{DiagnosticTag()} [capture] 捕获失败。", exception);
            Publish(LF("Capture.Failed", exception.Message), SelectionState.Idle, true);
        }
        finally
        {
            _captureTask = null;
            _capturePlane = null;
            _currentPlane = null;
            _lockedPlane = null;
            RestoreInteractiveOverlays("截图完成");
            HandleTransition(_selection.Complete(DateTimeOffset.Now), CancellationToken.None);
        }
    }

    private async Task CompleteSubmissionsIfReadyAsync()
    {
        var completed = _submissions.Values
            .Where(submission => submission.Task.IsCompleted)
            .ToArray();
        foreach (var submission in completed)
        {
            await CompleteSubmissionAsync(submission);
        }
    }

    private async Task CompleteSubmissionAsync(ActiveSubmission submission)
    {
        var diagnosticTag = DiagnosticTag(submission.OperationId, submission.RequestId);
        try
        {
            var result = await submission.Task;
            var text = string.IsNullOrWhiteSpace(result.Text)
                ? LF("Translation.CaptureSaved", Path.GetFileName(result.CapturePath))
                : result.Text;
            AssistantConversationView? chat = null;
            if (result.Mode == AssistantRequestMode.CustomCommand &&
                submission.Conversation is not null &&
                !string.IsNullOrWhiteSpace(result.CustomCommand))
            {
                text = submission.Conversation.Append(result.CustomCommand, text);
                chat = submission.Conversation.CreateView();
            }
            var prepared = await PrepareResultContentAsync(
                result,
                text,
                diagnosticTag,
                submission.Cancellation.Token);
            var resultOverlay = FindOverlay(submission.OverlayId);
            if (resultOverlay?.Result is { } resultState)
            {
                resultOverlay.IsPresentationDeferred = false;
                resultState.Complete(
                    submission.RequestId,
                    prepared.DisplayText,
                    isError: false,
                    prepared.Format,
                    prepared.VisibleText,
                    prepared.RenderedImage,
                    chat);
                resultOverlay.IsDirty = true;
                resultOverlay.SynchronizeTextureBuffers = true;
            }
            var operationName = result.Mode switch
            {
                AssistantRequestMode.CustomCommand => L("Translation.Operation.Custom"),
                AssistantRequestMode.LayoutTranslate => L("Translation.Operation.Layout"),
                _ => L("Translation.Operation.Direct")
            };
            _log.Info(
                $"{diagnosticTag} [submit] {operationName}完成：" +
                $"总耗时={submission.Stopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
                $"结果字符={result.Text?.Length ?? 0}，流式更新={submission.ResultUpdateSequence}，" +
                $"格式={prepared.Format}，渲染图={(prepared.RenderedImage?.Length ?? 0):N0} bytes，" +
                $"截图={Path.GetFileName(result.CapturePath)}");
            Publish(LF("Translation.Completed", operationName), _selection.Snapshot.State);
        }
        catch (OperationCanceledException) when (submission.Cancellation.IsCancellationRequested)
        {
            _log.Info($"{diagnosticTag} [submit] 请求已因结果窗口关闭或服务停止而取消。");
        }
        catch (NoSpeechRecognizedException exception)
        {
            _log.Info(
                $"{diagnosticTag} [submit] 未识别到有效语音，不显示新的对话窗口：{exception.Message}");
            var resultOverlay = FindOverlay(submission.OverlayId);
            if (resultOverlay?.Result is { } resultState &&
                submission.PreviousResult is { } previousResult)
            {
                resultState.Restore(previousResult);
                resultOverlay.IsPresentationDeferred = false;
                resultOverlay.IsDirty = true;
                resultOverlay.SynchronizeTextureBuffers = true;
            }
            else if (resultOverlay is not null)
            {
                RemoveInteractiveOverlay(resultOverlay.Id, "语音命令未识别到声音", publishStatus: false);
            }
            Publish(L("Command.NoSpeech"), SelectionState.Idle);
        }
        catch (Exception exception)
        {
            _log.Error(
                $"{diagnosticTag} [submit] 捕获或翻译失败：" +
                $"总耗时={submission.Stopwatch.Elapsed.TotalMilliseconds:F0} ms。",
                exception);
            var resultOverlay = FindOverlay(submission.OverlayId);
            if (resultOverlay?.Result is { } resultState)
            {
                resultOverlay.IsPresentationDeferred = false;
                if (submission.PreviousResult is { } previousResult)
                {
                    resultState.Restore(previousResult);
                }
                else
                {
                    var errorText = LF("Translation.ErrorText", exception.Message);
                    resultState.Complete(
                        submission.RequestId,
                        errorText,
                        isError: true,
                        ResultContentFormat.PlainText,
                        ResultContentFormatter.NormalizeVisibleText(errorText),
                        chat: submission.Conversation?.CreateView(
                            submission.CustomCommand,
                            errorText));
                }
                resultOverlay.IsDirty = true;
                resultOverlay.SynchronizeTextureBuffers = true;
            }
            Publish(LF("Translation.Failed", exception.Message), _selection.Snapshot.State, true);
        }
        finally
        {
            submission.Stopwatch.Stop();
            _submissions.Remove(submission.OperationId);
            submission.Cancellation.Dispose();
            RenderInteractiveOverlays(force: true);
        }
    }

    private async Task<PreparedResultContent> PrepareResultContentAsync(
        TranslationResult result,
        string text,
        string diagnosticTag,
        CancellationToken cancellationToken)
    {
        if (result.ContentFormat == ResultContentFormat.Html)
        {
            try
            {
                _log.Info(
                    $"{diagnosticTag} [html] 开始离屏渲染：" +
                    $"源分辨率={result.SourcePixelWidth}x{result.SourcePixelHeight}，" +
                    $"HTML={text.Length} 字符。");
                var rendered = await _htmlResultRenderer.Value.RenderAsync(
                    text,
                    result.SourcePixelWidth,
                    result.SourcePixelHeight,
                    cancellationToken);
                return new PreparedResultContent(
                    rendered.VisibleText,
                    rendered.VisibleText,
                    ResultContentFormat.Html,
                    rendered.RenderedImage);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.Error(
                    $"{diagnosticTag} [html] 排版结果渲染失败，已回退纯文本窗口。",
                    exception);
                var fallback = ResultContentFormatter.ExtractHtmlVisibleText(text);
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    fallback = L("Translation.LayoutNoVisibleText");
                }
                return new PreparedResultContent(
                    fallback,
                    fallback,
                    ResultContentFormat.PlainText,
                    null);
            }
        }

        if (result.ContentFormat == ResultContentFormat.Markdown)
        {
            try
            {
                var visibleText = _renderer.ExtractVisibleText(ResultContentFormat.Markdown, text);
                return new PreparedResultContent(
                    text,
                    visibleText,
                    ResultContentFormat.Markdown,
                    null);
            }
            catch (Exception exception)
            {
                _log.Error(
                    $"{diagnosticTag} [markdown] Markdown 解析失败，已回退纯文本窗口。",
                    exception);
            }
        }

        var plainText = ResultContentFormatter.NormalizeVisibleText(text);
        return new PreparedResultContent(
            plainText,
            plainText,
            ResultContentFormat.PlainText,
            null);
    }

    private void QueueResultUpdate(
        ActiveSubmission submission,
        string text,
        AssistantConversationView? chat = null)
    {
        var sequence = submission.NextResultUpdateSequence();
        _resultUpdates.Enqueue(
            new ResultProgressUpdate(
                submission.OverlayId,
                submission.RequestId,
                submission.OperationId,
                sequence,
                text,
                DateTimeOffset.Now,
                chat));
    }

    private void DrainResultUpdates()
    {
        while (_resultUpdates.TryDequeue(out var update))
        {
            var overlay = FindOverlay(update.OverlayId);
            var accepted = overlay?.Result?.Update(update.RequestId, update.Text, update.Chat) == true;
            var queueDelay = DateTimeOffset.Now - update.QueuedAt;
            if (accepted)
            {
                overlay!.IsPresentationDeferred = false;
                overlay!.IsDirty = true;
                overlay.SynchronizeTextureBuffers = true;
            }

            if (!accepted || update.Sequence == 1 || update.Sequence % 10 == 0)
            {
                var message =
                    $"{DiagnosticTag(update.OperationId, update.RequestId)} [overlay] " +
                    $"流式更新：分块={update.Sequence}，字符={update.Text.Length}，" +
                    $"队列延迟={queueDelay.TotalMilliseconds:F1} ms，接受={accepted}";
                if (accepted)
                {
                    _log.Info(message);
                }
                else
                {
                    _log.Warning(message);
                }
            }
        }
    }

    private void RestoreInteractiveOverlays(string reason)
    {
        if (!_interactiveOverlaysSuppressed)
        {
            return;
        }

        _interactiveOverlaysSuppressed = false;
        if (_spatialOverlaysVisible)
        {
            RenderInteractiveOverlays();
        }
        _log.Info($"{DiagnosticTag()} [overlay] 恢复空间对象：原因={reason}，数量={_interactiveOverlays.Count}。");
    }

    private void DrainWpfOverlayCommands()
    {
        while (_wpfOverlayCommands.TryDequeue(out var command))
        {
            try
            {
                switch (command)
                {
                    case ShowWpfOverlayCommand show:
                    {
                        var source = new WpfWindowOverlaySource(show.Window, show.Options);
                        try
                        {
                            var plane = CreateWpfWindowPlane(source);
                            var createdOverlay = AddInteractiveOverlay(
                                InteractiveOverlayKind.Window,
                                plane,
                                capture: null,
                                resultState: null,
                                source,
                                source.Options.CanGrab,
                                showToolbarWhenGrabbed: source.Options.ShowToolbarWhenGrabbed);
                            _spatialOverlaysVisible = true;
                            createdOverlay.IsDirty = true;
                            _log.Info(
                                $"[overlay] WPF 窗口已注册：Overlay={createdOverlay.Id}，" +
                                $"名称={source.Options.Name}，宽度={plane.Width:F3}m，" +
                                $"高宽比={source.AspectRatio:F3}。");
                            show.Completion.TrySetResult(createdOverlay.Id);
                        }
                        catch
                        {
                            source.Dispose();
                            throw;
                        }
                        break;
                    }
                    case CloseWpfOverlayCommand close:
                    {
                        var closingOverlay = FindOverlay(close.OverlayId);
                        var removed = closingOverlay?.WindowSource is not null;
                        if (removed)
                        {
                            _diagnosticPressedOverlayIds.Remove(close.OverlayId);
                            RemoveInteractiveOverlay(close.OverlayId, "WPF 扩展接口关闭");
                        }
                        close.Completion.TrySetResult(removed);
                        break;
                    }
                    case InvalidateWpfOverlayCommand invalidate:
                        if (FindOverlay(invalidate.OverlayId) is { WindowSource: not null } invalidatedOverlay)
                        {
                            invalidatedOverlay.IsDirty = true;
                        }
                        break;
                    case ResizeWpfOverlayCommand resize:
                        if (FindOverlay(resize.OverlayId) is { WindowSource: { } resizingSource } resizingOverlay)
                        {
                            var width = Math.Clamp(resize.WidthMeters, 0.06f, 2.5f);
                            var height = width / (float)resizingSource.AspectRatio;
                            resizingOverlay.Plane = resizingOverlay.Plane with
                            {
                                Width = width,
                                Height = height,
                                OverlayExtent = MathF.Max(MathF.Max(width, height) * 1.08f, 0.12f)
                            };
                            resizingOverlay.IsChromeDirty = true;
                            resizingOverlay.IsProgressDirty = true;
                            _pointerStabilizer.Reset();
                            _pointerRayStabilizer.Reset();
                            _log.Info(
                                $"[overlay] WPF 窗口尺寸已热更新：Overlay={resize.OverlayId}，" +
                                $"尺寸={width:F3}x{height:F3}m。");
                        }
                        break;
                }
            }
            catch (Exception exception)
            {
                _log.Error("处理 WPF 空间窗口命令失败。", exception);
                switch (command)
                {
                    case ShowWpfOverlayCommand show:
                        show.Completion.TrySetException(exception);
                        break;
                    case CloseWpfOverlayCommand close:
                        close.Completion.TrySetException(exception);
                        break;
                }
            }
        }
    }

    private void CancelPendingWpfOverlayCommands()
    {
        while (_wpfOverlayCommands.TryDequeue(out var command))
        {
            var exception = new OperationCanceledException("SteamVR 服务已停止。");
            switch (command)
            {
                case ShowWpfOverlayCommand show:
                    show.Completion.TrySetException(exception);
                    break;
                case CloseWpfOverlayCommand close:
                    close.Completion.TrySetException(exception);
                    break;
            }
        }
    }

    private void DrainDiagnosticResultOverlayCommands()
    {
        while (_diagnosticResultOverlayCommands.TryDequeue(out var command))
        {
            try
            {
                switch (command)
                {
                    case ShowDiagnosticMarkdownOverlayCommand show:
                    {
                        var state = new ResultOverlayState();
                        var requestId = state.Begin(show.Markdown, ResultContentFormat.Markdown);
                        state.Complete(
                            requestId,
                            show.Markdown,
                            isError: false,
                            ResultContentFormat.Markdown,
                            _renderer.ExtractVisibleText(ResultContentFormat.Markdown, show.Markdown));
                        var createdOverlay = AddInteractiveOverlay(
                            InteractiveOverlayKind.Result,
                            CreateDiagnosticResultPlane(),
                            capture: null,
                            state,
                            canGrab: true,
                            showToolbarWhenGrabbed: false);
                        _spatialOverlaysVisible = true;
                        createdOverlay.IsDirty = true;
                        createdOverlay.SynchronizeTextureBuffers = true;
                        show.Completion.TrySetResult(createdOverlay.Id);
                        break;
                    }
                    case ShowDiagnosticChatOverlayCommand show:
                    {
                        var state = new ResultOverlayState();
                        state.Begin(
                            DiagnosticChatText(show.Chat),
                            ResultContentFormat.Markdown,
                            show.Chat);
                        var createdOverlay = AddInteractiveOverlay(
                            InteractiveOverlayKind.Result,
                            CreateDiagnosticResultPlane(),
                            capture: null,
                            state,
                            canGrab: true,
                            showToolbarWhenGrabbed: false);
                        _spatialOverlaysVisible = true;
                        createdOverlay.IsDirty = true;
                        createdOverlay.SynchronizeTextureBuffers = true;
                        show.Completion.TrySetResult(createdOverlay.Id);
                        break;
                    }
                    case UpdateDiagnosticChatOverlayCommand update:
                    {
                        if (FindOverlay(update.OverlayId) is { Result: { } chatResult } chatOverlay &&
                            chatResult.Snapshot() is { } snapshot &&
                            chatResult.Update(
                                snapshot.RequestId,
                                DiagnosticChatText(update.Chat),
                                update.Chat))
                        {
                            chatOverlay.IsDirty = true;
                        }
                        break;
                    }
                    case ScrollDiagnosticResultOverlayCommand scroll:
                        if (FindOverlay(scroll.OverlayId) is { Result: { } result } overlay &&
                            result.Scroll(scroll.Delta, overlay.MaximumScroll))
                        {
                            overlay.IsDirty = true;
                        }
                        break;
                    case CloseDiagnosticResultOverlayCommand close:
                    {
                        var closingOverlay = FindOverlay(close.OverlayId);
                        var removed = closingOverlay?.Result is not null;
                        if (removed)
                        {
                            RemoveInteractiveOverlay(
                                close.OverlayId,
                                "诊断结果窗口关闭",
                                publishStatus: false);
                        }
                        close.Completion.TrySetResult(removed);
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                _log.Error("处理诊断结果窗口命令失败。", exception);
                switch (command)
                {
                    case ShowDiagnosticMarkdownOverlayCommand show:
                        show.Completion.TrySetException(exception);
                        break;
                    case ShowDiagnosticChatOverlayCommand show:
                        show.Completion.TrySetException(exception);
                        break;
                    case CloseDiagnosticResultOverlayCommand close:
                        close.Completion.TrySetException(exception);
                        break;
                }
            }
        }
    }

    private void CancelPendingDiagnosticResultOverlayCommands()
    {
        while (_diagnosticResultOverlayCommands.TryDequeue(out var command))
        {
            var exception = new OperationCanceledException("SteamVR 服务已停止。");
            switch (command)
            {
                case ShowDiagnosticMarkdownOverlayCommand show:
                    show.Completion.TrySetException(exception);
                    break;
                case ShowDiagnosticChatOverlayCommand show:
                    show.Completion.TrySetException(exception);
                    break;
                case CloseDiagnosticResultOverlayCommand close:
                    close.Completion.TrySetException(exception);
                    break;
            }
        }
    }

    private static string DiagnosticChatText(AssistantConversationView chat)
    {
        if (!string.IsNullOrWhiteSpace(chat.PendingAnswer))
        {
            return chat.PendingAnswer;
        }
        if (chat.Turns.Count > 0)
        {
            return chat.Turns[^1].Assistant;
        }
        return "...";
    }

    private void DrainDiagnosticWpfPointerCommands()
    {
        DiagnosticWpfPointerCommand? latest = null;
        while (_diagnosticWpfPointerCommands.TryDequeue(out var command))
        {
            latest = command;
        }
        if (latest is not { } pointer)
        {
            return;
        }

        if (FindOverlay(pointer.OverlayId) is not { WindowSource: { } source } overlay)
        {
            _diagnosticPressedOverlayIds.Remove(pointer.OverlayId);
            return;
        }

        var processedAt = Stopwatch.GetTimestamp();
        NormalizedPoint? resolvedTexturePoint = null;
        NormalizedPoint? resolvedContentPoint = null;
        Vector3f? resolvedWorldPoint = null;
        if (pointer.Leave)
        {
            source.PointerLeave();
            SetPointerVisual(null, null);
        }
        else if (pointer.UseOpenVrIntersection)
        {
            var target = overlay.Plane.Center +
                         (overlay.Plane.Right * ((pointer.TexturePoint.X - 0.5f) * overlay.Plane.Width)) +
                         (overlay.Plane.Up * ((0.5f - pointer.TexturePoint.Y) * overlay.Plane.Height));
            var raySource = target + (overlay.Plane.Normal * 0.5f);
            var rayDirection = overlay.Plane.Normal * -1f;
            if (_spatialOverlayManager?.TryIntersect(
                    pointer.OverlayId,
                    raySource,
                    rayDirection,
                    CurrentTrackingOrigin(),
                    overlay.Plane,
                    out var hit) == true)
            {
                resolvedTexturePoint = hit.TexturePoint;
                resolvedContentPoint = hit.ContentPoint;
                resolvedWorldPoint = hit.WorldPoint;
            }
        }
        else
        {
            resolvedTexturePoint = pointer.TexturePoint.Clamp();
            resolvedContentPoint = SpatialQuadOverlayMath.MapTextureToContent(
                resolvedTexturePoint.Value);
        }

        var contentPoint = resolvedContentPoint ?? default;
        if (!pointer.Leave && resolvedContentPoint is not null && pointer.DispatchInput)
        {
            var wasPressed = _diagnosticPressedOverlayIds.Contains(pointer.OverlayId);
            if (pointer.IsPressed)
            {
                if (wasPressed)
                {
                    source.PointerMove(contentPoint);
                }
                else
                {
                    source.PointerDown(contentPoint);
                    _diagnosticPressedOverlayIds.Add(pointer.OverlayId);
                }
            }
            else if (wasPressed)
            {
                source.PointerUp(contentPoint);
                _diagnosticPressedOverlayIds.Remove(pointer.OverlayId);
            }
            else
            {
                source.PointerMove(contentPoint);
            }
        }
        else if (!pointer.Leave && resolvedContentPoint is not null)
        {
            source.PointerMove(contentPoint);
        }
        if (!pointer.Leave && resolvedTexturePoint is { } texturePoint)
        {
            SetPointerVisual(
                pointer.OverlayId,
                new OverlayPointerVisual(
                    ETrackedControllerRole.RightHand,
                    texturePoint,
                    contentPoint,
                    pointer.IsPressed,
                    WorldPoint: resolvedWorldPoint));
        }
        DiagnosticWpfPointerProcessed?.Invoke(
            this,
            new DiagnosticWpfPointerProcessedEventArgs(
                pointer.RequestId,
                pointer.OverlayId,
                pointer.IsPressed,
                pointer.TexturePoint,
                resolvedTexturePoint,
                source.GetHoveredElementNameForDiagnostics(),
                pointer.UseOpenVrIntersection,
                pointer.Leave,
                processedAt,
                Stopwatch.GetElapsedTime(pointer.RequestedTimestamp, processedAt)));
    }

    private void DrainDiagnosticWpfInteractionCommands()
    {
        while (_diagnosticWpfInteractionCommands.TryDequeue(out var command))
        {
            var processedAt = Stopwatch.GetTimestamp();
            var overlay = FindOverlay(command.OverlayId);
            var source = overlay?.WindowSource;
            if (overlay is null || source is null)
            {
                PublishDiagnosticInteractionFailure(command, processedAt, "Overlay is not available.");
                continue;
            }

            NormalizedPoint texturePoint;
            var succeeded = true;
            string? failure = null;
            switch (command.Kind)
            {
                case DiagnosticWpfInteractionKind.Click:
                {
                    var center = source.FindNamedElementCenter(command.ControlName ?? string.Empty);
                    if (center is not { } point)
                    {
                        succeeded = false;
                        failure = $"Control '{command.ControlName}' is unavailable.";
                        texturePoint = command.TexturePoint;
                        break;
                    }

                    texturePoint = point;
                    source.PointerMove(point);
                    SetPointerVisual(
                        command.OverlayId,
                        new OverlayPointerVisual(
                            ETrackedControllerRole.RightHand,
                            point,
                            point,
                            IsPressed: true));
                    source.PointerDown(point);
                    succeeded = source.PointerUp(point) != false;
                    if (!succeeded)
                    {
                        failure = $"Control '{command.ControlName}' did not accept the click.";
                    }
                    break;
                }
                case DiagnosticWpfInteractionKind.Scroll:
                    texturePoint = command.TexturePoint;
                    source.PointerWheel(texturePoint, command.WheelDelta);
                    break;
                default:
                    texturePoint = command.TexturePoint;
                    succeeded = false;
                    failure = "Unknown diagnostic interaction.";
                    break;
            }

            SetPointerVisual(
                command.OverlayId,
                new OverlayPointerVisual(
                    ETrackedControllerRole.RightHand,
                    texturePoint,
                    texturePoint,
                    IsPressed: false));
            if (!succeeded)
            {
                PublishDiagnosticInteractionFailure(command, processedAt, failure);
                continue;
            }

            if (!_pendingDiagnosticWpfInteractions.TryGetValue(command.OverlayId, out var pending))
            {
                pending = [];
                _pendingDiagnosticWpfInteractions[command.OverlayId] = pending;
            }
            pending.Add(new PendingDiagnosticWpfInteraction(
                command.RequestId,
                command.OverlayId,
                command.Kind,
                command.ControlName,
                command.RequestedTimestamp,
                processedAt));
        }
    }

    private void PublishDiagnosticInteractionFailure(
        DiagnosticWpfInteractionCommand command,
        long processedAt,
        string? failure)
    {
        DiagnosticWpfInteractionCompleted?.Invoke(
            this,
            new DiagnosticWpfInteractionCompletedEventArgs(
                command.RequestId,
                command.OverlayId,
                command.Kind,
                command.ControlName,
                succeeded: false,
                processedAt,
                Stopwatch.GetElapsedTime(command.RequestedTimestamp, processedAt),
                TimeSpan.Zero,
                failure));
    }

    private void CompletePendingDiagnosticWpfInteractions(long overlayId, long renderedAt)
    {
        if (!_pendingDiagnosticWpfInteractions.Remove(overlayId, out var pending))
        {
            return;
        }

        foreach (var interaction in pending)
        {
            DiagnosticWpfInteractionCompleted?.Invoke(
                this,
                new DiagnosticWpfInteractionCompletedEventArgs(
                    interaction.RequestId,
                    interaction.OverlayId,
                    interaction.Kind,
                    interaction.ControlName,
                    succeeded: true,
                    renderedAt,
                    Stopwatch.GetElapsedTime(interaction.RequestedTimestamp, renderedAt),
                    Stopwatch.GetElapsedTime(interaction.ProcessedTimestamp, renderedAt),
                    null));
        }
    }

    private SpatialSelectionPlane CreateWpfWindowPlane(WpfWindowOverlaySource source)
    {
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            CurrentTrackingOrigin(),
            0f,
            poses);
        var hmdPose = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!hmdPose.bPoseIsValid)
        {
            throw new InvalidOperationException("头显姿态不可用，无法放置 WPF 窗口。");
        }

        var matrix = hmdPose.mDeviceToAbsoluteTracking;
        var forward = Forward(matrix);
        var right = Right(matrix);
        var normal = forward * -1f;
        var up = Vector3f.Cross(normal, right).Normalized();
        var width = source.Options.WidthMeters;
        var height = width / (float)source.AspectRatio;
        var extent = MathF.Max(MathF.Max(width, height) * 1.08f, 0.12f);
        var center = Position(matrix) + (forward * source.Options.DistanceMeters);
        if (source.Options.Placement == WpfSpatialOverlayPlacement.LeftHand &&
            ReadControllerPose(ETrackedControllerRole.LeftHand, poses) is { } leftHand)
        {
            center = leftHand.Position + (up * (height * 0.62f)) + (forward * 0.04f);
        }
        return new SpatialSelectionPlane(
            center,
            right,
            up,
            normal,
            width,
            height,
            extent,
            new NormalizedPoint(0f, 0f),
            new NormalizedPoint(1f, 1f),
            0f);
    }

    private SpatialSelectionPlane CreateDiagnosticResultPlane()
    {
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            CurrentTrackingOrigin(),
            0f,
            poses);
        var hmdPose = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!hmdPose.bPoseIsValid)
        {
            throw new InvalidOperationException("头显姿态不可用，无法放置诊断结果窗口。");
        }

        var matrix = hmdPose.mDeviceToAbsoluteTracking;
        var forward = Forward(matrix);
        var right = Right(matrix);
        var normal = forward * -1f;
        var up = Vector3f.Cross(normal, right).Normalized();
        const float width = 0.36f;
        const float height = 0.24f;
        return new SpatialSelectionPlane(
            Position(matrix) + (forward * ResultPlaneDistance),
            right,
            up,
            normal,
            width,
            height,
            0.40f,
            new NormalizedPoint(0f, 0f),
            new NormalizedPoint(1f, 1f),
            0f);
    }

    private void StartSpeechWarmup()
    {
        if (_speechWarmupTask is not null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _log.Info("[asr] 开始后台预热 SenseVoice。");
        _speechWarmupTask = Task.Run(() =>
            new SenseVoiceCommandTranscriber(_configuration.Speech, _log));
        _ = _speechWarmupTask.ContinueWith(
            _ =>
            {
                stopwatch.Stop();
                _log.Info($"[asr] 后台预热完成：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms。");
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
        _ = _speechWarmupTask.ContinueWith(
            task =>
            {
                stopwatch.Stop();
                _log.Error(
                    $"[asr] SenseVoice 后台预热失败：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms。" +
                    "普通短按翻译仍可使用；长按命令将报告此错误。",
                    task.Exception?.GetBaseException());
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void UpdateVrChatVoiceInput(bool physicallyPressed, CancellationToken cancellationToken)
    {
        lock (_voiceInputSync)
        {
            _vrVoiceInputPressed = physicallyPressed;
            UpdateCombinedVrChatVoiceInput(cancellationToken);
        }
    }

    private void UpdateCombinedVrChatVoiceInput(CancellationToken cancellationToken)
    {
        var physicallyPressed = _vrVoiceInputPressed || _desktopVoiceInputPressed;
        if (physicallyPressed)
        {
            _voiceInputReleaseCandidateAt = null;
            if (!_voiceInputPressed && !_voiceInputWasPhysicallyPressed)
            {
                StartVrChatVoiceInput(cancellationToken);
            }

            _voiceInputWasPhysicallyPressed = true;
            return;
        }

        _voiceInputWasPhysicallyPressed = false;
        if (!_voiceInputPressed)
        {
            _voiceInputReleaseCandidateAt = null;
            return;
        }

        var now = Environment.TickCount64;
        _voiceInputReleaseCandidateAt ??= now;
        if (now - _voiceInputReleaseCandidateAt.Value < VoiceInputReleaseDebounceMilliseconds)
        {
            return;
        }

        _voiceInputReleaseCandidateAt = null;
        _voiceInputPressed = false;
        _voiceInputTask = CompleteVrChatVoiceInputAsync(cancellationToken);
    }

    private async Task CompleteDesktopVoiceReleaseAfterDebounceAsync()
    {
        try
        {
            await Task.Delay(VoiceInputReleaseDebounceMilliseconds + 10);
            lock (_voiceInputSync)
            {
                UpdateCombinedVrChatVoiceInput(_cancellation?.Token ?? CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _log.Error("[voice-input] 桌面 PTT 松开处理失败。", exception);
        }
    }

    private void StartVrChatVoiceInput(CancellationToken cancellationToken)
    {
        if (_voiceInputTask is { IsCompleted: false })
        {
            _log.Warning("[voice-input] 上一次识别仍在进行，忽略新的 PTT 按下。");
            return;
        }

        try
        {
            _voiceInputRecorder.Start();
            _voiceInputPressed = true;
            _log.Info("[voice-input] PTT 按下，开始 VRChat 语音输入录音。");
            _ = SetVrChatTypingSafeAsync(true, cancellationToken);
            Publish(L("Voice.Recording"), _selection.Snapshot.State);
        }
        catch (Exception exception)
        {
            _voiceInputPressed = false;
            _log.Error("[voice-input] 启动录音失败。", exception);
            Publish(LF("Voice.RecordFailed", exception.Message), _selection.Snapshot.State, true);
        }
    }

    private async Task CompleteVrChatVoiceInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var audio = await _voiceInputRecorder.StopAsync(cancellationToken);
            _log.Info(
                $"[voice-input] PTT 松开：录音={audio.Duration.TotalMilliseconds:F0} ms，开始 SenseVoice 识别。");
            Publish(L("Voice.Recognizing"), _selection.Snapshot.State);
            var transcriber = await GetSpeechTranscriberAsync();
            var result = await transcriber.TranscribeAsync(audio, "[voice-input]", cancellationToken);
            if (_vrChatOscOutput is null)
            {
                throw new InvalidOperationException("VRChat OSC 输出未初始化。");
            }

            var sourceText = result.Text.Trim();
            var outputText = sourceText;
            var translationSucceeded = false;
            if (_configuration.VrChatVoiceInput.TranslationEnabled)
            {
                var oscPlan = VoiceTranslationOscPlan.Create(
                    _configuration.VrChatVoiceInput.TranslationDisplayMode,
                    _configuration.VrChatVoiceInput.SendImmediately);
                if (oscPlan.ShowOriginal)
                {
                    await _vrChatOscOutput.SendAsync(
                        sourceText,
                        oscPlan.SendOriginalImmediately,
                        cancellationToken);
                    _log.Info(
                        $"[voice-input] OSC 已输出待翻译原文：字符={sourceText.Length}，" +
                        $"立即发送={_configuration.VrChatVoiceInput.SendImmediately}，" +
                        $"文本={PreviewText(sourceText)}");
                }

                Publish(L("Voice.Translating"), _selection.Snapshot.State);
                try
                {
                    outputText = await _pipeline.TranslateSpeechAsync(
                        sourceText,
                        _configuration.VrChatVoiceInput.TranslationTargetLanguage,
                        _configuration.VrChatVoiceInput.TranslationSystemPrompt,
                        _configuration.VrChatVoiceInput.TranslationPrompt,
                        cancellationToken,
                        "[voice-input]");
                    translationSucceeded = true;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _log.Error("[voice-input] 语音翻译失败，将发送原始识别文本。", exception);
                    Publish(
                        LF("Voice.TranslationFallback", exception.Message),
                        _selection.Snapshot.State,
                        true);
                }

                if (oscPlan.ShowOriginal)
                {
                    if (translationSucceeded)
                    {
                        if (oscPlan.UpdateSubmittedOriginal)
                        {
                            await _vrChatOscOutput.UpdateSubmittedChatboxAsync(
                                outputText,
                                cancellationToken);
                        }
                        else
                        {
                            await _vrChatOscOutput.SendPreviewAsync(
                                outputText,
                                cancellationToken);
                        }
                        _log.Info(
                            $"[voice-input] OSC 已用译文更新 Chatbox 输入框：字符={outputText.Length}，" +
                            $"原文已提交={oscPlan.UpdateSubmittedOriginal}，" +
                            $"更新通知音=False，文本={PreviewText(outputText)}");
                        Publish(LF("Voice.Sent", outputText), _selection.Snapshot.State);
                    }
                    return;
                }
            }

            var translationPlan = VoiceTranslationOscPlan.Create(
                _configuration.VrChatVoiceInput.TranslationDisplayMode,
                _configuration.VrChatVoiceInput.SendImmediately);
            await _vrChatOscOutput.SendAsync(
                outputText,
                _configuration.VrChatVoiceInput.TranslationEnabled
                    ? translationPlan.SendTranslationImmediately
                    : _configuration.VrChatVoiceInput.SendImmediately,
                cancellationToken);
            _log.Info(
                $"[voice-input] OSC 已发送：目标={_configuration.VrChatVoiceInput.Host}:" +
                $"{_configuration.VrChatVoiceInput.Port}，字符={result.Text.Length}，" +
                $"分段间隔={_configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds} ms，" +
                $"文本={PreviewText(outputText)}");
            Publish(LF("Voice.Sent", outputText), _selection.Snapshot.State);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Info("[voice-input] VRChat 语音输入已取消。");
        }
        catch (Exception exception)
        {
            _log.Error("[voice-input] 识别或 OSC 发送失败。", exception);
            Publish(LF("Voice.Failed", exception.Message), _selection.Snapshot.State, true);
        }
        finally
        {
            await SetVrChatTypingSafeAsync(false, CancellationToken.None);
        }
    }

    private async Task SetVrChatTypingSafeAsync(bool isTyping, CancellationToken cancellationToken)
    {
        if (_vrChatOscOutput is null)
        {
            return;
        }

        try
        {
            await _vrChatOscOutput.SetTypingAsync(isTyping, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            _log.Error($"[voice-input] OSC typing={isTyping} 发送失败。", exception);
        }
    }

    private static string PreviewText(string text) =>
        text.Length <= 120 ? text : text[..120] + "...";

    private async Task<SenseVoiceCommandTranscriber> GetSpeechTranscriberAsync()
    {
        StartSpeechWarmup();
        try
        {
            return await _speechWarmupTask!;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"SenseVoice 不可用：{exception.GetBaseException().Message}",
                exception);
        }
    }

    private bool InitializeOpenVr()
    {
        _log.Info("开始以被动 Background 客户端连接已运行的 SteamVR。");
        if (!Environment.Is64BitProcess)
        {
            throw new InvalidOperationException("SteamVR 模块仅支持 Windows x64。");
        }

        var assets = SteamVrManifestStore.EnsureExtracted();
        _log.Info($"SteamVR 动作清单：{assets.ActionManifestPath}");
        var initError = EVRInitError.None;
        _ = OpenVR.Init(ref initError, OpenVrApplicationType);
        if (initError == EVRInitError.Init_NoServerForBackgroundApp)
        {
            _log.Info("SteamVR 尚未接受连接；保持被动等待，不启动运行时。");
            return false;
        }

        if (initError != EVRInitError.None)
        {
            throw new InvalidOperationException(OpenVR.GetStringForHmdError(initError));
        }

        _openVrInitialized = true;
        _log.Info("[steamvr-connect] OpenVR Background 客户端已连接运行时。");
        if (OpenVR.Applications is null ||
            OpenVR.Input is null ||
            OpenVR.Overlay is null ||
            OpenVR.System is null)
        {
            throw new InvalidOperationException(
                "SteamVR 服务已连接，但合成器接口尚未就绪；程序将保持被动重试。");
        }

        var manifestError = OpenVR.Applications.AddApplicationManifest(
            assets.ApplicationManifestPath,
            bTemporary: false);
        var manifestRegistration = new SteamVrApplicationRegistrationStatus(
            manifestError,
            EVRApplicationError.None);
        if (!manifestRegistration.CanContinue)
        {
            throw new InvalidOperationException($"注册 SteamVR 应用清单失败：{manifestError}");
        }

        var identityError = OpenVR.Applications.IdentifyApplication(
            unchecked((uint)Environment.ProcessId),
            SteamVrManifestStore.ApplicationKey);
        if (identityError != EVRApplicationError.None)
        {
            _log.Warning(
                $"[steamvr-connect] SteamVR 暂未识别当前应用身份：{identityError}。" +
                "这通常发生在 SteamVR 已运行后首次注册清单；身份识别不是连接硬门槛，" +
                "将继续直接加载动作清单。");
        }
        else
        {
            _log.Info(
                $"[steamvr-connect] 应用清单与进程身份已就绪：Manifest={manifestError}。");
        }

        EnsureInput(OpenVR.Input.SetActionManifestPath(assets.ActionManifestPath), "载入动作清单");
        ResolveActionSet(GlobalActionSetPath, ref _globalActionSetHandle);
        ResolveActionSet(SelectionActionSetPath, ref _selectionActionSetHandle);
        ResolveActionSet(ResultsActionSetPath, ref _resultsActionSetHandle);
        ResolveActionSet(VoiceInputActionSetPath, ref _voiceInputActionSetHandle);
        ResolveAction(ToggleActionPath, ref _toggleActionHandle);
        ResolveAction(LeftTriggerActionPath, ref _leftTriggerActionHandle);
        ResolveAction(RightTriggerActionPath, ref _rightTriggerActionHandle);
        ResolveAction(LeftHapticActionPath, ref _leftHapticActionHandle);
        ResolveAction(RightHapticActionPath, ref _rightHapticActionHandle);
        ResolveAction(ResultScrollActionPath, ref _resultScrollActionHandle);
        ResolveAction(OverlayToggleActionPath, ref _overlayToggleActionHandle);
        ResolveAction(LeftGripActionPath, ref _leftGripActionHandle);
        ResolveAction(RightGripActionPath, ref _rightGripActionHandle);
        ResolveAction(LeftPointerClickActionPath, ref _leftPointerClickActionHandle);
        ResolveAction(RightPointerClickActionPath, ref _rightPointerClickActionHandle);
        ResolveAction(VoiceInputPttActionPath, ref _voiceInputPttActionHandle);
        CreateOverlays();

        _connected = true;
        _log.Info("SteamVR 叠加层和动作输入已连接。高优先级输入覆盖需要 SteamVR 开发者设置支持。");
        Publish(L("Runtime.Connected"), SelectionState.Idle);
        return true;
    }

    private void PublishPassiveWait(string message)
    {
        if (string.Equals(_lastPassiveWaitStatus, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastPassiveWaitStatus = message;
        _lastConnectionError = null;
        _log.Info($"[steamvr-passive] {message}");
        Publish(message, SelectionState.Idle);
    }

    private void CreateOverlays()
    {
        _overlayTextureDevice = new D3D11OverlayDevice(_log);
        CreateOverlay(
            SelectionOverlayKey,
            "SteamVR Translator Selection",
            InstructionPlaneWidth,
            100,
            ref _selectionOverlayHandle);
        _selectionOverlayTexture = new D3D11OverlayTexture(_overlayTextureDevice);
        _selectionOverlayTexture.Attach(_selectionOverlayHandle);
        _spatialOverlayManager = new SpatialQuadOverlayManager(_overlayTextureDevice, _log);

        SetHeadLockedSelectionOverlayTransform();
        foreach (var overlay in _interactiveOverlays)
        {
            overlay.IsDirty = true;
            overlay.IsChromeDirty = true;
            overlay.IsProgressDirty = true;
            overlay.SynchronizeTextureBuffers = true;
            overlay.SynchronizeVideoTextureBuffers = true;
            overlay.SynchronizeChromeTextureBuffers = true;
            overlay.SynchronizeProgressTextureBuffers = true;
            overlay.SynchronizePointerTextureBuffers = true;
            overlay.RenderedPointerPressed = null;
        }
        RenderInteractiveOverlays(force: true);
    }

    private static void CreateOverlay(
        string key,
        string name,
        float width,
        uint sortOrder,
        ref ulong handle)
    {
        var overlayApi = OpenVR.Overlay ?? throw new InvalidOperationException(
            "SteamVR Overlay 接口不可用；合成器可能仍在启动或已经退出。");
        var existing = OpenVR.k_ulOverlayHandleInvalid;
        if (overlayApi.FindOverlay(key, ref existing) == EVROverlayError.None)
        {
            _ = overlayApi.DestroyOverlay(existing);
        }

        EnsureOverlay(
            overlayApi.CreateOverlay(key, name, ref handle),
            $"创建叠加层 {name}");
        EnsureOverlay(overlayApi.SetOverlayWidthInMeters(handle, width), "设置叠加层宽度");
        EnsureOverlay(overlayApi.SetOverlayAlpha(handle, 1f), "设置叠加层透明度");
        EnsureOverlay(overlayApi.SetOverlayColor(handle, 1f, 1f, 1f), "设置叠加层颜色");
        EnsureOverlay(
            overlayApi.SetOverlayTextureColorSpace(handle, EColorSpace.Gamma),
            "设置叠加层色彩空间");
        EnsureOverlay(
            overlayApi.SetOverlayInputMethod(handle, VROverlayInputMethod.None),
            "设置叠加层输入方式");
        EnsureOverlay(overlayApi.SetOverlaySortOrder(handle, sortOrder), "设置叠加层层级");
    }

    private void UpdateActionState()
    {
        var actionSets = new List<VRActiveActionSet_t>
        {
            new()
            {
                ulActionSet = _globalActionSetHandle,
                ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
                ulSecondaryActionSet = OpenVR.k_ulInvalidActionSetHandle,
                nPriority = 0
            }
        };
        if (_configuration.VrChatVoiceInput.Enabled)
        {
            actionSets.Add(new VRActiveActionSet_t
            {
                ulActionSet = _voiceInputActionSetHandle,
                ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
                ulSecondaryActionSet = OpenVR.k_ulInvalidActionSetHandle,
                nPriority = 0
            });
        }
        if (_selection.Snapshot.State is SelectionState.Armed or SelectionState.Sizing or SelectionState.Locked)
        {
            actionSets.Add(new VRActiveActionSet_t
            {
                ulActionSet = _selectionActionSetHandle,
                ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
                ulSecondaryActionSet = OpenVR.k_ulInvalidActionSetHandle,
                nPriority = OpenVR.k_nActionSetOverlayGlobalPriorityMin + 1
            });
        }

        // Keep high-priority interaction actions active only while an overlay is targeted.
        // The global visibility toggle lives in the always-active, normal-priority global set.
        if (_grabs.Count > 0 ||
            _interactionTargetOverlayId is not null ||
            _contactOverlayIds.Count > 0 ||
            _pointerCapture is not null)
        {
            actionSets.Add(new VRActiveActionSet_t
            {
                ulActionSet = _resultsActionSetHandle,
                ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
                ulSecondaryActionSet = OpenVR.k_ulInvalidActionSetHandle,
                nPriority = OpenVR.k_nActionSetOverlayGlobalPriorityMin
            });
        }

        EnsureInput(
            OpenVR.Input.UpdateActionState(
                actionSets.ToArray(),
                unchecked((uint)Marshal.SizeOf<VRActiveActionSet_t>())),
            "更新 SteamVR 动作状态");
    }

    private bool ReadDigital(ulong actionHandle)
    {
        var data = new InputDigitalActionData_t();
        EnsureInput(
            OpenVR.Input.GetDigitalActionData(
                actionHandle,
                ref data,
                unchecked((uint)Marshal.SizeOf<InputDigitalActionData_t>()),
                OpenVR.k_ulInvalidInputValueHandle),
            "读取 SteamVR 数字动作");
        return data.bActive && data.bState;
    }

    private InputAnalogActionData_t ReadAnalog(ulong actionHandle)
    {
        var data = new InputAnalogActionData_t();
        EnsureInput(
            OpenVR.Input.GetAnalogActionData(
                actionHandle,
                ref data,
                unchecked((uint)Marshal.SizeOf<InputAnalogActionData_t>()),
                OpenVR.k_ulInvalidInputValueHandle),
            "读取 SteamVR 模拟动作");
        return data;
    }

    private bool TryReadSpatialPlane(out SpatialSelectionPlane plane)
    {
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            CurrentTrackingOrigin(),
            0f,
            poses);
        var hmdPose = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!hmdPose.bPoseIsValid)
        {
            plane = default;
            return false;
        }

        var leftPosition = ReadControllerPosition(ETrackedControllerRole.LeftHand, poses);
        var rightPosition = ReadControllerPosition(ETrackedControllerRole.RightHand, poses);
        if (leftPosition is null || rightPosition is null)
        {
            plane = default;
            return false;
        }

        var created = SpatialSelectionPlane.TryCreate(
            Position(hmdPose.mDeviceToAbsoluteTracking),
            Forward(hmdPose.mDeviceToAbsoluteTracking),
            Right(hmdPose.mDeviceToAbsoluteTracking),
            leftPosition.Value,
            rightPosition.Value,
            out plane);
        return created;
    }

    private static Vector3f? ReadControllerPosition(
        ETrackedControllerRole role,
        IReadOnlyList<TrackedDevicePose_t> poses)
    {
        var index = OpenVR.System.GetTrackedDeviceIndexForControllerRole(role);
        if (index == OpenVR.k_unTrackedDeviceIndexInvalid || index >= poses.Count || !poses[(int)index].bPoseIsValid)
        {
            return null;
        }

        return Position(poses[(int)index].mDeviceToAbsoluteTracking);
    }

    private static TrackedHandPose? ReadControllerPose(
        ETrackedControllerRole role,
        IReadOnlyList<TrackedDevicePose_t> poses)
    {
        var index = OpenVR.System.GetTrackedDeviceIndexForControllerRole(role);
        if (index == OpenVR.k_unTrackedDeviceIndexInvalid || index >= poses.Count || !poses[(int)index].bPoseIsValid)
        {
            return null;
        }

        var matrix = poses[(int)index].mDeviceToAbsoluteTracking;
        return new TrackedHandPose(
            Position(matrix),
            new Vector3f(matrix.m0, matrix.m4, matrix.m8).Normalized(),
            new Vector3f(matrix.m1, matrix.m5, matrix.m9).Normalized(),
            new Vector3f(matrix.m2, matrix.m6, matrix.m10).Normalized());
    }

    private void UpdateOverlayInteraction(CancellationToken cancellationToken)
    {
        var leftGrip = ReadDigital(_leftGripActionHandle);
        var rightGrip = ReadDigital(_rightGripActionHandle);
        var leftPointerPressed = ReadDigital(_leftPointerClickActionHandle);
        var rightPointerPressed = ReadDigital(_rightPointerClickActionHandle);
        if (_interactiveOverlaysSuppressed || !_spatialOverlaysVisible || _interactiveOverlays.Count == 0)
        {
            SetContactHighlights(null, null);
            SetInteractionTarget(null);
            _interactionContactHand = null;
            _grabs.Clear();
            ResetOverlayPointer();
            _lastLeftGripPressed = leftGrip;
            _lastRightGripPressed = rightGrip;
            _lastLeftPointerPressed = leftPointerPressed;
            _lastRightPointerPressed = rightPointerPressed;
            return;
        }

        var origin = CurrentTrackingOrigin();
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            origin,
            0f,
            poses);
        var leftHand = ReadControllerPose(ETrackedControllerRole.LeftHand, poses);
        var rightHand = ReadControllerPose(ETrackedControllerRole.RightHand, poses);
        var hmdPose = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        var viewerPosition = hmdPose.bPoseIsValid
            ? Position(hmdPose.mDeviceToAbsoluteTracking)
            : (Vector3f?)null;

        var previousGrabCount = _grabs.Count;
        var leftReleased = UpdateGrabForHand(
            ETrackedControllerRole.LeftHand,
            leftGrip,
            leftHand,
            origin);
        var rightReleased = UpdateGrabForHand(
            ETrackedControllerRole.RightHand,
            rightGrip,
            rightHand,
            origin);

        var leftContact = !_grabs.ContainsHand(ETrackedControllerRole.LeftHand) && leftHand is { } left
            ? FindContact(left.Position)
            : null;
        var rightContact = !_grabs.ContainsHand(ETrackedControllerRole.RightHand) && rightHand is { } right
            ? FindContact(right.Position)
            : null;
        if (leftHand is { } leftPose &&
            leftContact is { } leftHit &&
            OverlayGrabTransition.ShouldBegin(
                leftGrip,
                _lastLeftGripPressed,
                leftContact,
                rightReleased))
        {
            BeginGrab(leftHit.OverlayId, ETrackedControllerRole.LeftHand, leftPose);
        }
        if (rightHand is { } rightPose &&
            rightContact is { } rightHit &&
            OverlayGrabTransition.ShouldBegin(
                rightGrip,
                _lastRightGripPressed,
                rightContact,
                leftReleased))
        {
            BeginGrab(rightHit.OverlayId, ETrackedControllerRole.RightHand, rightPose);
        }

        SetContactHighlights(leftContact, rightContact, _grabs.Active);

        if (_grabs.HasMultiple)
        {
            _interactionContactHand = null;
            SetInteractionTarget(null);
            if (previousGrabCount < 2)
            {
                CancelSingleOverlayCommand("双手同时抓取空间对象");
                ResetOverlayPointer();
                Publish(L("Interaction.MultipleStarted"), SelectionState.Idle);
                _log.Info("[interaction] 进入双手抓取模式；工具栏、指针和滚动已禁用。");
            }
        }
        else if (_grabs.Single is { } activeGrab)
        {
            _interactionContactHand = activeGrab.Hand;
            SetInteractionTarget(activeGrab.OverlayId);
            if (previousGrabCount > 1)
            {
                Publish(L("Interaction.SingleRestored"), SelectionState.Idle);
                _log.Info("[interaction] 离开双手抓取模式；单窗口操作已恢复。");
            }
        }
        else
        {
            var previousContactHand = _interactionContactHand;
            var selectedContact = OverlayContactSelection.Select(
                leftContact,
                rightContact,
                _interactionTargetOverlayId,
                _interactionContactHand);
            _interactionContactHand = selectedContact?.Hand;
            SetInteractionTarget(selectedContact?.Contact.OverlayId);
            if (previousContactHand != _interactionContactHand)
            {
                _log.Info(
                    $"[interaction] 接触手变更：{previousContactHand?.ToString() ?? "None"} -> " +
                    $"{_interactionContactHand?.ToString() ?? "None"}，Overlay={selectedContact?.Contact.OverlayId.ToString() ?? "None"}。");
            }
        }

        UpdateOverlayPointer(
            origin,
            leftHand,
            rightHand,
            viewerPosition,
            leftPointerPressed,
            rightPointerPressed,
            cancellationToken);
        _lastLeftGripPressed = leftGrip;
        _lastRightGripPressed = rightGrip;
        _lastLeftPointerPressed = leftPointerPressed;
        _lastRightPointerPressed = rightPointerPressed;
    }

    private void UpdateOverlayPointer(
        ETrackingUniverseOrigin origin,
        TrackedHandPose? leftHand,
        TrackedHandPose? rightHand,
        Vector3f? viewerPosition,
        bool leftPressed,
        bool rightPressed,
        CancellationToken cancellationToken)
    {
        if (_grabs.HasMultiple)
        {
            ResetOverlayPointer();
            return;
        }

        var activeGrab = _grabs.Single;
        var targetId = _pointerCapture?.OverlayId ?? activeGrab?.OverlayId ?? _interactionTargetOverlayId;
        var contactHand = activeGrab?.Hand ?? _interactionContactHand;
        var pointerHand = _pointerCapture?.Hand ?? Opposite(contactHand);
        var target = targetId is { } id ? FindOverlay(id) : null;
        if (target is null || pointerHand is null || _spatialOverlayManager is null)
        {
            ResetOverlayPointer();
            return;
        }

        var pose = pointerHand == ETrackedControllerRole.LeftHand ? leftHand : rightHand;
        var pressed = pointerHand == ETrackedControllerRole.LeftHand ? leftPressed : rightPressed;
        var wasPressed = pointerHand == ETrackedControllerRole.LeftHand
            ? _lastLeftPointerPressed
            : _lastRightPointerPressed;
        SpatialOverlayPointerHit? hit = null;
        OverlayPointerRayVisual? pointerRay = null;
        if (pose is { } handPose)
        {
            var pointerNow = DateTimeOffset.Now;
            var rawDirection = SpatialOverlayInteraction.PointerDirection(handPose);
            var stabilizedRay = _pointerRayStabilizer.Update(
                target.Id,
                pointerHand.Value,
                handPose.Position,
                rawDirection,
                pointerNow,
                Volatile.Read(ref _pointerSmoothingStrength));
            if (_spatialOverlayManager.TryIntersect(
                    target.Id,
                    stabilizedRay.Source,
                    stabilizedRay.Direction,
                    origin,
                    target.Plane,
                    out var measuredHit))
            {
                var pointerInterval = target.Result?.Snapshot()?.ContentFormat == ResultContentFormat.Html
                    ? OverlayPointerStabilizer.HighResolutionUpdateInterval
                    : OverlayPointerStabilizer.DefaultUpdateInterval;
                var stabilized = _pointerStabilizer.Update(
                    measuredHit.OverlayId,
                    pointerHand.Value,
                    measuredHit.TexturePoint,
                    measuredHit.ContentPoint,
                    pressed,
                    pointerNow,
                    pointerInterval);
                hit = measuredHit with
                {
                    TexturePoint = stabilized.TexturePoint,
                    ContentPoint = stabilized.ContentPoint
                };
                if (viewerPosition is { } viewer)
                {
                    pointerRay = new OverlayPointerRayVisual(
                        stabilizedRay.Source,
                        measuredHit.WorldPoint + (target.Plane.Normal * PointerLayerOffset),
                        viewer);
                }
                var toolbarAction = IsToolbarVisible(target)
                    ? OverlayToolbarLayout.HitTest(
                        target.Kind,
                        target.Plane,
                        hit.Value.TexturePoint,
                        target.ToolbarSide)
                    : null;
                var forwardToWindow = _pointerCapture is { } activeCapture
                    ? activeCapture.ToolbarAction is null && !activeCapture.IsSwiping
                    : toolbarAction is null;
                if (forwardToWindow && stabilized.Changed)
                {
                    target.WindowSource?.PointerMove(hit.Value.ContentPoint);
                }
            }
        }

        if (pressed && !wasPressed && hit is { } downHit)
        {
            var toolbarAction = IsToolbarVisible(target)
                ? OverlayToolbarLayout.HitTest(
                    target.Kind,
                    target.Plane,
                    downHit.TexturePoint,
                    target.ToolbarSide)
                : null;
            var windowControlName = toolbarAction is null
                ? target.WindowSource?.PointerDown(downHit.ContentPoint)
                : null;
            _pointerCapture = new OverlayPointerCapture(
                target.Id,
                pointerHand.Value,
                downHit.TexturePoint,
                downHit.ContentPoint,
                downHit.WorldPoint,
                downHit.ContentPoint,
                false,
                toolbarAction,
                windowControlName);
            if (toolbarAction == OverlayToolbarAction.CustomCommand)
            {
                var wasCommandPressed = _commandPress.IsPressed;
                var pressedAt = DateTimeOffset.Now;
                HandleCommandButtonDown(
                    OverlayCommandButtonSource,
                    pressedAt,
                    cancellationToken);
                if (!wasCommandPressed &&
                    _commandPress.IsPressed &&
                    _commandTargetOverlayId == target.Id)
                {
                    _commandPress.ActivateLongHold(
                        pressedAt.AddMilliseconds(
                            _configuration.Speech.HoldThresholdMilliseconds));
                    _pendingSubmissionMode = AssistantRequestMode.CustomCommand;
                    _toolbarCommandPressOverlayId = target.Id;
                    Publish(L("Command.ToolbarListening"), SelectionState.Idle);
                }
            }
            _log.Info(
                $"[pointer] 光标按下并锁定对象：Overlay={target.Id}，手={pointerHand}，" +
                $"UV=({downHit.ContentPoint.X:F3},{downHit.ContentPoint.Y:F3})，" +
                $"工具栏={toolbarAction?.ToString() ?? "None"}，" +
                $"WPF控件={windowControlName ?? "None"}。");
        }

        if (_pointerCapture is { } capture && capture.OverlayId == target.Id)
        {
            if (hit is { } currentHit)
            {
                var previousContentPoint = capture.LastContentPoint;
                var startsSwipe = false;
                if (pressed && capture.ToolbarAction is null && !capture.IsSwiping)
                {
                    var viewport = target.WindowSource is { } windowSource
                        ? new Size(windowSource.SourcePixelWidth, windowSource.SourcePixelHeight)
                        : OverlayRenderer.CalculateLogicalCanvasSize(target.Plane);
                    if (OverlaySwipeGesture.ShouldStart(
                            capture.PressedContentPoint,
                            currentHit.ContentPoint,
                            viewport.Width,
                            viewport.Height))
                    {
                        startsSwipe = target.Result is not null
                            ? target.MaximumScroll > 0.01
                            : target.WindowSource?.BeginPointerSwipe(capture.PressedContentPoint) == true;
                    }
                }

                capture = capture with
                {
                    LastTexturePoint = currentHit.TexturePoint,
                    LastContentPoint = currentHit.ContentPoint,
                    LastWorldPoint = currentHit.WorldPoint,
                    IsSwiping = capture.IsSwiping || startsSwipe
                };
                _pointerCapture = capture;
                if (pressed && capture.IsSwiping)
                {
                    var swipeFrom = startsSwipe
                        ? capture.PressedContentPoint
                        : previousContentPoint;
                    var normalizedOffsetDelta = swipeFrom.Y - currentHit.ContentPoint.Y;
                    if (target.Result is { } result)
                    {
                        var viewportHeight = OverlayRenderer.CalculateLogicalCanvasSize(target.Plane).Height;
                        var offsetDelta = OverlaySwipeGesture.CalculateOffsetDelta(
                            swipeFrom,
                            currentHit.ContentPoint,
                            viewportHeight);
                        if (result.Scroll(offsetDelta, target.MaximumScroll))
                        {
                            target.IsDirty = true;
                        }
                    }
                    else
                    {
                        target.WindowSource?.PointerSwipe(normalizedOffsetDelta);
                    }
                }
            }

            SetPointerVisual(
                target.Id,
                new OverlayPointerVisual(
                    capture.Hand,
                    capture.LastTexturePoint,
                    capture.LastContentPoint,
                    pressed,
                    pointerRay,
                    capture.LastWorldPoint));
            if (!pressed && wasPressed)
            {
                var toolbarAction = capture.ToolbarAction;
                var commandButtonRelease =
                    toolbarAction == OverlayToolbarAction.CustomCommand &&
                    _toolbarCommandPressOverlayId == capture.OverlayId;
                var executeToolbarAction = commandButtonRelease ||
                                           (toolbarAction is { } pendingAction &&
                                            hit is { } releaseHit &&
                                             OverlayToolbarLayout.HitTest(
                                                 target.Kind,
                                                 target.Plane,
                                                 releaseHit.TexturePoint,
                                                 target.ToolbarSide) == pendingAction);
                bool? windowControlExecuted = null;
                if (toolbarAction is null)
                {
                    if (capture.IsSwiping)
                    {
                        target.WindowSource?.EndPointerSwipe();
                        windowControlExecuted = false;
                    }
                    else
                    {
                        windowControlExecuted = target.WindowSource?.PointerUp(capture.LastContentPoint);
                    }
                }
                _log.Info(
                    $"[pointer] 光标松开对象：Overlay={capture.OverlayId}，手={capture.Hand}，" +
                    $"UV=({capture.LastContentPoint.X:F3},{capture.LastContentPoint.Y:F3})，" +
                    $"工具栏={toolbarAction?.ToString() ?? "None"}，工具栏执行={executeToolbarAction}，" +
                    $"滑动={capture.IsSwiping}，" +
                    $"WPF控件={capture.WindowControlName ?? "None"}，" +
                    $"WPF执行={windowControlExecuted?.ToString() ?? "Native"}。");
                _pointerCapture = null;
                if (executeToolbarAction)
                {
                    if (commandButtonRelease)
                    {
                        _toolbarCommandPressOverlayId = null;
                        HandleCommandButtonUp(
                            OverlayCommandButtonSource,
                            DateTimeOffset.Now,
                            cancellationToken);
                    }
                    else
                    {
                        ExecuteToolbarAction(
                            target,
                            toolbarAction!.Value,
                            cancellationToken);
                    }
                }
            }
            return;
        }

        if (hit is null)
        {
            _pointerStabilizer.Reset();
        }

        SetPointerVisual(
            hit?.OverlayId,
            hit is { } hoverHit
                ? new OverlayPointerVisual(
                    pointerHand.Value,
                    hoverHit.TexturePoint,
                    hoverHit.ContentPoint,
                    false,
                    pointerRay,
                    hoverHit.WorldPoint)
                : null);
    }

    private void ResetOverlayPointer()
    {
        if (_pointerCapture is { } capture)
        {
            if (capture.ToolbarAction == OverlayToolbarAction.CustomCommand &&
                _toolbarCommandPressOverlayId == capture.OverlayId)
            {
                CancelToolbarCommandPress("指针交互中断");
            }
            else if (capture.ToolbarAction is null)
            {
                var windowSource = FindOverlay(capture.OverlayId)?.WindowSource;
                if (capture.IsSwiping)
                {
                    windowSource?.EndPointerSwipe();
                }
                else
                {
                    windowSource?.CancelPointer();
                }
            }
        }
        _pointerCapture = null;
        _pointerStabilizer.Reset();
        _pointerRayStabilizer.Reset();
        SetPointerVisual(null, null);
    }

    private void SetPointerVisual(long? overlayId, OverlayPointerVisual? pointer)
    {
        foreach (var overlay in _interactiveOverlays)
        {
            var next = overlay.Id == overlayId ? pointer : null;
            if (PointerVisualEquals(overlay.Pointer, next))
            {
                continue;
            }

            if (overlay.Pointer is not null && next is null)
            {
                overlay.WindowSource?.PointerLeave();
            }
            var previousToolbarState = ToolbarPointerState(overlay, overlay.Pointer);
            var nextToolbarState = ToolbarPointerState(overlay, next);
            overlay.Pointer = next;
            if (previousToolbarState != nextToolbarState)
            {
                overlay.IsChromeDirty = true;
            }
        }
    }

    private (OverlayToolbarAction? Action, bool Pressed) ToolbarPointerState(
        InteractiveOverlay overlay,
        OverlayPointerVisual? pointer)
    {
        if (!IsToolbarVisible(overlay) || pointer is not { } value)
        {
            return (null, false);
        }

        var action = OverlayToolbarLayout.HitTest(
            overlay.Kind,
            overlay.Plane,
            value.TexturePoint,
            overlay.ToolbarSide);
        return (action, action is not null && value.IsPressed);
    }

    private static bool PointerVisualEquals(
        OverlayPointerVisual? left,
        OverlayPointerVisual? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Value.Hand == right.Value.Hand &&
               left.Value.IsPressed == right.Value.IsPressed &&
               MathF.Abs(left.Value.TexturePoint.X - right.Value.TexturePoint.X) * OverlayRenderer.Width < 0.75f &&
               MathF.Abs(left.Value.TexturePoint.Y - right.Value.TexturePoint.Y) * OverlayRenderer.Height < 0.75f &&
               PointerWorldPointEquals(left.Value.WorldPoint, right.Value.WorldPoint) &&
               PointerRayEquals(left.Value.Ray, right.Value.Ray);
    }

    private static bool PointerWorldPointEquals(Vector3f? left, Vector3f? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return (left.Value - right.Value).LengthSquared < 0.000001f;
    }

    private static bool PointerRayEquals(
        OverlayPointerRayVisual? left,
        OverlayPointerRayVisual? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        const float toleranceSquared = 0.000001f;
        return (left.Value.Source - right.Value.Source).LengthSquared < toleranceSquared &&
               (left.Value.Target - right.Value.Target).LengthSquared < toleranceSquared &&
               (left.Value.Viewer - right.Value.Viewer).LengthSquared < toleranceSquared;
    }

    private static ETrackedControllerRole? Opposite(ETrackedControllerRole? hand) => hand switch
    {
        ETrackedControllerRole.LeftHand => ETrackedControllerRole.RightHand,
        ETrackedControllerRole.RightHand => ETrackedControllerRole.LeftHand,
        _ => null
    };

    private OverlayGrab? UpdateGrabForHand(
        ETrackedControllerRole hand,
        bool gripPressed,
        TrackedHandPose? pose,
        ETrackingUniverseOrigin origin)
    {
        if (!_grabs.TryGet(hand, out var grab))
        {
            return null;
        }

        var overlay = FindOverlay(grab.OverlayId);
        if (!gripPressed || pose is null || overlay is null)
        {
            _grabs.Release(hand, out _);
            _log.Info($"[interaction] 释放空间对象：Overlay={grab.OverlayId}，手={hand}。");
            if (overlay is not null)
            {
                overlay.IsChromeDirty = true;
                overlay.WindowSource?.SetHoldingHand(null);
            }
            if (_pointerCapture is { ToolbarAction: not null } toolbarCapture &&
                toolbarCapture.OverlayId == grab.OverlayId)
            {
                ResetOverlayPointer();
            }
            MarkGrabbedOverlaysDirty();
            return grab;
        }

        overlay.Plane = SpatialOverlayInteraction.Move(grab, pose.Value);
        _spatialOverlayManager?.UpdateTransform(overlay.Id, overlay.Plane, origin);
        return null;
    }

    private void BeginGrab(long overlayId, ETrackedControllerRole hand, TrackedHandPose pose)
    {
        var overlay = FindOverlay(overlayId);
        if (overlay is null || !overlay.CanGrab)
        {
            return;
        }

        if (!_grabs.TryBegin(new OverlayGrab(overlayId, hand, overlay.Plane, pose)))
        {
            return;
        }

        overlay.ToolbarSide = OverlayToolbarPlacement.OppositeHoldingHand(hand);
        overlay.WindowSource?.SetHoldingHand(hand);
        overlay.IsChromeDirty = true;
        MarkGrabbedOverlaysDirty();
        if (hand == ETrackedControllerRole.LeftHand)
        {
            PulseLeft(0.035f, 105f, 0.35f);
        }
        else
        {
            PulseRight(0.035f, 105f, 0.35f);
        }
        _log.Info($"[interaction] 抓取空间对象：Overlay={overlayId}，手={hand}。");
    }

    private bool IsToolbarVisible(InteractiveOverlay overlay) =>
        overlay.ShowToolbarWhenGrabbed &&
        _grabs.ContainsOverlay(overlay.Id);

    private void MarkGrabbedOverlaysDirty()
    {
        foreach (var grab in _grabs.Active)
        {
            if (FindOverlay(grab.OverlayId) is { } overlay)
            {
                overlay.IsChromeDirty = true;
            }
        }
    }

    private void ExecuteToolbarAction(
        InteractiveOverlay overlay,
        OverlayToolbarAction action,
        CancellationToken cancellationToken)
    {
        if (_grabs.HasMultiple)
        {
            Publish(L("Interaction.MultipleNoAction"), SelectionState.Idle, true);
            return;
        }

        if (action == OverlayToolbarAction.Translate)
        {
            StartDirectToolbarTranslation(
                overlay,
                AssistantRequestMode.Translate,
                cancellationToken);
            return;
        }
        if (action == OverlayToolbarAction.LayoutTranslate)
        {
            StartDirectToolbarTranslation(
                overlay,
                AssistantRequestMode.LayoutTranslate,
                cancellationToken);
            return;
        }
        if (action == OverlayToolbarAction.CustomCommand)
        {
            return;
        }

        if (action == OverlayToolbarAction.Close)
        {
            RemoveInteractiveOverlay(overlay.Id, "工具栏关闭");
            return;
        }

        if (overlay.Kind != InteractiveOverlayKind.Result ||
            overlay.Result?.Snapshot() is not { } snapshot)
        {
            Publish(L("Osc.TextOnly"), SelectionState.Idle, true);
            return;
        }

        if (snapshot.Status == ResultStatus.Streaming)
        {
            Publish(L("Osc.StillGenerating"), SelectionState.Idle, true);
            return;
        }

        if (snapshot.Status == ResultStatus.Error || string.IsNullOrWhiteSpace(snapshot.VisibleText))
        {
            Publish(L("Osc.NoText"), SelectionState.Idle, true);
            return;
        }

        if (_toolbarOscTask is { IsCompleted: false })
        {
            Publish(L("Osc.Busy"), SelectionState.Idle, true);
            return;
        }

        _toolbarOscTask = SendResultOverlayToOscAsync(
            overlay.Id,
            snapshot.VisibleText,
            _cancellation?.Token ?? CancellationToken.None);
    }

    private void StartDirectToolbarTranslation(
        InteractiveOverlay overlay,
        AssistantRequestMode mode,
        CancellationToken cancellationToken)
    {
        if (overlay is not { Kind: InteractiveOverlayKind.Capture, Capture: not null })
        {
            Publish(L("Translation.CaptureOnly"), SelectionState.Idle, true);
            return;
        }
        if (_commandPress.IsPressed)
        {
            Publish(L("Command.RecordingInProgress"), SelectionState.Idle, true);
            return;
        }

        _commandTargetOverlayId = overlay.Id;
        _pendingSubmissionMode = mode;
        _commandRecordingError = null;
        _log.Info(
            $"[toolbar] 单击启动翻译：Overlay={overlay.Id}，模式={mode}。");
        StartTranslationForCommandTarget(cancellationToken);
    }

    private void CancelToolbarCommandPress(string reason)
    {
        if (_toolbarCommandPressOverlayId is null)
        {
            return;
        }

        CancelSingleOverlayCommand(reason);
    }

    private void CancelSingleOverlayCommand(string reason)
    {
        var hadPendingCommand = _commandPress.IsPressed ||
                                _commandTargetOverlayId is not null ||
                                _toolbarCommandPressOverlayId is not null;
        var targetOverlayId = _commandTargetOverlayId ?? _toolbarCommandPressOverlayId;

        _toolbarCommandPressOverlayId = null;
        _commandTargetOverlayId = null;
        SetCommandRecording(targetOverlayId, false);
        _commandPress.Reset();
        _pendingSubmissionMode = AssistantRequestMode.Translate;
        _commandRecordingError = null;
        if (hadPendingCommand && _commandRecorder.IsRecording)
        {
            _ = CancelToolbarCommandRecordingAsync(reason);
        }
        if (hadPendingCommand)
        {
            _log.Info($"[interaction] 已取消单窗口命令：原因={reason}。");
        }
    }

    private void SetCommandRecording(long? overlayId, bool isRecording)
    {
        if (overlayId is null || FindOverlay(overlayId.Value) is not { } overlay ||
            overlay.IsCommandRecording == isRecording)
        {
            return;
        }

        overlay.IsCommandRecording = isRecording;
        overlay.IsChromeDirty = true;
        overlay.SynchronizeChromeTextureBuffers = true;
    }

    private async Task CancelToolbarCommandRecordingAsync(string reason)
    {
        try
        {
            await _commandRecorder.CancelAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _log.Error($"取消截图工具栏语音命令录音失败：{reason}。", exception);
        }
    }

    private async Task SendResultOverlayToOscAsync(
        long overlayId,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            var chunkCount = TextChunker.Split(
                text,
                _configuration.VrChatVoiceInput.MaxChatboxCharacters).Count;
            _log.Info(
                $"[toolbar] 开始发送文本结果：Overlay={overlayId}，" +
                $"目标={_configuration.VrChatVoiceInput.Host}:{_configuration.VrChatVoiceInput.Port}，" +
                $"字符={text.Length}，分段={chunkCount}，" +
                $"分段间隔={_configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds} ms，" +
                $"文本={PreviewText(text)}");
            Publish(
                chunkCount > 1
                    ? LF("Osc.SendingChunks", chunkCount)
                    : L("Osc.Sending"),
                SelectionState.Idle);
            _vrChatOscOutput ??= new VrChatOscOutput(_configuration.VrChatVoiceInput);
            await _vrChatOscOutput.SendAsync(text, cancellationToken);
            _log.Info(
                $"[toolbar] 文本结果 OSC 发送完成：Overlay={overlayId}，" +
                $"字符={text.Length}，分段={chunkCount}。");
            Publish(L("Osc.Sent"), SelectionState.Idle);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Info($"[toolbar] 文本结果 OSC 发送已取消：Overlay={overlayId}。");
        }
        catch (Exception exception)
        {
            _log.Error($"[toolbar] 文本结果 OSC 发送失败：Overlay={overlayId}。", exception);
            Publish(LF("Osc.Failed", exception.Message), SelectionState.Idle, true);
        }
    }

    private OverlayContact? FindContact(Vector3f position)
    {
        OverlayContact? closest = null;
        foreach (var overlay in _interactiveOverlays)
        {
            if (overlay.IsPresentationDeferred)
            {
                continue;
            }

            if (!SpatialOverlayInteraction.TryContact(overlay.Plane, position, out var distance))
            {
                continue;
            }

            if (closest is null || distance < closest.Value.Distance)
            {
                closest = new OverlayContact(overlay.Id, distance);
            }
        }

        return closest;
    }

    private void UpdateResultControls()
    {
        var clickPressed = ReadDigital(_overlayToggleActionHandle);
        if (_interactiveOverlays.Count == 0)
        {
            if (_overlayCloseHold.IsPressed)
            {
                CancelOverlayCloseHold("没有可关闭的空间对象", suppressRelease: true);
            }
            if (!clickPressed)
            {
                _suppressResultClickRelease = false;
            }
            _lastResultClickPressed = clickPressed;
            _lastScrollTargetOverlayId = null;
            _resultScrollBoundary = ResultScrollBoundary.None;
            _wpfWheelAccumulator = 0;
            return;
        }

        var now = DateTimeOffset.Now;
        var elapsed = _lastResultScrollAt == default
            ? TimeSpan.Zero
            : now - _lastResultScrollAt;
        _lastResultScrollAt = now;
        var canControl = !_interactiveOverlaysSuppressed && _selection.Snapshot.State == SelectionState.Idle;
        if (!canControl && _overlayCloseHold.IsPressed)
        {
            CancelOverlayCloseHold("空间对象当前不可交互", suppressRelease: true);
        }

        if (canControl && clickPressed && !_lastResultClickPressed)
        {
            var closeTarget = _spatialOverlaysVisible ? ResolveInteractionTarget() : null;
            _overlayCloseHold.Press(closeTarget?.Id, now);
            _suppressResultClickRelease = false;
            if (closeTarget is not null)
            {
                SetOverlayCloseHoldProgress(closeTarget.Id, 0);
                _log.Info(
                    $"[interaction] 开始长按关闭空间对象：Overlay={closeTarget.Id}，阈值=" +
                    $"{SingleOverlayCloseHoldDuration.TotalMilliseconds:F0}ms。");
            }
        }

        if (canControl && clickPressed && _overlayCloseHold.IsPressed &&
            _overlayCloseHold.OverlayId is { } closeTargetId)
        {
            var currentTarget = _spatialOverlaysVisible ? ResolveInteractionTarget() : null;
            if (currentTarget?.Id != closeTargetId)
            {
                CancelOverlayCloseHold("目标窗口已松开", suppressRelease: true);
            }
            else
            {
                var update = _overlayCloseHold.Update(now);
                SetOverlayCloseHoldProgress(closeTargetId, update.Progress);
                if (update.CompletedNow)
                {
                    PulseRight(0.08f, 55f, 0.65f);
                    RemoveInteractiveOverlay(closeTargetId, "右摇杆长按关闭");
                }
            }
        }

        if (!clickPressed && _lastResultClickPressed)
        {
            if (_suppressResultClickRelease)
            {
                _suppressResultClickRelease = false;
                _overlayCloseHold.Reset();
            }
            else
            {
                var release = _overlayCloseHold.Release();
                SetOverlayCloseHoldProgress(release.OverlayId, null);
                if (canControl && release.OverlayId is null && !release.Completed)
                {
                    ToggleSpatialOverlayVisibility();
                }
                else if (release.OverlayId is not null && !release.Completed)
                {
                    _log.Info(
                        $"[interaction] 长按关闭已取消：Overlay={release.OverlayId}，原因=右摇杆提前松开。");
                }
            }
        }

        _lastResultClickPressed = clickPressed;
        var scrollTarget = ResolveInteractionTarget();
        if (!canControl || !_spatialOverlaysVisible || scrollTarget is null ||
            (scrollTarget.Result is null && scrollTarget.WindowSource is null))
        {
            _lastScrollTargetOverlayId = null;
            _resultScrollBoundary = ResultScrollBoundary.None;
            _wpfWheelAccumulator = 0;
            return;
        }

        var scroll = ReadAnalog(_resultScrollActionHandle);
        if (!scroll.bActive || ResultScrollMath.IsNeutral(scroll.y))
        {
            _resultScrollBoundary = ResultScrollBoundary.None;
            _wpfWheelAccumulator = 0;
            return;
        }

        if (_lastScrollTargetOverlayId != scrollTarget.Id)
        {
            _lastScrollTargetOverlayId = scrollTarget.Id;
            _resultScrollBoundary = ResultScrollBoundary.None;
            _wpfWheelAccumulator = 0;
        }

        var delta = ResultScrollMath.CalculateDelta(
            scroll.y,
            Volatile.Read(ref _invertResultScroll),
            elapsed);
        if (scrollTarget.WindowSource is { } windowSource)
        {
            _wpfWheelAccumulator += -delta * 4;
            var wheelSteps = Math.Clamp((int)(_wpfWheelAccumulator / 120d), -4, 4);
            if (wheelSteps != 0)
            {
                var wheelDelta = wheelSteps * 120;
                _wpfWheelAccumulator -= wheelDelta;
                windowSource.PointerWheel(
                    scrollTarget.Pointer?.ContentPoint ?? new NormalizedPoint(0.5f, 0.5f),
                    wheelDelta);
                scrollTarget.IsDirty = true;
            }
            return;
        }

        var resultState = scrollTarget.Result!;

        if (_resultScrollBoundary != ResultScrollBoundary.None)
        {
            return;
        }

        var previousOffset = resultState.Snapshot()?.ScrollOffset ?? 0;
        if (resultState.Scroll(delta, scrollTarget.MaximumScroll))
        {
            scrollTarget.IsDirty = true;
        }

        var currentOffset = resultState.Snapshot()?.ScrollOffset ?? 0;
        if (delta < 0 && currentOffset <= 0)
        {
            _resultScrollBoundary = ResultScrollBoundary.Top;
            if (previousOffset > 0)
            {
                _log.Info("[interaction] 结果滚动已归位到顶部；摇杆回中后可反向滚动。");
            }
        }
        else if (delta > 0 && currentOffset >= scrollTarget.MaximumScroll)
        {
            _resultScrollBoundary = ResultScrollBoundary.Bottom;
        }
    }

    private void ToggleSpatialOverlayVisibility()
    {
        _spatialOverlaysVisible = !_spatialOverlaysVisible;
        if (_spatialOverlaysVisible)
        {
            RenderInteractiveOverlays();
        }
        else
        {
            SetContactHighlights(null, null);
            SetInteractionTarget(null);
            _interactionContactHand = null;
            _grabs.Clear();
            ResetOverlayPointer();
            HideInteractiveOverlays();
        }

        PulseRight(0.035f, 100f, 0.3f);
        _log.Info($"[interaction] 右摇杆切换全局空间对象：显示={_spatialOverlaysVisible}。");
        Publish(
            _spatialOverlaysVisible ? L("Interaction.Visible") : L("Interaction.Hidden"),
            SelectionState.Idle);
    }

    private void CancelOverlayCloseHold(string reason, bool suppressRelease)
    {
        var overlayId = _overlayCloseHold.OverlayId;
        var wasPressed = _overlayCloseHold.IsPressed;
        SetOverlayCloseHoldProgress(overlayId, null);
        _overlayCloseHold.Reset();
        if (wasPressed && suppressRelease)
        {
            _suppressResultClickRelease = true;
        }
        if (overlayId is not null)
        {
            _log.Info($"[interaction] 长按关闭已取消：Overlay={overlayId}，原因={reason}。");
        }
    }

    private void SetOverlayCloseHoldProgress(long? overlayId, double? progress)
    {
        if (overlayId is null || FindOverlay(overlayId.Value) is not { } overlay)
        {
            return;
        }

        double? normalized = progress is null ? null : Math.Clamp(progress.Value, 0, 1);
        if (overlay.CloseHoldProgress is null && normalized is null)
        {
            return;
        }
        if (overlay.CloseHoldProgress is { } previous && normalized is { } next &&
            Math.Abs(previous - next) < 0.001)
        {
            return;
        }

        overlay.CloseHoldProgress = normalized;
        overlay.IsProgressDirty = true;
    }

    private void UpdateSelectionManagerHint(
        SpatialSelectionPlane? plane,
        bool bothTriggersPressed,
        SelectionState state)
    {
        if (!bothTriggersPressed || plane is not { } value || state != SelectionState.Sizing)
        {
            return;
        }

        var usable = value.IsUsable;
        if (usable == _lastSelectionPlaneUsable)
        {
            return;
        }

        _lastSelectionPlaneUsable = usable;
        Publish(
            usable
                ? L("Overlay.Selection.Restored")
                : !value.HasUsableDimensions
                    ? LF(
                        "Overlay.Selection.TooNarrowStatus",
                        value.Width * 100,
                        value.Height * 100,
                        SpatialSelectionPlane.MinimumWidthMeters * 100)
                    : LF(
                        "Overlay.Selection.AngleStatus",
                        value.ViewAngleDegrees,
                        SpatialSelectionPlane.MaximumViewAngleDegrees),
            state,
            !usable);
    }

    private void RenderSelectionIfNeeded(bool force)
    {
        var snapshot = _selection.Snapshot;
        var now = DateTimeOffset.Now;
        if (!force && snapshot == _lastRenderedSnapshot)
        {
            return;
        }

        if (!force && now - _lastRenderAt < TimeSpan.FromMilliseconds(80))
        {
            return;
        }

        var invalidHint = SelectionInvalidHint(_currentPlane, snapshot.State);
        var frameUsable = invalidHint is null;
        if (SetSelectionOverlayImage(
                _renderer.RenderSelection(snapshot, frameUsable, invalidHint)))
        {
            _lastRenderedSnapshot = snapshot;
            _lastRenderAt = now;
        }
    }

    private static string? SelectionInvalidHint(
        SpatialSelectionPlane? plane,
        SelectionState state)
    {
        if (state != SelectionState.Sizing)
        {
            return null;
        }
        if (plane is not { } value)
        {
            return L("Overlay.Selection.TrackingUnavailable");
        }
        if (!value.HasUsableDimensions)
        {
            return LF(
                "Overlay.Selection.TooNarrow",
                SpatialSelectionPlane.MinimumWidthMeters * 100);
        }
        return value.HasUsableOrientation
            ? null
            : L("Overlay.Selection.Angle");
    }

    private bool SetSelectionOverlayImage(byte[] image)
    {
        if (_selectionOverlayTexture is null)
        {
            throw new InvalidOperationException("D3D11 Overlay 纹理尚未初始化。");
        }

        _selectionOverlayTexture.Update(image);
        return true;
    }

    private void ResetSelectionOverlayImage(byte[] image)
    {
        if (_selectionOverlayTexture is null)
        {
            throw new InvalidOperationException("D3D11 Overlay 纹理尚未初始化。");
        }

        _selectionOverlayTexture.Reset(image);
    }

    private void ResetRejectedSelection(SelectionSnapshot snapshot)
    {
        _currentPlane = null;
        _lockedPlane = null;
        _lastSelectionPlaneUsable = true;
        SetHeadLockedSelectionOverlayTransform();
        ResetSelectionOverlayImage(_renderer.RenderSelection(snapshot));
        _lastRenderedSnapshot = snapshot;
        _lastRenderAt = DateTimeOffset.Now;
        ShowSelectionOverlay();
        _log.Info($"{DiagnosticTag()} [selection] 无效框已清除，恢复待框选提示。");
    }

    private void RenderInteractiveOverlays(bool force = false)
    {
        if (_interactiveOverlaysSuppressed || !_spatialOverlaysVisible)
        {
            _spatialOverlayManager?.HideAll();
            return;
        }
        if (_spatialOverlayManager is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var origin = CurrentTrackingOrigin();
        foreach (var overlay in _interactiveOverlays)
        {
            if (overlay.IsPresentationDeferred)
            {
                _spatialOverlayManager.Hide(overlay.Id);
                overlay.IsShown = false;
                continue;
            }

            _spatialOverlayManager.UpdateTransform(overlay.Id, overlay.Plane, origin);
            UpdateDirectVideoTransform(overlay, origin);
            RenderInteractiveOverlay(overlay, force, now);
            RenderInteractiveChrome(overlay, origin, force);
            RenderInteractiveProgress(overlay, origin, force);
            UpdateInteractivePointer(overlay, origin);
            _spatialOverlayManager.Show(overlay.Id);
            overlay.IsShown = true;
        }

        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(origin, 0f, poses);
        var hmdPose = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (hmdPose.bPoseIsValid)
        {
            _spatialOverlayManager.UpdateDepthOrder(
                Position(hmdPose.mDeviceToAbsoluteTracking),
                _interactiveOverlays);
        }
    }

    private void RenderInteractiveOverlay(
        InteractiveOverlay overlay,
        bool force,
        DateTimeOffset? renderAt = null)
    {
        var now = renderAt ?? DateTimeOffset.Now;
        var minimumInterval = overlay.WindowSource?.FrameInterval ??
                              TimeSpan.FromSeconds(1d / RuntimeFramesPerSecond);
        var windowNeedsRender = overlay.WindowSource?.NeedsRender == true;
        var directPixelsNeedRender = overlay.WindowSource?.NeedsDirectPixelRender == true;
        if (_spatialOverlayManager is null)
        {
            return;
        }
        if (!force && !overlay.SynchronizeTextureBuffers)
        {
            if (!overlay.IsDirty && !windowNeedsRender && !directPixelsNeedRender)
            {
                return;
            }
            if (now - overlay.LastRenderAt + WpfFrameIntervalTolerance < minimumInterval)
            {
                return;
            }
        }

        if (overlay.Kind == InteractiveOverlayKind.Capture && overlay.Capture is { } capture)
        {
            var frame = _renderer.RenderCapture(
                capture.ImageBytes,
                overlay.Plane,
                overlay.IsHighlighted);
            _spatialOverlayManager.UpdatePixels(
                overlay.Id,
                frame.Pixels,
                frame.PixelWidth,
                frame.PixelHeight,
                overlay.SynchronizeTextureBuffers);
            overlay.RenderCount++;
            if (force || overlay.RenderCount == 1)
            {
                _log.Info(
                    $"{DiagnosticTag()} [overlay] 截图层已渲染：Overlay={overlay.Id}，" +
                    $"纹理={frame.PixelWidth}x{frame.PixelHeight}，次数={overlay.RenderCount}，" +
                    $"强制={force}");
            }
        }
        else if (overlay.Result?.Snapshot() is { } snapshot)
        {
            var frameStartedAt = Stopwatch.GetTimestamp();
            var frame = _renderer.RenderResults(
                snapshot,
                overlay.Plane,
                overlay.IsHighlighted,
                cacheOwner: overlay);
            var rasterizeDuration = Stopwatch.GetElapsedTime(frameStartedAt);
            var uploadStartedAt = Stopwatch.GetTimestamp();
            _spatialOverlayManager.UpdatePixels(
                overlay.Id,
                frame.Pixels,
                frame.PixelWidth,
                frame.PixelHeight,
                overlay.SynchronizeTextureBuffers);
            var uploadDuration = Stopwatch.GetElapsedTime(uploadStartedAt);
            overlay.MaximumScroll = frame.MaximumScrollOffset;
            overlay.Result.ClampScroll(overlay.MaximumScroll);
            overlay.RenderCount++;
            ResultOverlayFrameRendered?.Invoke(
                this,
                new ResultOverlayFrameRenderedEventArgs(
                    overlay.Id,
                    snapshot.ContentFormat,
                    snapshot.Chat is not null,
                    frame.PixelWidth,
                    frame.PixelHeight,
                    rasterizeDuration,
                    uploadDuration,
                    Stopwatch.GetElapsedTime(frameStartedAt),
                    now));
            if (force || overlay.RenderCount == 1 || overlay.RenderCount % 10 == 0)
            {
                _log.Info(
                    $"{DiagnosticTag()} [overlay] 结果层已渲染：Overlay={overlay.Id}，" +
                    $"次数={overlay.RenderCount}，请求={snapshot.RequestId}，" +
                    $"状态={snapshot.Status}，字符={snapshot.Text.Length}，" +
                    $"纹理={frame.PixelWidth}x{frame.PixelHeight}，" +
                    $"滚动={snapshot.ScrollOffset:F0}，强制={force}");
            }
        }
        else if (overlay.WindowSource is { } windowSource)
        {
            var rendered = false;
            if (windowSource.HasDirectPixelSource &&
                (force || directPixelsNeedRender) &&
                windowSource.TryGetLatestDirectPixelFrame(out var directFrame))
            {
                var uploadStartedAt = Stopwatch.GetTimestamp();
                if (directFrame.Format == DirectOverlayPixelFormat.Bgra)
                {
                    _spatialOverlayManager.UpdateLayerBgraPixels(
                        overlay.Id,
                        SpatialOverlayLayer.Video,
                        directFrame.Pixels,
                        directFrame.Width,
                        directFrame.Height,
                        overlay.SynchronizeVideoTextureBuffers);
                }
                else
                {
                    _spatialOverlayManager.UpdateLayerPixels(
                        overlay.Id,
                        SpatialOverlayLayer.Video,
                        directFrame.Pixels,
                        directFrame.Width,
                        directFrame.Height,
                        overlay.SynchronizeVideoTextureBuffers);
                }
                var uploadDuration = Stopwatch.GetElapsedTime(uploadStartedAt);
                windowSource.MarkDirectPixelFrameRendered(directFrame.Sequence);
                overlay.SynchronizeVideoTextureBuffers = false;
                overlay.RenderCount++;
                rendered = true;
                WpfOverlayFrameRendered?.Invoke(
                    this,
                    new WpfOverlayFrameRenderedEventArgs(
                        overlay.Id,
                        windowSource.Options.Name,
                        directFrame.Width,
                        directFrame.Height,
                        TimeSpan.Zero,
                        uploadDuration,
                        uploadDuration,
                        now,
                        isDirectPixels: true));
                if (force || overlay.RenderCount == 1 || overlay.RenderCount % 300 == 0)
                {
                    _log.Info(
                        $"[overlay] 直传视频层已更新：Overlay={overlay.Id}，" +
                        $"名称={windowSource.Options.Name}，纹理={directFrame.Width}x{directFrame.Height}，" +
                        $"次数={overlay.RenderCount}。");
                }
            }

            if (!windowSource.HasDirectPixelSource ||
                force ||
                overlay.IsDirty ||
                windowNeedsRender ||
                overlay.SynchronizeTextureBuffers)
            {
                var frameStartedAt = Stopwatch.GetTimestamp();
                var frame = _renderer.RenderWindow(
                    windowSource,
                    overlay.Plane);
                var rasterizeDuration = Stopwatch.GetElapsedTime(frameStartedAt);
                var uploadStartedAt = Stopwatch.GetTimestamp();
                var synchronizeWindowBuffers = overlay.SynchronizeTextureBuffers |
                                               windowSource.ConsumeBufferSynchronizationRequest();
                _spatialOverlayManager.UpdatePixels(
                    overlay.Id,
                    frame.Pixels,
                    frame.PixelWidth,
                    frame.PixelHeight,
                    synchronizeWindowBuffers);
                var uploadDuration = Stopwatch.GetElapsedTime(uploadStartedAt);
                overlay.RenderCount++;
                rendered = true;
                WpfOverlayFrameRendered?.Invoke(
                    this,
                    new WpfOverlayFrameRenderedEventArgs(
                        overlay.Id,
                        windowSource.Options.Name,
                        frame.PixelWidth,
                        frame.PixelHeight,
                        rasterizeDuration,
                        uploadDuration,
                        Stopwatch.GetElapsedTime(frameStartedAt),
                        now));
                CompletePendingDiagnosticWpfInteractions(overlay.Id, Stopwatch.GetTimestamp());
                if (force || overlay.RenderCount == 1 || overlay.RenderCount % 300 == 0)
                {
                    _log.Info(
                        $"[overlay] WPF 窗口层已渲染：Overlay={overlay.Id}，" +
                        $"名称={windowSource.Options.Name}，纹理={frame.PixelWidth}x{frame.PixelHeight}，" +
                        $"次数={overlay.RenderCount}。");
                }
            }

            if (!rendered)
            {
                return;
            }
        }
        else
        {
            return;
        }

        overlay.IsDirty = false;
        overlay.SynchronizeTextureBuffers = false;
        overlay.LastRenderAt = now;
    }

    private void UpdateDirectVideoTransform(
        InteractiveOverlay overlay,
        ETrackingUniverseOrigin origin)
    {
        if (_spatialOverlayManager is null ||
            overlay.WindowSource is not { HasDirectPixelSource: true } windowSource)
        {
            return;
        }

        var videoPlane = SpatialQuadOverlayMath.CreateChildPlane(
            overlay.Plane,
            windowSource.DirectPixelRegion,
            VideoLayerOffset);
        _spatialOverlayManager.UpdateLayerTransform(
            overlay.Id,
            SpatialOverlayLayer.Video,
            videoPlane,
            origin);
        _spatialOverlayManager.ShowLayer(overlay.Id, SpatialOverlayLayer.Video);
    }

    private void RenderInteractiveChrome(
        InteractiveOverlay overlay,
        ETrackingUniverseOrigin origin,
        bool force)
    {
        if (_spatialOverlayManager is null)
        {
            return;
        }

        var showToolbar = IsToolbarVisible(overlay);
        var shouldShow = showToolbar || overlay.IsCommandRecording;
        if (!shouldShow)
        {
            _spatialOverlayManager.HideLayer(overlay.Id, SpatialOverlayLayer.Chrome);
            overlay.IsChromeDirty = false;
            return;
        }

        if (force || overlay.IsChromeDirty || overlay.SynchronizeChromeTextureBuffers)
        {
            var frame = _renderer.RenderChrome(
                overlay.Kind,
                overlay.Plane,
                showToolbar,
                overlay.Pointer,
                overlay.IsCommandRecording,
                overlay.ToolbarSide);
            _spatialOverlayManager.UpdateLayerPixels(
                overlay.Id,
                SpatialOverlayLayer.Chrome,
                frame.Pixels,
                frame.PixelWidth,
                frame.PixelHeight,
                overlay.SynchronizeChromeTextureBuffers);
            overlay.IsChromeDirty = false;
            overlay.SynchronizeChromeTextureBuffers = false;
        }

        _spatialOverlayManager.UpdateLayerTransform(
            overlay.Id,
            SpatialOverlayLayer.Chrome,
            SpatialQuadOverlayMath.OffsetPlane(overlay.Plane, ChromeLayerOffset),
            origin);
        _spatialOverlayManager.ShowLayer(overlay.Id, SpatialOverlayLayer.Chrome);
    }

    private void RenderInteractiveProgress(
        InteractiveOverlay overlay,
        ETrackingUniverseOrigin origin,
        bool force)
    {
        if (_spatialOverlayManager is null)
        {
            return;
        }

        if (overlay.CloseHoldProgress is not { } progress)
        {
            _spatialOverlayManager.HideLayer(overlay.Id, SpatialOverlayLayer.Progress);
            overlay.IsProgressDirty = false;
            return;
        }

        if (force || overlay.IsProgressDirty || overlay.SynchronizeProgressTextureBuffers)
        {
            var frame = _renderer.RenderCloseHoldProgress(overlay.Plane, progress);
            _spatialOverlayManager.UpdateLayerPixels(
                overlay.Id,
                SpatialOverlayLayer.Progress,
                frame.Pixels,
                frame.PixelWidth,
                frame.PixelHeight,
                overlay.SynchronizeProgressTextureBuffers);
            overlay.IsProgressDirty = false;
            overlay.SynchronizeProgressTextureBuffers = false;
        }

        var progressPlane = SpatialQuadOverlayMath.CreateSquareChildPlane(
            overlay.Plane,
            new NormalizedPoint(0.5f, 0.5f),
            OverlayRenderer.CalculateCloseHoldProgressLogicalExtent(overlay.Plane),
            ProgressLayerOffset);
        _spatialOverlayManager.UpdateLayerTransform(
            overlay.Id,
            SpatialOverlayLayer.Progress,
            progressPlane,
            origin);
        _spatialOverlayManager.ShowLayer(overlay.Id, SpatialOverlayLayer.Progress);
    }

    private void UpdateInteractivePointer(
        InteractiveOverlay overlay,
        ETrackingUniverseOrigin origin)
    {
        if (_spatialOverlayManager is null)
        {
            return;
        }

        if (overlay.Pointer is not { } pointer)
        {
            _spatialOverlayManager.HideLayer(overlay.Id, SpatialOverlayLayer.Ray);
            _spatialOverlayManager.HideLayer(overlay.Id, SpatialOverlayLayer.Pointer);
            return;
        }

        if (_configuration.ShowPointerRay &&
            pointer.Ray is { } ray &&
            SpatialQuadOverlayMath.TryCreateRayPlane(
                ray.Source,
                ray.Target,
                ray.Viewer,
                OverlayRenderer.PointerRayThicknessMeters,
                out var rayPlane))
        {
            if (overlay.SynchronizeRayTextureBuffers)
            {
                var rayFrame = _renderer.RenderPointerRay();
                _spatialOverlayManager.UpdateLayerPixels(
                    overlay.Id,
                    SpatialOverlayLayer.Ray,
                    rayFrame.Pixels,
                    rayFrame.PixelWidth,
                    rayFrame.PixelHeight,
                    synchronizeBuffers: true);
                overlay.SynchronizeRayTextureBuffers = false;
            }
            _spatialOverlayManager.UpdateLayerTransform(
                overlay.Id,
                SpatialOverlayLayer.Ray,
                rayPlane,
                origin);
            _spatialOverlayManager.ShowLayer(overlay.Id, SpatialOverlayLayer.Ray);
        }
        else
        {
            _spatialOverlayManager.HideLayer(overlay.Id, SpatialOverlayLayer.Ray);
        }

        var pointerStateChanged = overlay.RenderedPointerPressed != pointer.IsPressed;
        if (overlay.SynchronizePointerTextureBuffers || pointerStateChanged)
        {
            var frame = _renderer.RenderPointer(pointer.IsPressed);
            _spatialOverlayManager.UpdateLayerPixels(
                overlay.Id,
                SpatialOverlayLayer.Pointer,
                frame.Pixels,
                frame.PixelWidth,
                frame.PixelHeight,
                overlay.SynchronizePointerTextureBuffers || pointerStateChanged);
            overlay.SynchronizePointerTextureBuffers = false;
            overlay.RenderedPointerPressed = pointer.IsPressed;
        }

        var pointerPlane = SpatialQuadOverlayMath.CreatePointerPlane(
            overlay.Plane,
            pointer,
            OverlayRenderer.PointerLogicalExtent,
            PointerLayerOffset);
        _spatialOverlayManager.UpdateLayerTransform(
            overlay.Id,
            SpatialOverlayLayer.Pointer,
            pointerPlane,
            origin);
        _spatialOverlayManager.ShowLayer(overlay.Id, SpatialOverlayLayer.Pointer);
    }

    private InteractiveOverlay AddInteractiveOverlay(
        InteractiveOverlayKind kind,
        SpatialSelectionPlane plane,
        CapturedFrame? capture,
        ResultOverlayState? resultState,
        WpfWindowOverlaySource? windowSource = null,
        bool canGrab = true,
        AssistantConversation? conversation = null,
        bool showToolbarWhenGrabbed = true)
    {
        var overlay = new InteractiveOverlay
        {
            Id = Interlocked.Increment(ref _nextOverlayId),
            Kind = kind,
            Plane = plane,
            Capture = capture,
            Result = resultState,
            Conversation = conversation,
            WindowSource = windowSource,
            CanGrab = canGrab,
            ShowToolbarWhenGrabbed = showToolbarWhenGrabbed
        };
        _interactiveOverlays.Add(overlay);
        overlay.IsDirty = true;
        return overlay;
    }

    private void RemoveInteractiveOverlay(long overlayId, string reason, bool publishStatus = true)
    {
        var overlay = FindOverlay(overlayId);
        if (overlay is null)
        {
            return;
        }

        foreach (var submission in _submissions.Values
                     .Where(item => item.OverlayId == overlayId)
                     .ToArray())
        {
            _log.Info(
                $"{DiagnosticTag(submission.OperationId, submission.RequestId)} [submit] " +
                $"结果窗口关闭，正在取消请求：原因={reason}。");
            submission.Cancellation.Cancel();
        }

        if (_overlayCloseHold.OverlayId == overlayId)
        {
            _overlayCloseHold.Reset();
            _suppressResultClickRelease = true;
        }

        _spatialOverlayManager?.Remove(overlayId);
        _interactiveOverlays.Remove(overlay);
        _pendingDiagnosticWpfInteractions.Remove(overlayId);
        _contactOverlayIds.Remove(overlayId);
        overlay.WindowSource?.Dispose();
        if (_controlPanelOverlayId == overlayId)
        {
            _controlPanelOverlayId = null;
            _controlPanelWindow = null;
        }
        if (_grabs.RemoveOverlay(overlayId))
        {
            MarkGrabbedOverlaysDirty();
        }
        if (_interactionTargetOverlayId == overlayId)
        {
            _interactionTargetOverlayId = null;
        }
        if (_commandTargetOverlayId == overlayId)
        {
            _commandTargetOverlayId = null;
        }
        if (_pointerCapture?.OverlayId == overlayId)
        {
            ResetOverlayPointer();
        }
        _log.Info($"[interaction] 空间对象已关闭：Overlay={overlayId}，原因={reason}，剩余={_interactiveOverlays.Count}。");
        if (publishStatus)
        {
            Publish(LF("Interaction.ClosedCount", _interactiveOverlays.Count), SelectionState.Idle);
        }
    }

    private InteractiveOverlay? FindOverlay(long overlayId) =>
        _interactiveOverlays.FirstOrDefault(item => item.Id == overlayId);

    private void SetInteractionTarget(long? overlayId)
    {
        if (_interactionTargetOverlayId == overlayId)
        {
            return;
        }

        _interactionTargetOverlayId = overlayId;
        if (overlayId is { } currentId && FindOverlay(currentId) is { } currentOverlay)
        {
            _log.Info(
                $"[interaction] 手部接触空间对象：Overlay={currentId}，" +
                $"类型={currentOverlay.Kind}，手={_interactionContactHand?.ToString() ?? "Unknown"}。");
        }
    }

    private void SetContactHighlights(
        OverlayContact? left,
        OverlayContact? right,
        IEnumerable<OverlayGrab>? grabs = null)
    {
        var next = OverlayContactHighlights.Select(left, right, grabs);
        foreach (var overlayId in _contactOverlayIds.Concat(next).Distinct())
        {
            if (_contactOverlayIds.Contains(overlayId) == next.Contains(overlayId))
            {
                continue;
            }

            if (FindOverlay(overlayId) is { } overlay)
            {
                var highlighted = next.Contains(overlayId);
                overlay.IsHighlighted = highlighted;
                overlay.IsDirty = true;
                overlay.SynchronizeTextureBuffers = true;
                overlay.WindowSource?.SetInteractionHighlighted(highlighted);
                _log.Info(
                    $"[interaction] 接触边框：Overlay={overlayId}，" +
                    $"状态={(highlighted ? "On" : "Off")}，" +
                    $"来源={(highlighted && _grabs.ContainsOverlay(overlayId) ? "Grab" : highlighted ? "Touch" : "None")}。");
            }
        }

        _contactOverlayIds.Clear();
        _contactOverlayIds.UnionWith(next);
    }

    private InteractiveOverlay? ResolveInteractionTarget(bool capturesOnly = false)
    {
        if (_grabs.HasMultiple)
        {
            return null;
        }

        var id = _grabs.Single?.OverlayId ?? _interactionTargetOverlayId;
        var overlay = id is { } overlayId ? FindOverlay(overlayId) : null;
        return capturesOnly && overlay?.Kind != InteractiveOverlayKind.Capture ? null : overlay;
    }

    private SpatialSelectionPlane CreateResultPlaneInFront(
        SpatialSelectionPlane source,
        AssistantRequestMode mode)
    {
        var resultPlane = ResultOverlayLayout.ApplyModeDimensions(source, mode);
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            CurrentTrackingOrigin(),
            0f,
            poses);
        var hmdPose = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!hmdPose.bPoseIsValid)
        {
            return resultPlane;
        }

        var matrix = hmdPose.mDeviceToAbsoluteTracking;
        var forward = Forward(matrix);
        var right = Right(matrix);
        var normal = forward * -1f;
        var up = Vector3f.Cross(normal, right).Normalized();
        return resultPlane with
        {
            Center = Position(matrix) + (forward * ResultPlaneDistance),
            Right = right,
            Up = up,
            Normal = normal,
            ViewAngleDegrees = 0
        };
    }

    private void SetHeadLockedSelectionOverlayTransform()
    {
        EnsureOverlay(
            OpenVR.Overlay.SetOverlayWidthInMeters(_selectionOverlayHandle, InstructionPlaneWidth),
            "设置提示叠加层宽度");
        var transform = new HmdMatrix34_t
        {
            m0 = 1f,
            m5 = 1f,
            m10 = 1f,
            m11 = -InstructionPlaneDistance
        };
        EnsureOverlay(
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(
                _selectionOverlayHandle,
                OpenVR.k_unTrackedDeviceIndex_Hmd,
                ref transform),
            "设置头显相对提示层");
    }

    private void SetSpatialOverlayTransform(SpatialSelectionPlane plane)
    {
        EnsureOverlay(
            OpenVR.Overlay.SetOverlayWidthInMeters(_selectionOverlayHandle, plane.OverlayExtent),
            "设置空间框选宽度");
        var transform = new HmdMatrix34_t
        {
            m0 = plane.Right.X,
            m1 = plane.Up.X,
            m2 = plane.Normal.X,
            m3 = plane.Center.X,
            m4 = plane.Right.Y,
            m5 = plane.Up.Y,
            m6 = plane.Normal.Y,
            m7 = plane.Center.Y,
            m8 = plane.Right.Z,
            m9 = plane.Up.Z,
            m10 = plane.Normal.Z,
            m11 = plane.Center.Z
        };
        EnsureOverlay(
            OpenVR.Overlay.SetOverlayTransformAbsolute(
                _selectionOverlayHandle,
                CurrentTrackingOrigin(),
                ref transform),
            "设置双手空间框选平面");
    }

    private static Vector3f Position(HmdMatrix34_t matrix) =>
        new(matrix.m3, matrix.m7, matrix.m11);

    private static Vector3f Forward(HmdMatrix34_t matrix) =>
        new Vector3f(-matrix.m2, -matrix.m6, -matrix.m10).Normalized();

    private static Vector3f Right(HmdMatrix34_t matrix) =>
        new Vector3f(matrix.m0, matrix.m4, matrix.m8).Normalized();

    private static ETrackingUniverseOrigin CurrentTrackingOrigin() =>
        OpenVR.Compositor.GetTrackingSpace();

    private void ShowSelectionOverlay() =>
        EnsureOverlay(OpenVR.Overlay.ShowOverlay(_selectionOverlayHandle), "显示选择叠加层");

    private void HideSelectionOverlay()
    {
        if (_selectionOverlayHandle != OpenVR.k_ulOverlayHandleInvalid)
        {
            _ = OpenVR.Overlay.HideOverlay(_selectionOverlayHandle);
            _ = OpenVR.Overlay.ClearOverlayTexture(_selectionOverlayHandle);
        }
    }

    private void HideInteractiveOverlays()
    {
        _spatialOverlayManager?.HideAll();
        foreach (var overlay in _interactiveOverlays)
        {
            overlay.IsShown = false;
        }
    }

    private void PulseLeft(float duration, float frequency, float amplitude) =>
        _ = OpenVR.Input.TriggerHapticVibrationAction(
            _leftHapticActionHandle,
            0f,
            duration,
            frequency,
            amplitude,
            OpenVR.k_ulInvalidInputValueHandle);

    private void PulseBoth(float duration, float frequency, float amplitude)
    {
        _ = OpenVR.Input.TriggerHapticVibrationAction(
            _leftHapticActionHandle,
            0f,
            duration,
            frequency,
            amplitude,
            OpenVR.k_ulInvalidInputValueHandle);
        _ = OpenVR.Input.TriggerHapticVibrationAction(
            _rightHapticActionHandle,
            0f,
            duration,
            frequency,
            amplitude,
            OpenVR.k_ulInvalidInputValueHandle);
    }

    private void PulseRight(float duration, float frequency, float amplitude)
    {
        _ = OpenVR.Input.TriggerHapticVibrationAction(
            _rightHapticActionHandle,
            0f,
            duration,
            frequency,
            amplitude,
            OpenVR.k_ulInvalidInputValueHandle);
    }

    private bool ConsumeRuntimeQuitEvent()
    {
        var systemEvent = new VREvent_t();
        while (OpenVR.System.PollNextEvent(
                   ref systemEvent,
                   unchecked((uint)Marshal.SizeOf<VREvent_t>())))
        {
            if ((EVREventType)systemEvent.eventType == EVREventType.VREvent_Quit)
            {
                return true;
            }
        }

        return false;
    }

    private void ShutdownOpenVr()
    {
        Volatile.Write(ref _currentSceneProcessId, 0);
        if (_openVrInitialized || _connected)
        {
            _log.Info("正在释放 OpenVR Overlay、动作句柄和运行时连接。");
        }
        _connected = false;
        _lastTogglePressed = false;
        _idleTogglePressedAt = null;
        _idleToggleLongHoldActivated = false;
        _lastResultClickPressed = false;
        _suppressResultClickRelease = false;
        _lastLeftGripPressed = false;
        _lastRightGripPressed = false;
        _lastLeftPointerPressed = false;
        _lastRightPointerPressed = false;
        _diagnosticPressedOverlayIds.Clear();
        ResetOverlayPointer();
        _interactionTargetOverlayId = null;
        _interactionContactHand = null;
        _activeCaptureOperationId = null;
        _grabs.Clear();
        _vrVoiceInputPressed = false;
        if (!_desktopVoiceInputPressed)
        {
            _voiceInputPressed = false;
            _voiceInputWasPhysicallyPressed = false;
            _voiceInputReleaseCandidateAt = null;
        }
        _lastRenderedSnapshot = null;
        _interactiveOverlaysSuppressed = false;
        _commandPress.Reset();
        _toolbarCommandPressOverlayId = null;
        _commandTargetOverlayId = null;
        _overlayCloseHold.Reset();
        foreach (var overlay in _interactiveOverlays)
        {
            overlay.IsCommandRecording = false;
            overlay.CloseHoldProgress = null;
        }
        if (_commandRecorder.IsRecording)
        {
            try
            {
                _commandRecorder.CancelAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                _log.Error("释放 SteamVR 时取消语音命令录音失败。", exception);
            }
        }
        if (_voiceInputRecorder.IsRecording && !_desktopVoiceInputPressed)
        {
            try
            {
                _voiceInputRecorder.CancelAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                _log.Error("释放 SteamVR 时取消 VRChat 语音输入录音失败。", exception);
            }
        }
        _ = SetVrChatTypingSafeAsync(false, CancellationToken.None);
        _spatialOverlayManager?.Dispose();
        _spatialOverlayManager = null;
        if (_openVrInitialized)
        {
            _pipeline.ReleaseOpenVrResources();
            var overlayApi = OpenVR.Overlay;
            var handles = new[] { _selectionOverlayHandle };
            foreach (var handle in handles)
            {
                if (handle == OpenVR.k_ulOverlayHandleInvalid || overlayApi is null)
                {
                    continue;
                }

                _ = overlayApi.HideOverlay(handle);
                _ = overlayApi.ClearOverlayTexture(handle);
                _ = overlayApi.DestroyOverlay(handle);
            }

            OpenVR.Shutdown();
        }

        _selectionOverlayTexture?.Dispose();
        _selectionOverlayTexture = null;
        foreach (var overlay in _interactiveOverlays)
        {
            overlay.IsShown = false;
            overlay.IsHighlighted = false;
            overlay.IsDirty = true;
            overlay.IsChromeDirty = true;
            overlay.IsProgressDirty = true;
            overlay.SynchronizeTextureBuffers = true;
            overlay.SynchronizeVideoTextureBuffers = true;
            overlay.SynchronizeChromeTextureBuffers = true;
            overlay.SynchronizeProgressTextureBuffers = true;
            overlay.SynchronizePointerTextureBuffers = true;
            overlay.RenderedPointerPressed = null;
        }
        _overlayTextureDevice?.Dispose();
        _overlayTextureDevice = null;

        _openVrInitialized = false;
        _selectionOverlayHandle = OpenVR.k_ulOverlayHandleInvalid;
        _globalActionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
        _selectionActionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
        _resultsActionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
        _voiceInputActionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
        _toggleActionHandle = OpenVR.k_ulInvalidActionHandle;
        _leftTriggerActionHandle = OpenVR.k_ulInvalidActionHandle;
        _rightTriggerActionHandle = OpenVR.k_ulInvalidActionHandle;
        _leftHapticActionHandle = OpenVR.k_ulInvalidActionHandle;
        _rightHapticActionHandle = OpenVR.k_ulInvalidActionHandle;
        _resultScrollActionHandle = OpenVR.k_ulInvalidActionHandle;
        _overlayToggleActionHandle = OpenVR.k_ulInvalidActionHandle;
        _leftGripActionHandle = OpenVR.k_ulInvalidActionHandle;
        _rightGripActionHandle = OpenVR.k_ulInvalidActionHandle;
        _leftPointerClickActionHandle = OpenVR.k_ulInvalidActionHandle;
        _rightPointerClickActionHandle = OpenVR.k_ulInvalidActionHandle;
        _voiceInputPttActionHandle = OpenVR.k_ulInvalidActionHandle;
    }

    private static void ResolveActionSet(string path, ref ulong handle)
    {
        handle = OpenVR.k_ulInvalidActionSetHandle;
        EnsureInput(OpenVR.Input.GetActionSetHandle(path, ref handle), $"解析动作集 {path}");
    }

    private static void ResolveAction(string path, ref ulong handle)
    {
        handle = OpenVR.k_ulInvalidActionHandle;
        EnsureInput(OpenVR.Input.GetActionHandle(path, ref handle), $"解析动作 {path}");
    }

    private static void EnsureInput(EVRInputError error, string operation)
    {
        if (error != EVRInputError.None)
        {
            throw new InvalidOperationException($"{operation}失败：{error}");
        }
    }

    private static void EnsureOverlay(EVROverlayError error, string operation)
    {
        if (error != EVROverlayError.None)
        {
            throw new InvalidOperationException($"{operation}失败：{error}");
        }
    }

    private static string L(string key) => AppLocalization.Text(key);

    private static string LF(string key, params object?[] args) =>
        AppLocalization.Format(key, args);

    private void Publish(string message, SelectionState state, bool isError = false)
    {
        var logMessage = $"{DiagnosticTag()} [state] [{state}] {message}";
        if (isError)
        {
            _log.Error(logMessage);
        }
        else
        {
            _log.Info(logMessage);
        }

        StatusChanged?.Invoke(this, new SteamVrRuntimeEventArgs(message, state, isError));
    }

    private string DiagnosticTag(long? operationId = null, long? requestId = null)
    {
        operationId ??= _activeCaptureOperationId;
        var operation = operationId is { } op ? $"op={op:D4}" : "op=----";
        var request = requestId is { } req ? $" request={req:D4}" : string.Empty;
        return $"[{operation}{request}]";
    }

    private static string DescribePlane(SpatialSelectionPlane? plane)
    {
        if (plane is not { } value)
        {
            return "，平面=<不可用>";
        }

        return
            $"，平面={value.Width:F3}m x {value.Height:F3}m" +
            $"，中心=({value.Center.X:F3},{value.Center.Y:F3},{value.Center.Z:F3})" +
            $"，视线夹角={value.ViewAngleDegrees:F1}°";
    }
}

internal sealed record PreparedResultContent(
    string DisplayText,
    string VisibleText,
    ResultContentFormat Format,
    byte[]? RenderedImage);

internal abstract record DiagnosticResultOverlayCommand;

internal sealed record ShowDiagnosticMarkdownOverlayCommand(
    string Markdown,
    TaskCompletionSource<long> Completion) : DiagnosticResultOverlayCommand;

internal sealed record ShowDiagnosticChatOverlayCommand(
    AssistantConversationView Chat,
    TaskCompletionSource<long> Completion) : DiagnosticResultOverlayCommand;

internal sealed record UpdateDiagnosticChatOverlayCommand(
    long CommandId,
    long OverlayId,
    AssistantConversationView Chat) : DiagnosticResultOverlayCommand;

internal sealed record ScrollDiagnosticResultOverlayCommand(
    long RequestId,
    long OverlayId,
    double Delta) : DiagnosticResultOverlayCommand;

internal sealed record CloseDiagnosticResultOverlayCommand(
    long OverlayId,
    TaskCompletionSource<bool> Completion) : DiagnosticResultOverlayCommand;

internal sealed class WpfOverlayFrameRenderedEventArgs(
    long overlayId,
    string name,
    int pixelWidth,
    int pixelHeight,
    TimeSpan rasterizeDuration,
    TimeSpan uploadDuration,
    TimeSpan totalDuration,
    DateTimeOffset renderedAt,
    bool isDirectPixels = false) : EventArgs
{
    public long OverlayId { get; } = overlayId;

    public string Name { get; } = name;

    public int PixelWidth { get; } = pixelWidth;

    public int PixelHeight { get; } = pixelHeight;

    public TimeSpan RasterizeDuration { get; } = rasterizeDuration;

    public TimeSpan UploadDuration { get; } = uploadDuration;

    public TimeSpan TotalDuration { get; } = totalDuration;

    public DateTimeOffset RenderedAt { get; } = renderedAt;

    public bool IsDirectPixels { get; } = isDirectPixels;
}

internal sealed class ResultOverlayFrameRenderedEventArgs(
    long overlayId,
    ResultContentFormat contentFormat,
    bool isChat,
    int pixelWidth,
    int pixelHeight,
    TimeSpan rasterizeDuration,
    TimeSpan uploadDuration,
    TimeSpan totalDuration,
    DateTimeOffset renderedAt) : EventArgs
{
    public long OverlayId { get; } = overlayId;

    public ResultContentFormat ContentFormat { get; } = contentFormat;

    public bool IsChat { get; } = isChat;

    public int PixelWidth { get; } = pixelWidth;

    public int PixelHeight { get; } = pixelHeight;

    public TimeSpan RasterizeDuration { get; } = rasterizeDuration;

    public TimeSpan UploadDuration { get; } = uploadDuration;

    public TimeSpan TotalDuration { get; } = totalDuration;

    public DateTimeOffset RenderedAt { get; } = renderedAt;
}

internal sealed class DiagnosticRuntimeFrameCompletedEventArgs(
    long completedTimestamp,
    TimeSpan workDuration) : EventArgs
{
    public long CompletedTimestamp { get; } = completedTimestamp;

    public TimeSpan WorkDuration { get; } = workDuration;
}

internal sealed class DiagnosticWpfPointerProcessedEventArgs(
    long requestId,
    long overlayId,
    bool isPressed,
    NormalizedPoint requestedTexturePoint,
    NormalizedPoint? resolvedTexturePoint,
    string? hoveredControlName,
    bool usedOpenVrIntersection,
    bool leftWindow,
    long processedTimestamp,
    TimeSpan queueDuration) : EventArgs
{
    public long RequestId { get; } = requestId;

    public long OverlayId { get; } = overlayId;

    public bool IsPressed { get; } = isPressed;

    public NormalizedPoint RequestedTexturePoint { get; } = requestedTexturePoint;

    public NormalizedPoint? ResolvedTexturePoint { get; } = resolvedTexturePoint;

    public string? HoveredControlName { get; } = hoveredControlName;

    public bool UsedOpenVrIntersection { get; } = usedOpenVrIntersection;

    public bool LeftWindow { get; } = leftWindow;

    public long ProcessedTimestamp { get; } = processedTimestamp;

    public TimeSpan QueueDuration { get; } = queueDuration;
}

internal sealed class DiagnosticWpfInteractionCompletedEventArgs(
    long requestId,
    long overlayId,
    DiagnosticWpfInteractionKind kind,
    string? controlName,
    bool succeeded,
    long completedTimestamp,
    TimeSpan requestToTextureDuration,
    TimeSpan processedToTextureDuration,
    string? failure) : EventArgs
{
    public long RequestId { get; } = requestId;

    public long OverlayId { get; } = overlayId;

    public DiagnosticWpfInteractionKind Kind { get; } = kind;

    public string? ControlName { get; } = controlName;

    public bool Succeeded { get; } = succeeded;

    public long CompletedTimestamp { get; } = completedTimestamp;

    public TimeSpan RequestToTextureDuration { get; } = requestToTextureDuration;

    public TimeSpan ProcessedToTextureDuration { get; } = processedToTextureDuration;

    public string? Failure { get; } = failure;
}

internal sealed class ActiveSubmission
{
    private int _resultUpdateSequence;

    public required long OperationId { get; init; }

    public required long RequestId { get; init; }

    public required long SourceOverlayId { get; init; }

    public required long OverlayId { get; init; }

    public required AssistantRequestMode Mode { get; init; }

    public required IReadOnlyList<AssistantConversationTurn> ConversationHistory { get; init; }

    public required Stopwatch Stopwatch { get; init; }

    public required CancellationTokenSource Cancellation { get; init; }

    public AssistantConversation? Conversation { get; init; }

    public string? CustomCommand { get; set; }

    public ResultOverlaySnapshot? PreviousResult { get; init; }

    public Task<TranslationResult> Task { get; set; } = null!;

    public int ResultUpdateSequence => Volatile.Read(ref _resultUpdateSequence);

    public int NextResultUpdateSequence() => Interlocked.Increment(ref _resultUpdateSequence);
}

internal enum CommandButtonEventKind
{
    Down,
    Up,
    ShortPress
}

internal enum VrControlPanelAction
{
    StartCapture,
    OpenSubtitles
}

internal enum ResultScrollBoundary
{
    None,
    Top,
    Bottom
}

internal readonly record struct CommandButtonEvent(CommandButtonEventKind Kind, string Source);

public sealed class SteamVrRuntimeEventArgs(
    string message,
    SelectionState state,
    bool isError = false) : EventArgs
{
    public string Message { get; } = message;

    public SelectionState State { get; } = state;

    public bool IsError { get; } = isError;
}
