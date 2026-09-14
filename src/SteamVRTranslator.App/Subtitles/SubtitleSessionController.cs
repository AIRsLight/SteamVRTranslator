using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Speech;
using SteamVRTranslator.App.Translation;

namespace SteamVRTranslator.App.Subtitles;

public sealed class SubtitleSessionController : IAsyncDisposable
{
    private AppConfiguration _configuration;
    private readonly AppLog _log;
    private readonly HttpClient _httpClient;
    private readonly SubtitleAudioProcessor _audioProcessor;
    private readonly ProviderConcurrencyLimiter _providerConcurrency = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Dispatcher? _dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
    private readonly Func<uint, ISubtitleAudioCapture> _createCapture;
    private readonly Func<SpeechConfiguration, CancellationToken, ISubtitleLocalTranscriber> _createLocal;
    private readonly TimeSpan? _pollInterval;
    private readonly Func<bool> _isSupported;
    private SubtitleLiveSession? _live;
    private CancellationTokenSource? _replayCancellation;
    private TaskCompletionSource? _replayFinished;
    private Task? _disposal;
    private SubtitleListeningState _listeningState = SubtitleListeningState.Stopped;
    private uint _capturedProcessId;
    private long _stateVersion;
    private bool _disposed;

    public SubtitleSessionController(AppConfiguration configuration, AppLog log, HttpClient httpClient)
        : this(configuration, log, httpClient, id => new ProcessLoopbackAudioCapture(id, log),
            (speech, token) => new SenseVoiceCommandTranscriber(speech, log, token, SenseVoiceRequestPriority.Subtitle)) { }

    internal SubtitleSessionController(AppConfiguration configuration, AppLog log, HttpClient httpClient,
        Func<uint, ISubtitleAudioCapture> createCapture,
        Func<SpeechConfiguration, CancellationToken, ISubtitleLocalTranscriber> createLocal,
        Func<bool>? isSupported = null, TimeSpan? pollInterval = null, SubtitleAudioProcessor? audioProcessor = null)
    {
        _configuration = Snapshot(configuration);
        _log = log;
        _httpClient = httpClient;
        _createCapture = createCapture;
        _createLocal = createLocal;
        _isSupported = isSupported ?? (() => ProcessLoopbackAudioCapture.IsSupported);
        _pollInterval = pollInterval;
        _audioProcessor = audioProcessor ?? new SubtitleAudioProcessor(log);
        _audioProcessor.UpdateConfiguration(DiarizationConfiguration(_configuration));
        History = new SubtitleHistoryViewModel(configuration.Subtitles);
    }

    public SubtitleHistoryViewModel History { get; }
    public bool IsListening => _live?.IsActive == true;
    public SubtitleListeningState ListeningState { get { lock (_sync) return _listeningState; } }
    public long StateVersion { get { lock (_sync) return _stateVersion; } }
    public event EventHandler<SubtitleListeningStateChangedEventArgs>? ListeningStateChanged;

    public void ApplyConfiguration(AppConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Volatile.Write(ref _configuration, Snapshot(configuration));
        _audioProcessor.UpdateConfiguration(DiarizationConfiguration(_configuration));
        History.ApplyConfiguration(configuration.Subtitles);
        // Each processor applies the snapshot between segments, never during an ASR request.
    }

