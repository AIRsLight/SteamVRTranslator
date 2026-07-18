using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Interaction;
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
    private const string ResultClickActionPath = "/actions/results/in/toggle_visibility";
    private const string LeftGripActionPath = "/actions/results/in/left_grip";
    private const string RightGripActionPath = "/actions/results/in/right_grip";
    private const string LeftPointerClickActionPath = "/actions/results/in/left_pointer_click";
    private const string RightPointerClickActionPath = "/actions/results/in/right_pointer_click";
    private const string VoiceInputPttActionPath = "/actions/voiceinput/in/ptt";
    private const int VoiceInputReleaseDebounceMilliseconds = 60;
    private const string SelectionOverlayKey = "io.steamvrtranslator.selection";
    private const float InstructionPlaneDistance = 1.0f;
    private const float ResultPlaneDistance = 0.6f;
    private const float InstructionPlaneWidth = 1.1f;
    private const int ReconnectDelayMilliseconds = 2000;

    private readonly AppConfiguration _configuration;
    private readonly AppLog _log;
    private readonly TranslationPipeline _pipeline;
    private readonly OverlayRenderer _renderer = new();
    private readonly SelectionStateMachine _selection;
    private readonly List<InteractiveOverlay> _interactiveOverlays = [];
    private readonly ConcurrentQueue<ResultProgressUpdate> _resultUpdates = new();
    private readonly ConcurrentQueue<CommandButtonEvent> _commandButtonEvents = new();
    private readonly ConcurrentQueue<WpfOverlayCommand> _wpfOverlayCommands = new();
    private readonly object _lifecycleSync = new();
    private readonly WasapiCommandRecorder _commandRecorder;
    private readonly WasapiCommandRecorder _voiceInputRecorder;
    private readonly VrChatOscOutput? _vrChatOscOutput;
    private readonly CommandPressTracker _commandPress;
    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;
    private Task<CapturedFrame>? _captureTask;
    private Task<TranslationResult>? _submissionTask;
    private long? _activeSubmissionRequestId;
    private long? _activeOperationId;
    private long _nextOperationId;
    private Stopwatch? _submissionStopwatch;
    private int _resultUpdateSequence;
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
    private long? _interactionTargetOverlayId;
    private ETrackedControllerRole? _interactionContactHand;
    private long? _commandTargetOverlayId;
    private long? _activeResultOverlayId;
    private OverlayGrab? _grab;
    private OverlayPointerCapture? _pointerCapture;
    private bool _lastSelectionOrientationUsable = true;
    private bool _openVrInitialized;
    private bool _connected;
    private bool _lastTogglePressed;
    private bool _lastResultClickPressed;
    private bool _lastLeftGripPressed;
    private bool _lastRightGripPressed;
    private bool _lastLeftPointerPressed;
    private bool _lastRightPointerPressed;
    private bool _voiceInputPressed;
    private bool _voiceInputWasPhysicallyPressed;
    private long? _voiceInputReleaseCandidateAt;
    private Task? _voiceInputTask;
    private int _openBindingsRequested;
    private Exception? _commandRecordingError;
    private AssistantRequestMode _pendingSubmissionMode = AssistantRequestMode.Translate;
    private Task<SenseVoiceCommandTranscriber>? _speechWarmupTask;
    private string? _lastConnectionError;
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
    private ulong _resultClickActionHandle = OpenVR.k_ulInvalidActionHandle;
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
        _selection = new SelectionStateMachine(TimeSpan.FromSeconds(configuration.SelectionTimeoutSeconds));
        _commandRecorder = new WasapiCommandRecorder(configuration.Speech, log);
        _voiceInputRecorder = new WasapiCommandRecorder(configuration.Speech, log);
        if (configuration.VrChatVoiceInput.Enabled)
        {
            _vrChatOscOutput = new VrChatOscOutput(configuration.VrChatVoiceInput);
        }
        _commandPress = new CommandPressTracker(
            TimeSpan.FromMilliseconds(configuration.Speech.HoldThresholdMilliseconds));
    }

    public event EventHandler<SteamVrRuntimeEventArgs>? StatusChanged;

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

    public Task StartAsync()
    {
        lock (_lifecycleSync)
        {
            if (_loopTask is not null)
            {
                return Task.CompletedTask;
            }

            _cancellation = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunAsync(_cancellation.Token));
            StartSpeechWarmup();
        }

        _log.Info(
            $"[startup] SteamVR 运行循环已创建：PID={Environment.ProcessId}，" +
            $"捕获眼睛={_configuration.StereoCompositionMode}，" +
            $"结果滚动反转={_configuration.InvertResultScroll}，" +
            $"按住阈值={_configuration.Speech.HoldThresholdMilliseconds} ms，" +
            $"录音范围={_configuration.Speech.MinimumDurationMilliseconds} ms-" +
            $"{_configuration.Speech.MaximumDurationSeconds} s，" +
            $"翻译Provider={_configuration.Translation.GetActiveProvider()?.DisplayName ?? "<缺失>"}");
        Publish("正在连接 SteamVR...", _selection.Snapshot.State);
        return Task.CompletedTask;
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

        Publish("已停止", SelectionState.Idle);
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
        _pipeline.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_connected)
                {
                    try
                    {
                        InitializeOpenVr();
                        _lastConnectionError = null;
                    }
                    catch (Exception exception)
                    {
                        ShutdownOpenVr();
                        if (!string.Equals(_lastConnectionError, exception.Message, StringComparison.Ordinal))
                        {
                            _lastConnectionError = exception.Message;
                            _log.Error("SteamVR 连接失败。程序将继续等待运行时。", exception);
                            Publish($"SteamVR 未连接：{exception.Message}", SelectionState.Idle, true);
                        }

                        await Task.Delay(ReconnectDelayMilliseconds, cancellationToken);
                        continue;
                    }
                }

                try
                {
                    if (ConsumeRuntimeQuitEvent())
                    {
                        _selection.Cancel(DateTimeOffset.Now);
                        ShutdownOpenVr();
                        Publish("SteamVR 已退出，正在等待重新启动...", SelectionState.Idle, true);
                        await Task.Delay(ReconnectDelayMilliseconds, cancellationToken);
                        continue;
                    }

                    DrainWpfOverlayCommands();
                    PollFrame(cancellationToken);
                    await CompleteCaptureIfReadyAsync();
                    await CompleteSubmissionIfReadyAsync();
                    DrainResultUpdates();
                    RenderInteractiveOverlays();
                    await Task.Delay(16, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _log.Error("SteamVR 运行循环发生错误，准备重新连接。", exception);
                    Publish($"SteamVR 错误：{exception.Message}", _selection.Snapshot.State, true);
                    _selection.Cancel(DateTimeOffset.Now);
                    ShutdownOpenVr();
                    await Task.Delay(ReconnectDelayMilliseconds, cancellationToken);
                }
            }
        }
        finally
        {
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
            if (_submissionTask is not null)
            {
                try
                {
                    await _submissionTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _log.Error("停止时等待翻译任务失败。", exception);
                }

                _submissionTask = null;
            }
        }
    }

    private void PollFrame(CancellationToken cancellationToken)
    {
        UpdateActionState();

        var togglePressed = ReadDigital(_toggleActionHandle);
        var now = DateTimeOffset.Now;
        if (_configuration.VrChatVoiceInput.Enabled)
        {
            UpdateVrChatVoiceInput(ReadDigital(_voiceInputPttActionHandle), cancellationToken);
        }
        UpdateOverlayInteraction();
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

            var transition = _selection.Update(
                leftPressed,
                rightPressed,
                plane?.LeftPointer,
                plane?.RightPointer,
                DateTimeOffset.Now,
                plane?.HasUsableOrientation ?? false);
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
            RenderSelectionIfNeeded(force: transition.Kind != SelectionTransitionKind.None);
        }

        UpdateResultControls();

        HandleTransition(_selection.Tick(now), cancellationToken);
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

        if (_captureTask is { IsCompleted: false } || _submissionTask is { IsCompleted: false })
        {
            Publish("当前仍有捕获或翻译任务正在执行。", SelectionState.Idle, true);
            return;
        }

        var target = ResolveInteractionTarget(capturesOnly: true);
        if (target is null)
        {
            if (_interactionTargetOverlayId is not null)
            {
                Publish("左摇杆只能对截图执行翻译；请触碰截图后重试。", SelectionState.Idle, true);
                return;
            }

            HandleTransition(_selection.Toggle(now), cancellationToken);
            return;
        }

        if (_commandPress.IsPressed)
        {
            _log.Warning($"{DiagnosticTag()} [input] 忽略重复的翻译键按下事件：来源={source}");
            return;
        }

        _commandPress.Press(now);
        _commandTargetOverlayId = target.Id;
        _pendingSubmissionMode = AssistantRequestMode.Translate;
        _commandRecordingError = null;
        try
        {
            _commandRecorder.Start();
            _log.Info(
                $"{DiagnosticTag()} [input] 已开始暂存语音命令；" +
                "短按松开时将丢弃录音并直接翻译。");
            Publish("松开可直接翻译；继续按住可说出自定义命令。", SelectionState.Idle);
        }
        catch (Exception exception)
        {
            _commandRecordingError = exception;
            _log.Error("无法开始语音命令录音；短按翻译仍可使用。", exception);
            Publish("麦克风不可用；短按仍可直接翻译。", SelectionState.Idle, true);
        }
    }

    private void HandleCommandButtonUp(
        string source,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var heldDuration = _commandPress.HeldDuration(now);
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

        _log.Info(
            $"{DiagnosticTag()} [input] 翻译键松开：来源={source}，" +
            $"持续={heldDuration.TotalMilliseconds:F0} ms，手势={kind}，" +
            $"长按阈值={_configuration.Speech.HoldThresholdMilliseconds} ms");
        _pendingSubmissionMode = kind == CommandPressKind.CustomCommand
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
            Publish(
                _commandRecordingError is null
                    ? "正在听取自定义命令；松开后识别。"
                    : "麦克风不可用；松开后将显示详细错误。",
                SelectionState.Idle,
                _commandRecordingError is not null);
        }

        if (_commandPress.HeldDuration(now) >= TimeSpan.FromSeconds(_configuration.Speech.MaximumDurationSeconds))
        {
            _log.Warning($"语音命令达到 {_configuration.Speech.MaximumDurationSeconds} 秒上限，自动提交。");
            HandleCommandButtonUp("录音时长上限", now, cancellationToken);
        }
    }

    private void HandleTransition(SelectionTransition transition, CancellationToken cancellationToken)
    {
        switch (transition.Kind)
        {
            case SelectionTransitionKind.None:
                return;
            case SelectionTransitionKind.Armed:
                _activeOperationId = Interlocked.Increment(ref _nextOperationId);
                _currentPlane = null;
                _lockedPlane = null;
                _lastSelectionOrientationUsable = true;
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
                Publish("选择模式：按住双手扳机调整范围。", transition.Snapshot.State);
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
                Publish("正在调整范围；松开任一扳机立即捕获。", transition.Snapshot.State);
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
                Publish("范围已固定，正在立即捕获画面。", transition.Snapshot.State);
                break;
            case SelectionTransitionKind.RegionRejected:
                ResetRejectedSelection(transition.Snapshot);
                PulseBoth(0.08f, 35f, 0.3f);
                Publish("选择范围过小，请重新框选。", transition.Snapshot.State, true);
                break;
            case SelectionTransitionKind.OrientationRejected:
                PulseBoth(0.08f, 35f, 0.3f);
                if (_currentPlane is { } rejectedPlane)
                {
                    _log.Warning(
                        $"{DiagnosticTag()} [selection] 相框角度无效：" +
                        $"视线夹角={rejectedPlane.ViewAngleDegrees:F1}°/" +
                        $"上限={SpatialSelectionPlane.MaximumViewAngleDegrees:F0}°");
                }

                ResetRejectedSelection(transition.Snapshot);
                Publish("相框角度过大，请让相框正对当前视线。", transition.Snapshot.State, true);
                break;
            case SelectionTransitionKind.SubmissionRequested:
                if (_lockedPlane is not { } submittedPlane || !submittedPlane.IsUsable)
                {
                    HideSelectionOverlay();
                    _currentPlane = null;
                    _lockedPlane = null;
                    _selection.Complete(DateTimeOffset.Now);
                    RestoreInteractiveOverlays("框选平面无效");
                    Publish("空间框选平面无效，请重新框选。", SelectionState.Idle, true);
                    break;
                }

                HideSelectionOverlay();
                _lastRenderedSnapshot = null;
                var operationId = _activeOperationId ?? Interlocked.Increment(ref _nextOperationId);
                _activeOperationId = operationId;
                _log.Info(
                    $"{DiagnosticTag(operationId, null)} [capture] 松开扳机后立即捕获：" +
                    DescribePlane(submittedPlane));
                _capturePlane = submittedPlane;
                _captureTask = _pipeline.CaptureAsync(
                    submittedPlane,
                    cancellationToken,
                    DiagnosticTag(operationId, null));
                Publish("正在捕获所选区域...", transition.Snapshot.State);
                break;
            case SelectionTransitionKind.Cancelled:
                _log.Info($"{DiagnosticTag()} [selection] 操作已取消。");
                _lastResultClickPressed = true;
                HideSelectionOverlay();
                _currentPlane = null;
                _lockedPlane = null;
                RestoreInteractiveOverlays("取消框选");
                Publish("已取消选择。", transition.Snapshot.State);
                break;
            case SelectionTransitionKind.TimedOut:
                _log.Warning($"{DiagnosticTag()} [selection] 操作超时。");
                HideSelectionOverlay();
                _currentPlane = null;
                _lockedPlane = null;
                RestoreInteractiveOverlays("框选超时");
                Publish("选择超时，已取消。", transition.Snapshot.State, true);
                break;
            case SelectionTransitionKind.Completed:
                HideSelectionOverlay();
                _currentPlane = null;
                _lockedPlane = null;
                _activeOperationId = null;
                Publish(
                    _interactiveOverlays.Any(item => item.Kind == InteractiveOverlayKind.Capture)
                        ? "就绪；触碰截图后按左摇杆翻译，抓握可自由移动。"
                        : "就绪",
                    transition.Snapshot.State);
                break;
        }
    }

    private void StartTranslationForCommandTarget(CancellationToken cancellationToken)
    {
        var source = _commandTargetOverlayId is { } targetId
            ? FindOverlay(targetId)
            : null;
        _commandTargetOverlayId = null;
        if (source is not { Kind: InteractiveOverlayKind.Capture, Capture: { } capture })
        {
            _ = _commandRecorder.CancelAsync(CancellationToken.None);
            Publish("目标截图已不存在，翻译已取消。", SelectionState.Idle, true);
            return;
        }

        if (_submissionTask is { IsCompleted: false })
        {
            _ = _commandRecorder.CancelAsync(CancellationToken.None);
            Publish("已有翻译任务正在执行。", SelectionState.Idle, true);
            return;
        }

        var mode = _pendingSubmissionMode;
        var operationId = Interlocked.Increment(ref _nextOperationId);
        var resultState = new ResultOverlayState();
        var initialStatus = mode == AssistantRequestMode.CustomCommand
            ? "正在识别语音命令..."
            : "正在翻译...";
        var requestId = resultState.Begin(initialStatus);
        var resultPlane = CreateResultPlaneInFront(source.Plane);
        var resultOverlay = AddInteractiveOverlay(
            InteractiveOverlayKind.Result,
            resultPlane,
            capture: null,
            resultState);
        _activeOperationId = operationId;
        _activeSubmissionRequestId = requestId;
        _activeResultOverlayId = resultOverlay.Id;
        _submissionStopwatch = Stopwatch.StartNew();
        _resultUpdateSequence = 0;
        _log.Info(
            $"{DiagnosticTag(operationId, requestId)} [submit] 从截图对象启动请求：" +
            $"源Overlay={source.Id}，结果Overlay={resultOverlay.Id}，模式={mode}");
        _submissionTask = ExecuteTranslationAsync(
            capture,
            mode,
            operationId,
            requestId,
            text => QueueResultUpdate(resultOverlay.Id, operationId, requestId, text),
            cancellationToken);
        Publish(initialStatus, SelectionState.Idle);
    }

    private async Task<TranslationResult> ExecuteTranslationAsync(
        CapturedFrame capture,
        AssistantRequestMode mode,
        long operationId,
        long requestId,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        var diagnosticTag = DiagnosticTag(operationId, requestId);
        _log.Info($"{diagnosticTag} [submit] 使用已有截图，开始处理命令。");
        string? customCommand = null;
        try
        {
            if (mode == AssistantRequestMode.CustomCommand)
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
                QueueResultUpdate(
                    _activeResultOverlayId ?? 0,
                    operationId,
                    requestId,
                    $"识别命令：{customCommand}\n\n正在分析画面...");
                Publish($"已识别命令：{customCommand}", SelectionState.Idle);
            }
            else
            {
                _log.Info($"{diagnosticTag} [audio] 短按模式，取消并删除暂存录音。");
                await _commandRecorder.CancelAsync(cancellationToken);
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
        return await _pipeline.ExecuteAsync(
            capture,
            mode,
            customCommand,
            onPartialResult,
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
            Publish("截图已生成；触碰或抓住截图后按左摇杆翻译。", SelectionState.Idle);
        }
        catch (Exception exception)
        {
            _log.Error($"{DiagnosticTag()} [capture] 捕获失败。", exception);
            Publish($"捕获失败：{exception.Message}", SelectionState.Idle, true);
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

    private async Task CompleteSubmissionIfReadyAsync()
    {
        if (_submissionTask is null || !_submissionTask.IsCompleted)
        {
            return;
        }

        try
        {
            var result = await _submissionTask;
            var diagnosticTag = DiagnosticTag(_activeOperationId, _activeSubmissionRequestId);
            var text = string.IsNullOrWhiteSpace(result.Text)
                ? $"截图已保存\n{Path.GetFileName(result.CapturePath)}"
                : result.Text;
            var resultOverlay = _activeResultOverlayId is { } overlayId
                ? FindOverlay(overlayId)
                : null;
            if (_activeSubmissionRequestId is { } requestId && resultOverlay?.Result is { } resultState)
            {
                resultState.Complete(requestId, text, isError: false);
                resultOverlay.IsDirty = true;
            }
            var operationName = result.Mode == AssistantRequestMode.CustomCommand ? "自定义命令" : "翻译";
            _log.Info(
                $"{diagnosticTag} [submit] {operationName}完成：" +
                $"总耗时={_submissionStopwatch?.Elapsed.TotalMilliseconds:F0} ms，" +
                $"结果字符={result.Text?.Length ?? 0}，流式更新={_resultUpdateSequence}，" +
                $"截图={Path.GetFileName(result.CapturePath)}");
            Publish($"{operationName}完成。", SelectionState.Idle);
        }
        catch (Exception exception)
        {
            _log.Error(
                $"{DiagnosticTag(_activeOperationId, _activeSubmissionRequestId)} [submit] " +
                $"捕获或翻译失败：总耗时={_submissionStopwatch?.Elapsed.TotalMilliseconds:F0} ms。",
                exception);
            var resultOverlay = _activeResultOverlayId is { } overlayId
                ? FindOverlay(overlayId)
                : null;
            if (_activeSubmissionRequestId is { } requestId && resultOverlay?.Result is { } resultState)
            {
                resultState.Complete(requestId, $"翻译失败\n{exception.Message}", isError: true);
                resultOverlay.IsDirty = true;
            }
            Publish($"翻译失败：{exception.Message}", SelectionState.Idle, true);
        }
        finally
        {
            _submissionStopwatch?.Stop();
            _submissionTask = null;
            _activeSubmissionRequestId = null;
            _activeResultOverlayId = null;
            _submissionStopwatch = null;
            _activeOperationId = null;
            RenderInteractiveOverlays(force: true);
        }
    }

    private void QueueResultUpdate(long overlayId, long operationId, long requestId, string text)
    {
        var sequence = Interlocked.Increment(ref _resultUpdateSequence);
        _resultUpdates.Enqueue(
            new ResultProgressUpdate(
                overlayId,
                requestId,
                operationId,
                sequence,
                text,
                DateTimeOffset.Now));
    }

    private void DrainResultUpdates()
    {
        while (_resultUpdates.TryDequeue(out var update))
        {
            var overlay = FindOverlay(update.OverlayId);
            var accepted = overlay?.Result?.Update(update.RequestId, update.Text) == true;
            var queueDelay = DateTimeOffset.Now - update.QueuedAt;
            if (accepted)
            {
                overlay!.IsDirty = true;
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
        foreach (var overlay in _interactiveOverlays)
        {
            overlay.IsDirty = true;
        }
        if (_spatialOverlaysVisible)
        {
            RenderInteractiveOverlays(force: true);
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
                                source.Options.CanGrab);
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
        return new SpatialSelectionPlane(
            Position(matrix) + (forward * source.Options.DistanceMeters),
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
            _log.Info("[voice-input] SteamVR PTT 按下，开始 VRChat 语音输入录音。");
            _ = SetVrChatTypingSafeAsync(true, cancellationToken);
            Publish("VRChat 语音输入录音中...", _selection.Snapshot.State);
        }
        catch (Exception exception)
        {
            _voiceInputPressed = false;
            _log.Error("[voice-input] 启动录音失败。", exception);
            Publish($"VRChat 录音失败：{exception.Message}", _selection.Snapshot.State, true);
        }
    }

    private async Task CompleteVrChatVoiceInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var audio = await _voiceInputRecorder.StopAsync(cancellationToken);
            _log.Info(
                $"[voice-input] PTT 松开：录音={audio.Duration.TotalMilliseconds:F0} ms，开始 SenseVoice 识别。");
            Publish("正在识别 VRChat 语音输入...", _selection.Snapshot.State);
            var transcriber = await GetSpeechTranscriberAsync();
            var result = await transcriber.TranscribeAsync(audio, "[voice-input]", cancellationToken);
            if (_vrChatOscOutput is null)
            {
                throw new InvalidOperationException("VRChat OSC 输出未初始化。");
            }

            await _vrChatOscOutput.SendAsync(result.Text, cancellationToken);
            _log.Info(
                $"[voice-input] OSC 已发送：目标={_configuration.VrChatVoiceInput.Host}:" +
                $"{_configuration.VrChatVoiceInput.Port}，字符={result.Text.Length}，文本={PreviewText(result.Text)}");
            Publish($"VRChat 已发送：{result.Text}", _selection.Snapshot.State);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Info("[voice-input] VRChat 语音输入已取消。");
        }
        catch (Exception exception)
        {
            _log.Error("[voice-input] 识别或 OSC 发送失败。", exception);
            Publish($"VRChat 语音输入失败：{exception.Message}", _selection.Snapshot.State, true);
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

    private void InitializeOpenVr()
    {
        _log.Info("开始初始化 OpenVR Overlay 应用。");
        if (!Environment.Is64BitProcess)
        {
            throw new InvalidOperationException("SteamVR 模块仅支持 Windows x64。");
        }

        var assets = SteamVrManifestStore.EnsureExtracted();
        _log.Info($"SteamVR 动作清单：{assets.ActionManifestPath}");
        var initError = EVRInitError.None;
        _ = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Overlay);
        if (initError != EVRInitError.None)
        {
            throw new InvalidOperationException(OpenVR.GetStringForHmdError(initError));
        }

        _openVrInitialized = true;
        var applicationError = OpenVR.Applications.AddApplicationManifest(
            assets.ApplicationManifestPath,
            bTemporary: false);
        if (applicationError is not EVRApplicationError.None and not EVRApplicationError.AppKeyAlreadyExists)
        {
            throw new InvalidOperationException($"注册 SteamVR 应用清单失败：{applicationError}");
        }

        applicationError = OpenVR.Applications.IdentifyApplication(
            unchecked((uint)Environment.ProcessId),
            SteamVrManifestStore.ApplicationKey);
        if (applicationError != EVRApplicationError.None)
        {
            throw new InvalidOperationException($"识别 SteamVR 应用失败：{applicationError}");
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
        ResolveAction(ResultClickActionPath, ref _resultClickActionHandle);
        ResolveAction(LeftGripActionPath, ref _leftGripActionHandle);
        ResolveAction(RightGripActionPath, ref _rightGripActionHandle);
        ResolveAction(LeftPointerClickActionPath, ref _leftPointerClickActionHandle);
        ResolveAction(RightPointerClickActionPath, ref _rightPointerClickActionHandle);
        ResolveAction(VoiceInputPttActionPath, ref _voiceInputPttActionHandle);
        CreateOverlays();

        _connected = true;
        _log.Info("SteamVR 叠加层和动作输入已连接。高优先级输入覆盖需要 SteamVR 开发者设置支持。");
        Publish("SteamVR 已连接，等待翻译键。", SelectionState.Idle);
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
        var existing = OpenVR.k_ulOverlayHandleInvalid;
        if (OpenVR.Overlay.FindOverlay(key, ref existing) == EVROverlayError.None)
        {
            _ = OpenVR.Overlay.DestroyOverlay(existing);
        }

        EnsureOverlay(
            OpenVR.Overlay.CreateOverlay(key, name, ref handle),
            $"创建叠加层 {name}");
        EnsureOverlay(OpenVR.Overlay.SetOverlayWidthInMeters(handle, width), "设置叠加层宽度");
        EnsureOverlay(OpenVR.Overlay.SetOverlayAlpha(handle, 1f), "设置叠加层透明度");
        EnsureOverlay(OpenVR.Overlay.SetOverlayColor(handle, 1f, 1f, 1f), "设置叠加层颜色");
        EnsureOverlay(
            OpenVR.Overlay.SetOverlayTextureColorSpace(handle, EColorSpace.Gamma),
            "设置叠加层色彩空间");
        EnsureOverlay(
            OpenVR.Overlay.SetOverlayInputMethod(handle, VROverlayInputMethod.None),
            "设置叠加层输入方式");
        EnsureOverlay(OpenVR.Overlay.SetOverlaySortOrder(handle, sortOrder), "设置叠加层层级");
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

        if (_interactiveOverlays.Count > 0)
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

    private void UpdateOverlayInteraction()
    {
        var leftGrip = ReadDigital(_leftGripActionHandle);
        var rightGrip = ReadDigital(_rightGripActionHandle);
        var leftPointerPressed = ReadDigital(_leftPointerClickActionHandle);
        var rightPointerPressed = ReadDigital(_rightPointerClickActionHandle);
        if (_interactiveOverlaysSuppressed || !_spatialOverlaysVisible || _interactiveOverlays.Count == 0)
        {
            SetInteractionTarget(null);
            _interactionContactHand = null;
            _grab = null;
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

        if (_grab is { } grab)
        {
            var hand = grab.Hand == ETrackedControllerRole.LeftHand ? leftHand : rightHand;
            var gripPressed = grab.Hand == ETrackedControllerRole.LeftHand ? leftGrip : rightGrip;
            var overlay = FindOverlay(grab.OverlayId);
            if (!gripPressed || hand is null || overlay is null)
            {
                _log.Info($"[interaction] 释放空间对象：Overlay={grab.OverlayId}。");
                _grab = null;
            }
            else
            {
                overlay.Plane = SpatialOverlayInteraction.Move(grab, hand.Value);
                _spatialOverlayManager?.UpdateTransform(overlay.Id, overlay.Plane, origin);
                _interactionContactHand = grab.Hand;
                SetInteractionTarget(overlay.Id);
            }
        }

        if (_grab is null)
        {
            var leftContact = leftHand is { } left ? FindContact(left.Position) : null;
            var rightContact = rightHand is { } right ? FindContact(right.Position) : null;
            var selectedContact = Closest(leftContact, rightContact);
            _interactionContactHand = selectedContact is null
                ? null
                : leftContact is { } selectedLeft &&
                  (rightContact is null || selectedLeft.Distance <= rightContact.Value.Distance)
                    ? ETrackedControllerRole.LeftHand
                    : ETrackedControllerRole.RightHand;
            SetInteractionTarget(selectedContact?.OverlayId);

            if (leftGrip && !_lastLeftGripPressed && leftHand is { } leftPose && leftContact is { } leftHit)
            {
                BeginGrab(leftHit.OverlayId, ETrackedControllerRole.LeftHand, leftPose);
            }
            else if (rightGrip && !_lastRightGripPressed && rightHand is { } rightPose && rightContact is { } rightHit)
            {
                BeginGrab(rightHit.OverlayId, ETrackedControllerRole.RightHand, rightPose);
            }
        }

        UpdateOverlayPointer(
            origin,
            leftHand,
            rightHand,
            leftPointerPressed,
            rightPointerPressed);
        _lastLeftGripPressed = leftGrip;
        _lastRightGripPressed = rightGrip;
        _lastLeftPointerPressed = leftPointerPressed;
        _lastRightPointerPressed = rightPointerPressed;
    }

    private void UpdateOverlayPointer(
        ETrackingUniverseOrigin origin,
        TrackedHandPose? leftHand,
        TrackedHandPose? rightHand,
        bool leftPressed,
        bool rightPressed)
    {
        var targetId = _pointerCapture?.OverlayId ?? _grab?.OverlayId ?? _interactionTargetOverlayId;
        var contactHand = _grab?.Hand ?? _interactionContactHand;
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
        if (pose is { } handPose &&
            _spatialOverlayManager.TryIntersect(
                target.Id,
                handPose.Position,
                SpatialOverlayInteraction.PointerDirection(handPose),
                origin,
                target.Plane,
                out var measuredHit))
        {
            hit = measuredHit;
            target.WindowSource?.PointerMove(measuredHit.ContentPoint);
            if (target.WindowSource is not null)
            {
                target.IsDirty = true;
            }
        }

        if (pressed && !wasPressed && hit is { } downHit)
        {
            _pointerCapture = new OverlayPointerCapture(
                target.Id,
                pointerHand.Value,
                downHit.TexturePoint,
                downHit.ContentPoint);
            target.WindowSource?.PointerDown(downHit.ContentPoint);
            if (target.WindowSource is not null)
            {
                target.IsDirty = true;
            }
            _log.Info(
                $"[pointer] 光标按下并锁定对象：Overlay={target.Id}，手={pointerHand}，" +
                $"UV=({downHit.ContentPoint.X:F3},{downHit.ContentPoint.Y:F3})。");
        }

        if (_pointerCapture is { } capture && capture.OverlayId == target.Id)
        {
            if (hit is { } currentHit)
            {
                capture = capture with
                {
                    LastTexturePoint = currentHit.TexturePoint,
                    LastContentPoint = currentHit.ContentPoint
                };
                _pointerCapture = capture;
            }

            SetPointerVisual(
                target.Id,
                new OverlayPointerVisual(
                    capture.Hand,
                    capture.LastTexturePoint,
                    capture.LastContentPoint,
                    pressed));
            if (!pressed && wasPressed)
            {
                target.WindowSource?.PointerUp(capture.LastContentPoint);
                if (target.WindowSource is not null)
                {
                    target.IsDirty = true;
                }
                _log.Info(
                    $"[pointer] 光标松开对象：Overlay={capture.OverlayId}，手={capture.Hand}，" +
                    $"UV=({capture.LastContentPoint.X:F3},{capture.LastContentPoint.Y:F3})。");
                _pointerCapture = null;
            }
            return;
        }

        SetPointerVisual(
            hit?.OverlayId,
            hit is { } hoverHit
                ? new OverlayPointerVisual(
                    pointerHand.Value,
                    hoverHit.TexturePoint,
                    hoverHit.ContentPoint,
                    false)
                : null);
    }

    private void ResetOverlayPointer()
    {
        if (_pointerCapture is { } capture)
        {
            FindOverlay(capture.OverlayId)?.WindowSource?.CancelPointer();
        }
        _pointerCapture = null;
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

            overlay.Pointer = next;
            overlay.IsDirty = true;
        }
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
               MathF.Abs(left.Value.TexturePoint.Y - right.Value.TexturePoint.Y) * OverlayRenderer.Height < 0.75f;
    }

    private static ETrackedControllerRole? Opposite(ETrackedControllerRole? hand) => hand switch
    {
        ETrackedControllerRole.LeftHand => ETrackedControllerRole.RightHand,
        ETrackedControllerRole.RightHand => ETrackedControllerRole.LeftHand,
        _ => null
    };

    private void BeginGrab(long overlayId, ETrackedControllerRole hand, TrackedHandPose pose)
    {
        var overlay = FindOverlay(overlayId);
        if (overlay is null || !overlay.CanGrab)
        {
            return;
        }

        _grab = new OverlayGrab(overlayId, hand, overlay.Plane, pose);
        _interactionContactHand = hand;
        SetInteractionTarget(overlayId);
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

    private OverlayContact? FindContact(Vector3f position)
    {
        OverlayContact? closest = null;
        foreach (var overlay in _interactiveOverlays)
        {
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

    private static OverlayContact? Closest(OverlayContact? left, OverlayContact? right)
    {
        if (left is null)
        {
            return right;
        }
        if (right is null)
        {
            return left;
        }
        return left.Value.Distance <= right.Value.Distance ? left : right;
    }

    private void UpdateResultControls()
    {
        if (_interactiveOverlays.Count == 0)
        {
            _lastResultClickPressed = false;
            _lastScrollTargetOverlayId = null;
            _resultScrollBoundary = ResultScrollBoundary.None;
            _wpfWheelAccumulator = 0;
            return;
        }

        var clickPressed = ReadDigital(_resultClickActionHandle);
        var now = DateTimeOffset.Now;
        var elapsed = _lastResultScrollAt == default
            ? TimeSpan.Zero
            : now - _lastResultScrollAt;
        _lastResultScrollAt = now;
        var canControl = !_interactiveOverlaysSuppressed && _selection.Snapshot.State == SelectionState.Idle;
        if (canControl && clickPressed && !_lastResultClickPressed)
        {
            _spatialOverlaysVisible = !_spatialOverlaysVisible;
            if (_spatialOverlaysVisible)
            {
                foreach (var overlay in _interactiveOverlays)
                {
                    overlay.IsDirty = true;
                }
                RenderInteractiveOverlays(force: true);
            }
            else
            {
                SetInteractionTarget(null);
                _interactionContactHand = null;
                _grab = null;
                ResetOverlayPointer();
                HideInteractiveOverlays();
            }
            PulseRight(0.035f, 100f, 0.3f);
            _log.Info($"[interaction] 右摇杆切换全局空间对象：显示={_spatialOverlaysVisible}。");
            Publish(
                _spatialOverlaysVisible ? "已显示空间对象和手柄。" : "已隐藏空间对象和手柄。",
                SelectionState.Idle);
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
            _configuration.InvertResultScroll,
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

    private void UpdateSelectionManagerHint(
        SpatialSelectionPlane? plane,
        bool bothTriggersPressed,
        SelectionState state)
    {
        if (!bothTriggersPressed || plane is not { } value || state != SelectionState.Sizing)
        {
            return;
        }

        var usable = value.HasUsableOrientation;
        if (usable == _lastSelectionOrientationUsable)
        {
            return;
        }

        _lastSelectionOrientationUsable = usable;
        Publish(
            usable
                ? "角度已恢复；继续调整范围，松开任一扳机立即捕获。"
                : $"角度过大：视线夹角 {value.ViewAngleDegrees:F0}°，需小于 {SpatialSelectionPlane.MaximumViewAngleDegrees:F0}°。",
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

        var frameUsable = _currentPlane?.HasUsableOrientation ?? true;
        if (SetSelectionOverlayImage(_renderer.RenderSelection(snapshot, frameUsable)))
        {
            _lastRenderedSnapshot = snapshot;
            _lastRenderAt = now;
        }
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
        _lastSelectionOrientationUsable = true;
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
            _spatialOverlayManager.UpdateTransform(overlay.Id, overlay.Plane, origin);
            RenderInteractiveOverlay(overlay, force, now);
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
        var minimumInterval = overlay.WindowSource?.FrameInterval ?? TimeSpan.FromMilliseconds(33);
        if (_spatialOverlayManager is null ||
            (!force && now - overlay.LastRenderAt < minimumInterval) ||
            (!force && !overlay.IsDirty && overlay.WindowSource is null))
        {
            return;
        }

        var highlighted = _interactionTargetOverlayId == overlay.Id;
        if (overlay.Kind == InteractiveOverlayKind.Capture && overlay.Capture is { } capture)
        {
            _spatialOverlayManager.UpdatePixels(
                overlay.Id,
                _renderer.RenderCapture(
                    capture.ImageBytes,
                    overlay.Plane,
                    highlighted,
                    overlay.Pointer));
            overlay.RenderCount++;
            if (force || overlay.RenderCount == 1)
            {
                _log.Info(
                    $"{DiagnosticTag()} [overlay] 截图层已渲染：Overlay={overlay.Id}，" +
                    $"次数={overlay.RenderCount}，高亮={highlighted}，强制={force}");
            }
        }
        else if (overlay.Result?.Snapshot() is { } snapshot)
        {
            var frame = _renderer.RenderResults(
                snapshot,
                overlay.Plane,
                highlighted,
                overlay.Pointer);
            _spatialOverlayManager.UpdatePixels(overlay.Id, frame.Pixels);
            overlay.MaximumScroll = frame.MaximumScrollOffset;
            overlay.Result.ClampScroll(overlay.MaximumScroll);
            overlay.RenderCount++;
            if (force || overlay.RenderCount == 1 || overlay.RenderCount % 10 == 0)
            {
                _log.Info(
                    $"{DiagnosticTag()} [overlay] 结果层已渲染：Overlay={overlay.Id}，" +
                    $"次数={overlay.RenderCount}，请求={snapshot.RequestId}，" +
                    $"状态={snapshot.Status}，字符={snapshot.Text.Length}，" +
                    $"滚动={snapshot.ScrollOffset:F0}，高亮={highlighted}，强制={force}");
            }
        }
        else if (overlay.WindowSource is { } windowSource)
        {
            _spatialOverlayManager.UpdatePixels(
                overlay.Id,
                _renderer.RenderWindow(
                    windowSource,
                    overlay.Plane,
                    highlighted,
                    overlay.Pointer));
            overlay.RenderCount++;
            if (force || overlay.RenderCount == 1 || overlay.RenderCount % 300 == 0)
            {
                _log.Info(
                    $"[overlay] WPF 窗口层已渲染：Overlay={overlay.Id}，" +
                    $"名称={windowSource.Options.Name}，次数={overlay.RenderCount}。");
            }
        }
        else
        {
            return;
        }

        overlay.IsDirty = false;
        overlay.LastRenderAt = now;
    }

    private InteractiveOverlay AddInteractiveOverlay(
        InteractiveOverlayKind kind,
        SpatialSelectionPlane plane,
        CapturedFrame? capture,
        ResultOverlayState? resultState,
        WpfWindowOverlaySource? windowSource = null,
        bool canGrab = true)
    {
        var overlay = new InteractiveOverlay
        {
            Id = Interlocked.Increment(ref _nextOverlayId),
            Kind = kind,
            Plane = plane,
            Capture = capture,
            Result = resultState,
            WindowSource = windowSource,
            CanGrab = canGrab
        };
        _interactiveOverlays.Add(overlay);
        overlay.IsDirty = true;
        return overlay;
    }

    private void RemoveInteractiveOverlay(long overlayId, string reason)
    {
        var overlay = FindOverlay(overlayId);
        if (overlay is null)
        {
            return;
        }

        _spatialOverlayManager?.Remove(overlayId);
        _interactiveOverlays.Remove(overlay);
        overlay.WindowSource?.Dispose();
        if (_grab?.OverlayId == overlayId)
        {
            _grab = null;
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
        Publish($"已关闭空间对象，当前剩余 {_interactiveOverlays.Count} 个。", SelectionState.Idle);
    }

    private InteractiveOverlay? FindOverlay(long overlayId) =>
        _interactiveOverlays.FirstOrDefault(item => item.Id == overlayId);

    private void SetInteractionTarget(long? overlayId)
    {
        if (_interactionTargetOverlayId == overlayId)
        {
            return;
        }

        if (_interactionTargetOverlayId is { } previousId && FindOverlay(previousId) is { } previous)
        {
            previous.IsDirty = true;
        }
        _interactionTargetOverlayId = overlayId;
        if (overlayId is { } currentId && FindOverlay(currentId) is { } current)
        {
            current.IsDirty = true;
            _log.Info($"[interaction] 手部接触空间对象：Overlay={currentId}，类型={current.Kind}。");
        }
    }

    private InteractiveOverlay? ResolveInteractionTarget(bool capturesOnly = false)
    {
        var id = _grab?.OverlayId ?? _interactionTargetOverlayId;
        var overlay = id is { } overlayId ? FindOverlay(overlayId) : null;
        return capturesOnly && overlay?.Kind != InteractiveOverlayKind.Capture ? null : overlay;
    }

    private SpatialSelectionPlane CreateResultPlaneInFront(SpatialSelectionPlane source)
    {
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            CurrentTrackingOrigin(),
            0f,
            poses);
        var hmdPose = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!hmdPose.bPoseIsValid)
        {
            return source;
        }

        var matrix = hmdPose.mDeviceToAbsoluteTracking;
        var forward = Forward(matrix);
        var right = Right(matrix);
        var normal = forward * -1f;
        var up = Vector3f.Cross(normal, right).Normalized();
        return source with
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
        if (_openVrInitialized || _connected)
        {
            _log.Info("正在释放 OpenVR Overlay、动作句柄和运行时连接。");
        }
        _connected = false;
        _lastTogglePressed = false;
        _lastResultClickPressed = false;
        _lastLeftGripPressed = false;
        _lastRightGripPressed = false;
        _lastLeftPointerPressed = false;
        _lastRightPointerPressed = false;
        ResetOverlayPointer();
        _interactionTargetOverlayId = null;
        _interactionContactHand = null;
        _grab = null;
        _voiceInputPressed = false;
        _voiceInputWasPhysicallyPressed = false;
        _voiceInputReleaseCandidateAt = null;
        _lastRenderedSnapshot = null;
        _interactiveOverlaysSuppressed = false;
        _commandPress.Reset();
        if (_commandRecorder.IsRecording && _submissionTask is null)
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
        if (_voiceInputRecorder.IsRecording)
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
            var handles = new[] { _selectionOverlayHandle };
            foreach (var handle in handles)
            {
                if (handle == OpenVR.k_ulOverlayHandleInvalid)
                {
                    continue;
                }

                _ = OpenVR.Overlay.HideOverlay(handle);
                _ = OpenVR.Overlay.ClearOverlayTexture(handle);
                _ = OpenVR.Overlay.DestroyOverlay(handle);
            }

            OpenVR.Shutdown();
        }

        _selectionOverlayTexture?.Dispose();
        _selectionOverlayTexture = null;
        foreach (var overlay in _interactiveOverlays)
        {
            overlay.IsShown = false;
            overlay.IsDirty = true;
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
        _resultClickActionHandle = OpenVR.k_ulInvalidActionHandle;
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
        operationId ??= _activeOperationId;
        requestId ??= _activeSubmissionRequestId;
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

internal enum CommandButtonEventKind
{
    Down,
    Up,
    ShortPress
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
