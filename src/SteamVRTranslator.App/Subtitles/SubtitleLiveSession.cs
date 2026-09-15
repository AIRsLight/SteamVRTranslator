using System.Diagnostics;
using System.Threading.Channels;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Subtitles;

internal interface ISubtitleAudioCapture : IAsyncDisposable
{
    event EventHandler<ProcessLoopbackAudioSegment>? SegmentReady;
    Task Completion { get; }
    Task StartAsync(CancellationToken cancellationToken);
    void SetDiagnosticTag(string tag) { }
}

/// <summary>Owns one generation of capture, queue and processing until all have stopped.</summary>
internal sealed class SubtitleLiveSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly Channel<QueuedLiveSegment> _segments;
    private readonly SubtitleTrace _trace;
    private long _received, _dropped, _processed, _abandoned, _canceled, _failed;
    private SubtitleListeningState? _lastState;
    private uint _lastProcess;
    private readonly Func<uint> _currentProcess;
    private readonly Func<uint, ISubtitleAudioCapture> _createCapture;
    private readonly Func<CancellationToken, Task> _prepare;
    private readonly Func<QueuedLiveSegment, CancellationToken, Task> _process;
    private readonly Action<SubtitleListeningState, string, uint> _stateChanged;
    private readonly AppLog _log;
    private readonly Func<ValueTask> _cleanup;
    private readonly TimeSpan _pollInterval;
    private readonly object _sync = new();
    private Task? _disposal;
    private bool _active = true;

    public SubtitleLiveSession(Func<uint> currentProcess, Func<uint, ISubtitleAudioCapture> createCapture,
        Func<CancellationToken, Task> prepare, Func<QueuedLiveSegment, CancellationToken, Task> process,
        Action<SubtitleListeningState, string, uint> stateChanged, AppLog log, CancellationToken token,
        TimeSpan? pollInterval = null, Func<ValueTask>? cleanup = null, SubtitleTrace? trace = null)
    {
        _currentProcess = currentProcess;
        _createCapture = createCapture;
        _prepare = prepare;
        _process = process;
        _stateChanged = stateChanged;
        _log = log;
        _trace = trace ?? new SubtitleTrace(log, "live");
        _segments = Channel.CreateBounded<QueuedLiveSegment>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            dropped =>
            {
                Interlocked.Increment(ref _dropped);
                _log.Warning($"{dropped.Tag} stage=queue status=dropped reason=capacity capacity=4 waitMs={Stopwatch.GetElapsedTime(dropped.EnqueuedAt).TotalMilliseconds:F0}");
            });
        _cleanup = cleanup ?? (() => ValueTask.CompletedTask);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public bool IsActive => Volatile.Read(ref _active);
    public Task Completion { get; private set; } = Task.CompletedTask;
    public void Start() => Completion = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var token = _cancellation.Token;
        Task? monitor = null, processor = null;
        Exception? failure = null;
        var timer = Stopwatch.StartNew();
        _trace.Info("stage=session status=start mode=live queueCapacity=4 overflow=drop-oldest");
        try
        {
            ReportState(SubtitleListeningState.Starting, "正在准备字幕识别引擎...", 0);
            await _prepare(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            monitor = MonitorAsync(token);
            processor = ProcessAsync(token);
            var finished = await Task.WhenAny(monitor, processor).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("字幕后台任务意外退出，请重新开始监听。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _trace.Info("stage=session status=canceled"); }
        catch (Exception exception) { failure = exception; _log.Error($"{_trace.Tag} 实时字幕会话失败。", exception); }
        finally
        {
            _cancellation.Cancel();
            _segments.Writer.TryComplete();
            foreach (var task in new[] { monitor, processor })
            {
                if (task is null) continue;
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception exception) { failure ??= exception; }
            }
            while (_segments.Reader.TryRead(out var pending))
            {
                _abandoned++;
                _log.Info($"{pending.Tag} stage=queue status=abandoned reason=session-ended");
            }
            try { await SubtitleTrace.MeasureAsync(_log, _trace.Tag, "cleanup", () => _cleanup().AsTask()).ConfigureAwait(false); }
            catch (Exception exception) { failure ??= exception; }
            Volatile.Write(ref _active, false);
            _trace.Info($"stage=session status={(failure is null ? "stopped" : "failed")} elapsedMs={timer.Elapsed.TotalMilliseconds:F0} received={_received} processed={_processed} dropped={_dropped} abandoned={_abandoned} canceled={_canceled} failed={_failed}");
            ReportState(failure is null ? SubtitleListeningState.Stopped : SubtitleListeningState.Error,
                failure?.Message ?? "实时字幕监听已停止。", 0);
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        ISubtitleAudioCapture? capture = null;
        uint capturedProcess = 0;
        var captureNumber = 0;
        string? captureTag = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (capture?.Completion.IsCompleted == true)
                    {
                        await capture.Completion.ConfigureAwait(false);
                        throw new IOException("进程音频捕获已意外停止。");
                    }
                    var process = _currentProcess();
                    if (process != capturedProcess || capture is null)
                    {
                        await StopCaptureAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (process == 0)
                            ReportState(SubtitleListeningState.WaitingForProcess, "正在等待 SteamVR 场景进程音频...", 0);
                        else
                        {
                            ReportState(SubtitleListeningState.Starting, $"正在连接场景进程音频 (PID {process})...", process);
                            var tag = $"{_trace.Tag} [capture={++captureNumber}] pid={process}";
                            captureTag = tag;
                            _log.Info($"{tag} stage=capture-connect status=start");
                            var next = _createCapture(process);
                            next.SetDiagnosticTag(tag);
                            var baseTime = stopwatch.Elapsed;
                            next.SegmentReady += (_, segment) =>
                            {
                                var number = Interlocked.Increment(ref _received);
                                var segmentTag = $"{tag} [segment={number:D6}]";
                                _log.Info($"{segmentTag} stage=segment status=ready reason={segment.CompletionReason} startMs={segment.Start.TotalMilliseconds:F0} endMs={segment.End.TotalMilliseconds:F0} bytes={segment.PcmBytes.Length}");
                                if (token.IsCancellationRequested)
                                {
                                    Interlocked.Increment(ref _abandoned);
                                    _log.Info($"{segmentTag} stage=queue status=abandoned reason=session-ended");
                                    return;
                                }
                                var queued = new QueuedLiveSegment(segment, baseTime, segmentTag, Stopwatch.GetTimestamp());
                                _log.Info($"{segmentTag} stage=queue status=enqueue");
                                if (!_segments.Writer.TryWrite(queued))
                                {
                                    Interlocked.Increment(ref _abandoned);
                                    _log.Info($"{segmentTag} stage=queue status=abandoned reason=queue-closed");
                                }
                            };
                            // The monitor owns even a partially activated capture. Stop never releases
                            // its COM objects concurrently with activation or packet reads.
                            capture = next;
                            await next.StartAsync(token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            capturedProcess = process;
                            _log.Info($"{tag} stage=capture-connect status=complete elapsedMs={(stopwatch.Elapsed - baseTime).TotalMilliseconds:F0}");
                            ReportState(SubtitleListeningState.Listening, $"正在监听场景进程音频 (PID {process})", process);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    _log.Error($"{captureTag ?? _trace.Tag} stage=capture-connect status=failed retryMs={_pollInterval.TotalMilliseconds:F0} 进程音频连接中断，将自动重试。", exception);
                    ReportState(SubtitleListeningState.Error, exception.Message, capturedProcess);
                    await StopCaptureAsync().ConfigureAwait(false);
                }
                await Task.Delay(_pollInterval, token).ConfigureAwait(false);
            }
        }
        finally { await StopCaptureAsync().ConfigureAwait(false); }

        async Task StopCaptureAsync()
        {
            var old = capture;
            capture = null;
            capturedProcess = 0;
            if (old is not null)
                await SubtitleTrace.MeasureAsync(_log, captureTag ?? _trace.Tag, "capture-dispose", () => old.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(CancellationToken token)
    {
        await foreach (var segment in _segments.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (token.IsCancellationRequested)
            {
                Interlocked.Increment(ref _abandoned);
                _log.Info($"{segment.Tag} stage=queue status=abandoned reason=session-ended");
                token.ThrowIfCancellationRequested();
            }
            _log.Info($"{segment.Tag} stage=queue status=dequeue waitMs={Stopwatch.GetElapsedTime(segment.EnqueuedAt).TotalMilliseconds:F0} pending={_segments.Reader.Count}");
            try
            {
                await SubtitleTrace.MeasureAsync(_log, segment.Tag, "segment-process", () => _process(segment, token)).ConfigureAwait(false);
                _processed++;
            }
            catch (OperationCanceledException) { _canceled++; throw; }
            catch { _failed++; throw; }
        }
    }

    private void ReportState(SubtitleListeningState state, string message, uint process)
    {
        if (_lastState != state || _lastProcess != process)
            _trace.Info($"stage=state from={_lastState?.ToString() ?? "Created"} to={state} pid={process}");
        _lastState = state; _lastProcess = process;
        _stateChanged(state, message, process);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            return new ValueTask(_disposal ??= StopAsync());
        }
    }

    private async Task StopAsync()
    {
        _trace.Info("stage=session status=stop-requested");
        _cancellation.Cancel();
        await Completion.ConfigureAwait(false);
        _cancellation.Dispose();
    }
}