    public async Task StartListeningAsync(Func<uint> currentSceneProcessId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentSceneProcessId);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsListening) return;
            if (!_isSupported()) throw new PlatformNotSupportedException("进程音频字幕需要 Windows 10 build 20348 或更高版本。");
            if (_live is not null) await _live.DisposeAsync().ConfigureAwait(false);
            var context = new SubtitleProcessingContext(_createLocal, _log, _audioProcessor);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _live = new SubtitleLiveSession(currentSceneProcessId, _createCapture,
                token => PrepareAsync(context, token),
                (segment, token) => ProcessSegmentAsync(context, segment, token),
                SetListeningState, _log, linked.Token, _pollInterval,
                async () => { try { await context.DisposeAsync().ConfigureAwait(false); } finally { linked.Dispose(); } });
            _live.Start();
            _log.Info("[subtitles] 实时字幕会话启动中。准备识别引擎后连接场景进程。");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopListeningAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var live = _live;
            if (live is null) return;
            if (live.IsActive) SetListeningState(SubtitleListeningState.Stopping, "正在停止实时字幕...", 0);
            await live.DisposeAsync().ConfigureAwait(false);
            _live = null;
            SetListeningState(SubtitleListeningState.Stopped, "实时字幕监听已停止。", 0);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAllAsync()
    {
        Task? replay;
        lock (_sync) { _replayCancellation?.Cancel(); replay = _replayFinished?.Task; }
        await StopListeningAsync().ConfigureAwait(false);
        if (replay is not null) await replay.ConfigureAwait(false);
        await _audioProcessor.ReleaseIdleModelsAsync().ConfigureAwait(false);
    }

    public async Task<SubtitleReplayResult> ReplayFileAsync(string audioPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        CancellationTokenSource cancellation;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_replayFinished?.Task.IsCompleted == false) throw new InvalidOperationException("已有音频文件正在重放，请先取消。");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _replayCancellation = cancellation;
            _replayFinished = finished;
        }
        try
        {
            return await Task.Run(async () =>
            {
                await using var context = new SubtitleProcessingContext(_createLocal, _log, _audioProcessor);
                try
                {
                    await PrepareAsync(context, cancellation.Token).ConfigureAwait(false);
                    return await ReplayFileCoreAsync(context, audioPath, progress, cancellation.Token).ConfigureAwait(false);
                }
                finally { await Task.WhenAll(context.Translations).ConfigureAwait(false); }
            }).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _replayCancellation = null;
                cancellation.Dispose();
                finished.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        await StopAllAsync().ConfigureAwait(false);
        await _audioProcessor.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task PrepareAsync(SubtitleProcessingContext context, CancellationToken token)
    {
        await context.ApplyConfigurationAsync(Volatile.Read(ref _configuration)).ConfigureAwait(false);
        await context.PrepareAsync(token).ConfigureAwait(false);
    }

    private async Task ProcessSegmentAsync(SubtitleProcessingContext context, QueuedLiveSegment queued, CancellationToken token)
    {
        string? path = null;
        try
        {
            await PrepareAsync(context, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            path = ApplicationDataPaths.CreateTemporaryFilePath("subtitle-live", ".wav");
            using (var writer = new WaveFileWriter(path, queued.Segment.Format))
                writer.Write(queued.Segment.PcmBytes, 0, queued.Segment.PcmBytes.Length);
            await ProcessLiveFileAsync(context, path, queued, token).ConfigureAwait(false);
        }
        catch (NoSpeechRecognizedException) { _log.Info("[subtitles] 当前音频分段未检测到语音。继续监听。"); }
        finally
        {
            if (path is not null)
            {
                try { File.Delete(path); }
                catch (IOException exception) { _log.Warning($"[subtitles] 清理临时音频失败：{exception.Message}"); }
            }
        }
    }

    private Task<T> OnUiAsync<T>(Func<T> action, CancellationToken token)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }
        return _dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, token).Task;
    }

    private void SetListeningState(SubtitleListeningState state, string message, uint processId)
    {
        SubtitleListeningStateChangedEventArgs args;
        lock (_sync)
        {
            if (_listeningState == state && _capturedProcessId == processId && state != SubtitleListeningState.Error) return;
            _listeningState = state;
            _capturedProcessId = processId;
            args = new(state, message, processId, ++_stateVersion);
        }
        ListeningStateChanged?.Invoke(this, args);
    }

    private static AppConfiguration Snapshot(AppConfiguration configuration)
    {
        var copy = JsonSerializer.Deserialize<AppConfiguration>(JsonSerializer.Serialize(configuration))!;
        // Effective language and resolved prompts are runtime-only (JsonIgnore).
        copy.Speech.EffectiveRecognitionLanguage = configuration.Speech.EffectiveRecognitionLanguage;
        copy.Subtitles.TranslationSystemPrompt = configuration.Subtitles.TranslationSystemPrompt;
        copy.Subtitles.TranslationPrompt = configuration.Subtitles.TranslationPrompt;
        return copy;
    }

    private static SubtitleDiarizationConfiguration? DiarizationConfiguration(AppConfiguration configuration) =>
        configuration.Subtitles.AsrBackend == SubtitleAsrBackends.VibeVoiceApi ? null : configuration.Subtitles.Diarization;

    private async Task<SubtitleReplayResult> ReplayFileCoreAsync(
        SubtitleProcessingContext context,
        string audioPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _log.Info($"[subtitles] 文件重放开始：{audioPath}");
        if (string.Equals(
                context.Configuration.Subtitles.AsrBackend,
                SubtitleAsrBackends.VibeVoiceApi,
                StringComparison.OrdinalIgnoreCase))
        {
            return await ReplayVibeVoiceFileAsync(
                context,
                audioPath,
                stopwatch,
                progress,
                cancellationToken);
        }

        var document = await context.AnalyzeAudioAsync(audioPath, cancellationToken);
        var transcriber = context.Local!;
        var translationTasks = context.Translations;
        var recognized = 0;
        for (var index = 0; index < document.Segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = document.Segments[index];
            var path = SubtitleAudioProcessor.WriteSegmentToTemporaryWave(document, segment);
            using var audio = new CommandAudioInput(
                path,
                TimeSpan.FromSeconds(segment.EndSeconds - segment.StartSeconds));
            try
            {
                var transcription = await transcriber.TranscribeAsync(
                    audio,
                    $"[subtitles:{index + 1}/{document.Segments.Count}]",
                    cancellationToken);
                var sourceText = transcription.Text.Trim();
                if (string.IsNullOrWhiteSpace(sourceText))
                {
                    continue;
                }

                recognized++;
                var entry = await OnUiAsync(() => History.Add(
                    segment.Speaker,
                    TimeSpan.FromSeconds(segment.StartSeconds),
                    TimeSpan.FromSeconds(segment.EndSeconds),
                    context.Configuration.Subtitles.ShowOriginalText ? sourceText : string.Empty), cancellationToken).ConfigureAwait(false);
                if (context.Configuration.Subtitles.TranslateText)
                {
                    translationTasks.Add(TranslateEntryAsync(context, entry, sourceText, cancellationToken));
                }
                else
                {
                    await OnUiAsync(() => entry.IsTranslating = false, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.Error(
                    $"[subtitles] 分段 {index + 1}/{document.Segments.Count} 识别失败。",
                    exception);
            }

            progress?.Report((double)(index + 1) / document.Segments.Count);
        }

        await Task.WhenAll(translationTasks);
        stopwatch.Stop();
        _log.Info(
            $"[subtitles] 文件重放完成：总耗时={stopwatch.Elapsed.TotalSeconds:F2}s，" +
            $"分段={document.Segments.Count}，有效字幕={recognized}。");
        return new SubtitleReplayResult(document.Segments.Count, recognized, stopwatch.Elapsed);
    }

    private async Task ProcessLiveFileAsync(
        SubtitleProcessingContext context,
        string audioPath,
        QueuedLiveSegment queued,
        CancellationToken cancellationToken)
    {
        var baseTime = queued.BaseTime;
        if (string.Equals(
                context.Configuration.Subtitles.AsrBackend,
                SubtitleAsrBackends.VibeVoiceApi,
                StringComparison.OrdinalIgnoreCase))
        {
            var transcriber = context.Api!;
            var result = await transcriber.TranscribeAsync(
                audioPath,
                context.Configuration.Speech.EffectiveRecognitionLanguage,
                "[subtitles:live:vibevoice]",
                cancellationToken);
            foreach (var segment in result.Segments)
            {
                await AddLiveEntryAsync(
                    context,
                    VibeVoiceSpeakerId(context, segment.Speaker),
                    baseTime + queued.Segment.Start + TimeSpan.FromSeconds(segment.StartSeconds),
                    baseTime + queued.Segment.Start + TimeSpan.FromSeconds(segment.EndSeconds),
                    segment.Text,
                    cancellationToken);
            }
            return;
        }

        var document = await context.AnalyzeAudioAsync(audioPath, cancellationToken);
        var transcriberLocal = context.Local!;
        for (var index = 0; index < document.Segments.Count; index++)
        {
            var segment = document.Segments[index];
            var path = SubtitleAudioProcessor.WriteSegmentToTemporaryWave(document, segment);
            using var audio = new CommandAudioInput(
                path,
                TimeSpan.FromSeconds(segment.EndSeconds - segment.StartSeconds));
            var transcription = await transcriberLocal.TranscribeAsync(
                audio,
                $"[subtitles:live:{index + 1}/{document.Segments.Count}]",
                cancellationToken);
            await AddLiveEntryAsync(
                context,
                segment.Speaker,
                baseTime + queued.Segment.Start + TimeSpan.FromSeconds(segment.StartSeconds),
                baseTime + queued.Segment.Start + TimeSpan.FromSeconds(segment.EndSeconds),
                transcription.Text,
                cancellationToken);
        }
    }

    private async Task AddLiveEntryAsync(
        SubtitleProcessingContext context,
        int speaker,
        TimeSpan start,
        TimeSpan end,
        string text,
        CancellationToken cancellationToken)
    {
        var sourceText = text.Trim();
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return;
        }

        var entry = await OnUiAsync(() => History.Add(
            speaker,
            start,
            end < start ? start : end,
            context.Configuration.Subtitles.ShowOriginalText ? sourceText : string.Empty), cancellationToken).ConfigureAwait(false);
        if (context.Configuration.Subtitles.TranslateText)
        {
            await TranslateEntryAsync(context, entry, sourceText, cancellationToken);
        }
        else
        {
            await OnUiAsync(() => entry.IsTranslating = false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<SubtitleReplayResult> ReplayVibeVoiceFileAsync(
        SubtitleProcessingContext context,
        string audioPath,
        Stopwatch stopwatch,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        context.ApiSpeakers.Clear();
        var transcriber = context.Api!;
        var result = await transcriber.TranscribeAsync(
            audioPath,
            context.Configuration.Speech.EffectiveRecognitionLanguage,
            "[subtitles:vibevoice]",
            cancellationToken);
        var translationTasks = context.Translations;
        var recognized = 0;
        foreach (var segment in result.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceText = segment.Text.Trim();
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                continue;
            }

            recognized++;
            var entry = await OnUiAsync(() => History.Add(
                VibeVoiceSpeakerId(context, segment.Speaker),
                TimeSpan.FromSeconds(segment.StartSeconds),
                TimeSpan.FromSeconds(Math.Max(segment.StartSeconds, segment.EndSeconds)),
                context.Configuration.Subtitles.ShowOriginalText ? sourceText : string.Empty), cancellationToken).ConfigureAwait(false);
            if (context.Configuration.Subtitles.TranslateText)
            {
                translationTasks.Add(TranslateEntryAsync(context, entry, sourceText, cancellationToken));
            }
            else
            {
                await OnUiAsync(() => entry.IsTranslating = false, CancellationToken.None).ConfigureAwait(false);
            }
        }

        progress?.Report(1);
        await Task.WhenAll(translationTasks);
        stopwatch.Stop();
        _log.Info(
            $"[subtitles] VibeVoice 文件重放完成：总耗时={stopwatch.Elapsed.TotalSeconds:F2}s，" +
            $"说话人={context.ApiSpeakers.Count}，有效字幕={recognized}。");
        return new SubtitleReplayResult(result.Segments.Count, recognized, stopwatch.Elapsed);
    }

    private static int VibeVoiceSpeakerId(SubtitleProcessingContext context, string speaker)
    {
        var normalized = string.IsNullOrWhiteSpace(speaker) ? "A" : speaker.Trim();
        if (context.ApiSpeakers.TryGetValue(normalized, out var id))
        {
            return id;
        }

        id = context.ApiSpeakers.Count;
        context.ApiSpeakers[normalized] = id;
        return id;
    }

    private async Task TranslateEntryAsync(
        SubtitleProcessingContext context,
        SubtitleHistoryEntry entry,
        string sourceText,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = context.Configuration.Translation.GetProviderFor(
                    PromptProviderPurpose.SubtitleTranslation)
                ?? throw new InvalidOperationException("字幕翻译没有可用的模型提供商。");
            using var lease = await _providerConcurrency.AcquireAsync(
                provider.Id,
                provider.MaxConcurrency,
                cancellationToken);
            var backend = TranslationBackendFactory.Create(
                context.Configuration.Translation,
                provider,
                _httpClient,
                textTranslationPurpose: PromptProviderPurpose.SubtitleTranslation);
            var translated = await backend.TranslateTextAsync(
                sourceText,
                context.Configuration.Subtitles.TargetLanguage,
                context.Configuration.Subtitles.TranslationSystemPrompt,
                context.Configuration.Subtitles.TranslationPrompt,
                onPartialResult: null,
                cancellationToken);
            await OnUiAsync(() =>
            {
                entry.TranslatedText = translated?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(entry.TranslatedText)) entry.Error = "翻译模型没有返回文本。";
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await OnUiAsync(() => entry.Error = exception.Message, CancellationToken.None).ConfigureAwait(false);
            _log.Error($"[subtitles] 字幕翻译失败：说话人={entry.DisplaySpeaker}。", exception);
        }
        finally
        {
            await OnUiAsync(() => entry.IsTranslating = false, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

public enum SubtitleListeningState
{
    Stopped,
    WaitingForProcess,
    Starting,
    Stopping,
    Listening,
    Error
}

public sealed class SubtitleListeningStateChangedEventArgs(
    SubtitleListeningState state,
    string message,
    uint processId, long version = 0) : EventArgs
{
    public SubtitleListeningState State { get; } = state;

    public string Message { get; } = message;

    public uint ProcessId { get; } = processId;

    public long Version { get; } = version;
}

internal sealed record QueuedLiveSegment(
    ProcessLoopbackAudioSegment Segment,
    TimeSpan BaseTime);

public sealed record SubtitleReplayResult(
    int SegmentCount,
    int RecognizedCount,
    TimeSpan Elapsed);
