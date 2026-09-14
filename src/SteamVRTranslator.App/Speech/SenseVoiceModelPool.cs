using System.Diagnostics;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Speech;

internal enum SenseVoiceRequestPriority { Interactive, Subtitle }

internal interface ISenseVoiceWorker : IDisposable
{
    int ProcessId { get; }
    Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken);
    string TakeDiagnostics();
}

// Only settings passed to the native process identify a loaded model. UI settings,
// microphone selection and GPU display names must not create additional instances.
internal sealed record SenseVoiceModelKey(
    string Executable, string Model, string? Vad, string Backend, int? Device, string Language)
{
    public static SenseVoiceModelKey From(SpeechConfiguration configuration)
    {
        var backend = configuration.SenseVoiceBackend.ToLowerInvariant();
        if (backend is not ("cpu" or "vulkan"))
            throw new InvalidOperationException($"不支持的 SenseVoice 引擎：{configuration.SenseVoiceBackend}。");
        return new(
            Normalize(backend == "vulkan" ? configuration.SenseVoiceVulkanExecutablePath : configuration.SenseVoiceExecutablePath),
            Normalize(configuration.SenseVoiceModelPath),
            string.IsNullOrWhiteSpace(configuration.SenseVoiceVadModelPath) ? null : Normalize(configuration.SenseVoiceVadModelPath),
            backend, backend == "vulkan" ? configuration.SenseVoiceVulkanDeviceIndex : null,
            configuration.EffectiveRecognitionLanguage.ToLowerInvariant());
    }

    public SpeechConfiguration ToConfiguration() => new()
    {
        SenseVoiceExecutablePath = Executable, SenseVoiceVulkanExecutablePath = Executable,
        SenseVoiceModelPath = Model, SenseVoiceVadModelPath = Vad, SenseVoiceBackend = Backend,
        SenseVoiceVulkanDeviceIndex = Device, EffectiveRecognitionLanguage = Language
    };

    private static string Normalize(string path) => Path.GetFullPath(path, AppContext.BaseDirectory).ToUpperInvariant();
}

/// <summary>One resident process per native configuration, retained while any client holds a lease.</summary>
internal sealed class SenseVoiceModelPool : IAsyncDisposable
{
    public static SenseVoiceModelPool Shared { get; } = new(SenseVoiceCommandTranscriber.CreateWorker);
    private readonly object _sync = new();
    private readonly Dictionary<SenseVoiceModelKey, Entry> _entries = [];
    private readonly Func<SpeechConfiguration, AppLog, CancellationToken, ISenseVoiceWorker> _create;
    private readonly TimeSpan _requestTimeout;
    private bool _disposed;

    internal SenseVoiceModelPool(Func<SpeechConfiguration, AppLog, CancellationToken, ISenseVoiceWorker> create,
        TimeSpan? requestTimeout = null)
    {
        _create = create;
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(2);
    }

