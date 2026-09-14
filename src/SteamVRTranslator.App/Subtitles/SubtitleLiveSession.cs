using System.Diagnostics;
using System.Threading.Channels;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Subtitles;

internal interface ISubtitleAudioCapture : IAsyncDisposable
{
    event EventHandler<ProcessLoopbackAudioSegment>? SegmentReady;
    Task Completion { get; }
    Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one generation of capture, queue and processing until all have stopped.</summary>
internal sealed class SubtitleLiveSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly Channel<QueuedLiveSegment> _segments = Channel.CreateBounded<QueuedLiveSegment>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
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
        TimeSpan? pollInterval = null, Func<ValueTask>? cleanup = null)
    {
        _currentProcess = currentProcess;
        _createCapture = createCapture;
        _prepare = prepare;
        _process = process;
        _stateChanged = stateChanged;
        _log = log;
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
        try
        {
            _stateChanged(SubtitleListeningState.Starting, "正在准备字幕识别引擎...", 0);
            await _prepare(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            monitor = MonitorAsync(token);
            processor = ProcessAsync(token);
            var finished = await Task.WhenAny(monitor, processor).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("字幕后台任务意外退出，请重新开始监听。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; _log.Error("[subtitles] 实时字幕会话失败。", exception); }
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
            try { await _cleanup().ConfigureAwait(false); }
            catch (Exception exception) { failure ??= exception; _log.Error("[subtitles] 释放字幕识别引擎失败。", exception); }
            Volatile.Write(ref _active, false);
            _stateChanged(failure is null ? SubtitleListeningState.Stopped : SubtitleListeningState.Error,
                failure?.Message ?? "实时字幕监听已停止。", 0);
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        ISubtitleAudioCapture? capture = null;
        uint capturedProcess = 0;
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
                            _stateChanged(SubtitleListeningState.WaitingForProcess, "正在等待 SteamVR 场景进程音频...", 0);
                        else
                        {
                            _stateChanged(SubtitleListeningState.Starting, $"正在连接场景进程音频 (PID {process})...", process);
                            var next = _createCapture(process);
                            var baseTime = stopwatch.Elapsed;
                            next.SegmentReady += (_, segment) =>
                            {
                                if (!token.IsCancellationRequested)
                                    _segments.Writer.TryWrite(new QueuedLiveSegment(segment, baseTime));
                            };
                            // The monitor owns even a partially activated capture. Stop never releases
                            // its COM objects concurrently with activation or packet reads.
                            capture = next;
                            await next.StartAsync(token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            capturedProcess = process;
                            _stateChanged(SubtitleListeningState.Listening, $"正在监听场景进程音频 (PID {process})", process);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    _log.Error("[subtitles] 进程音频连接中断，将自动重试。", exception);
                    _stateChanged(SubtitleListeningState.Error, exception.Message, capturedProcess);
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
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(CancellationToken token)
    {
        await foreach (var segment in _segments.Reader.ReadAllAsync(token).ConfigureAwait(false))
            await _process(segment, token).ConfigureAwait(false);
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
        _cancellation.Cancel();
        await Completion.ConfigureAwait(false);
        _cancellation.Dispose();
    }
}