    public async Task<Lease> AcquireAsync(SpeechConfiguration configuration, AppLog log, CancellationToken token)
    {
        var key = SenseVoiceModelKey.From(configuration);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Entry entry;
            Task? closing;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new Entry(new SharedModel(() => key.ToConfiguration(), _create, log, _requestTimeout));
                    _entries.Add(key, entry);
                }
                closing = entry.Closing;
                if (closing is null) entry.References++;
            }
            if (closing is not null)
            {
                // Never start another copy while the last instance is still shutting down.
                await closing.WaitAsync(token).ConfigureAwait(false);
                continue;
            }
            try
            {
                await entry.Model.Ready.WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                log.Info("[asr] 已连接共享 SenseVoice 模型。");
                return new Lease(this, key, entry);
            }
            catch
            {
                await ReleaseAsync(key, entry).ConfigureAwait(false);
                throw;
            }
        }
    }

    private Task ReleaseAsync(SenseVoiceModelKey key, Entry entry)
    {
        lock (_sync)
        {
            if (--entry.References > 0) return Task.CompletedTask;
            return entry.Closing ??= RetireAsync(key, entry);
        }
    }

    private Task RetireAsync(SenseVoiceModelKey key, Entry entry) => Task.Run(async () =>
    {
        await entry.Model.StopAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(key);
        }
    });

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            return new(Task.WhenAll(_entries.Select(pair =>
                pair.Value.Closing ??= RetireAsync(pair.Key, pair.Value)).ToArray()));
        }
    }

    internal sealed class Entry(SharedModel model)
    {
        public SharedModel Model { get; } = model;
        public int References { get; set; }
        public Task? Closing { get; set; }
    }

    internal sealed class Lease(SenseVoiceModelPool pool, SenseVoiceModelKey key, Entry entry) : IDisposable, IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _lifetime = new();
        private Task? _disposal;
        internal int QueuedRequests => entry.Model.QueuedRequests;

        public async Task<CommandTranscriptionResult> TranscribeAsync(CommandAudioInput audio,
            string tag, SenseVoiceRequestPriority priority, CancellationToken token)
        {
            CancellationTokenSource linked;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposal is not null, this);
                linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            }
            using (linked)
                return await entry.Model.TranscribeAsync(audio, tag, priority, linked.Token).ConfigureAwait(false);
        }

        public void Dispose() => _ = DisposeAsync();

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposal is null)
                {
                    _lifetime.Cancel();
                    _disposal = pool.ReleaseAsync(key, entry);
                    _lifetime.Dispose();
                }
                return new(_disposal);
            }
        }
    }

    internal sealed class SharedModel
    {
        private readonly object _sync = new();
        private readonly LinkedList<Request> _interactive = [], _subtitles = [];
        private readonly SemaphoreSlim _available = new(0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<SpeechConfiguration> _configuration;
        private readonly Func<SpeechConfiguration, AppLog, CancellationToken, ISenseVoiceWorker> _create;
        private readonly AppLog _log;
        private readonly TimeSpan _timeout;
        private readonly Task _run;
        private ISenseVoiceWorker? _worker;
        private Task? _disposal;

        public SharedModel(Func<SpeechConfiguration> configuration,
            Func<SpeechConfiguration, AppLog, CancellationToken, ISenseVoiceWorker> create, AppLog log, TimeSpan timeout)
        {
            _configuration = configuration; _create = create; _log = log; _timeout = timeout;
            _run = Task.Run(RunAsync);
        }

        public Task Ready => _ready.Task;
        internal int QueuedRequests { get { lock (_sync) return _interactive.Count + _subtitles.Count; } }

        public async Task<CommandTranscriptionResult> TranscribeAsync(CommandAudioInput audio,
            string tag, SenseVoiceRequestPriority priority, CancellationToken token)
        {
            // Callers delete their recording as soon as cancellation completes. The model
            // must own a copy until it has consumed the response to an in-flight request.
            var path = ApplicationDataPaths.CreateTemporaryFilePath("sensevoice-shared", ".wav");
            Request? request = null;
            try
            {
                await using (var source = File.OpenRead(audio.FilePath))
                await using (var destination = File.Create(path))
                    await source.CopyToAsync(destination, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                request = new(path, tag, audio.Duration, token);
                lock (_sync)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    request.Node = (priority == SenseVoiceRequestPriority.Interactive ? _interactive : _subtitles).AddLast(request);
                }
                _available.Release();
            }
            catch { DeleteAudio(path); throw; }
            using var registration = token.Register(() => Cancel(request));
            return await request.Completion.Task.ConfigureAwait(false);
        }

        private void Cancel(Request request)
        {
            var queued = false;
            lock (_sync)
            {
                if (request.Node?.List is { } list)
                {
                    list.Remove(request.Node);
                    request.Node = null;
                    queued = true;
                }
                request.Completion.TrySetCanceled(request.Token);
            }
            if (queued) DeleteAudio(request.Path);
        }

        private async Task RunAsync()
        {
            try
            {
                _stop.Token.ThrowIfCancellationRequested();
                _worker = _create(_configuration(), _log, _stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                _ready.TrySetResult();
                while (true)
                {
                    await _available.WaitAsync(_stop.Token).ConfigureAwait(false);
                    Request? request;
                    lock (_sync)
                    {
                        var list = _interactive.Count > 0 ? _interactive : _subtitles;
                        request = list.First?.Value;
                        if (request is not null) { list.RemoveFirst(); request.Node = null; }
                    }
                    if (request is null) continue;
                    try
                    {
                        if (!request.Token.IsCancellationRequested)
                            await ProcessAsync(request).ConfigureAwait(false);
                        else request.Completion.TrySetCanceled(request.Token);
                    }
                    finally { DeleteAudio(request.Path); }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _ready.TrySetCanceled(_stop.Token); }
            catch (Exception exception)
            {
                _ready.TrySetException(exception);
                _log.Error("[asr] 共享 SenseVoice 模型启动失败。", exception);
            }
            finally
            {
                lock (_sync)
                {
                    foreach (var request in _interactive.Concat(_subtitles))
                    {
                        request.Node = null;
                        request.Completion.TrySetCanceled();
                        DeleteAudio(request.Path);
                    }
                    _interactive.Clear(); _subtitles.Clear();
                }
                ResetWorker();
            }
        }

        private async Task ProcessAsync(Request request)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _worker ??= _create(_configuration(), _log, _stop.Token);
                operation.CancelAfter(_timeout);
                _log.Info($"{request.Tag} [asr] 共享模型开始识别：PID={_worker.ProcessId}，音频={request.Duration.TotalMilliseconds:F0} ms。");
                var text = await _worker.TranscribeAsync(request.Path, operation.Token).ConfigureAwait(false);
                var diagnostics = _worker.TakeDiagnostics();
                if (!string.IsNullOrWhiteSpace(diagnostics)) _log.Info($"{request.Tag} [asr] worker诊断：{diagnostics}");
                request.Completion.TrySetResult(new(text, stopwatch.Elapsed));
                _log.Info($"{request.Tag} [asr] 识别完成：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms，字符={text.Length}。");
            }
            catch (NoSpeechRecognizedException exception)
            {
                request.Completion.TrySetException(exception);
                _log.Info($"{request.Tag} [asr] 当前音频未识别到语音。");
            }
            catch (Exception exception)
            {
                ResetWorker();
                if (_stop.IsCancellationRequested) request.Completion.TrySetCanceled(_stop.Token);
                else
                {
                    var failure = operation.IsCancellationRequested
                        ? new TimeoutException("SenseVoice 识别超时，下次请求将重新加载模型。", exception) : exception;
                    request.Completion.TrySetException(failure);
                    _log.Error($"{request.Tag} [asr] 共享模型识别失败，下次请求将重建进程。", failure);
                }
            }
        }

        public Task StopAsync()
        {
            lock (_sync) return _disposal ??= StopCoreAsync();
        }

        private async Task StopCoreAsync()
        {
            _stop.Cancel();
            await _run.ConfigureAwait(false);
            _log.Info("[asr] 已释放共享 SenseVoice 模型：所有使用者均已退出。");
            // Keep the token source readable for a request that was copying audio when
            // the last lease closed. It will observe cancellation before enqueueing.
        }

        private void ResetWorker()
        {
            var worker = _worker; _worker = null;
            try { worker?.Dispose(); }
            catch (Exception exception) { _log.Error("[asr] 释放共享模型进程失败。", exception); }
        }

        private void DeleteAudio(string path)
        {
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { _log.Warning($"[asr] 清理共享识别音频失败：{exception.Message}"); }
        }

        private sealed class Request(string path, string tag, TimeSpan duration, CancellationToken token)
        {
            public string Path { get; } = path;
            public string Tag { get; } = tag;
            public TimeSpan Duration { get; } = duration;
            public CancellationToken Token { get; } = token;
            public LinkedListNode<Request>? Node { get; set; }
            public TaskCompletionSource<CommandTranscriptionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
