using System.Diagnostics;
using System.Threading.Channels;
using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Speech;
using SteamVRTranslator.App.Translation;

namespace SteamVRTranslator.App.Subtitles;

public sealed class SubtitleSessionController : IDisposable
{
    private AppConfiguration _configuration;
    private readonly AppLog _log;
    private readonly HttpClient _httpClient;
    private readonly SubtitleAudioProcessor _audioProcessor;
    private readonly ProviderConcurrencyLimiter _providerConcurrency = new();
    private readonly SpeakerIdentityRegistry _speakerRegistry = new();
    private readonly Dictionary<string, int> _vibeVoiceSpeakers =
        new(StringComparer.OrdinalIgnoreCase);
    private SenseVoiceCommandTranscriber? _transcriber;
    private VibeVoiceApiTranscriber? _vibeVoiceTranscriber;
    private CancellationTokenSource? _listeningCancellation;
    private Task? _captureMonitorTask;
    private Task? _segmentProcessorTask;
    private ProcessLoopbackAudioCapture? _liveCapture;
    private Channel<QueuedLiveSegment>? _liveSegments;
    private Stopwatch? _liveStopwatch;
    private uint _capturedProcessId;
    private SubtitleListeningState _listeningState = SubtitleListeningState.Stopped;
    private bool _disposed;

    public SubtitleSessionController(
        AppConfiguration configuration,
        AppLog log,
        HttpClient httpClient)
    {
        _configuration = configuration;
        _log = log;
        _httpClient = httpClient;
        _audioProcessor = new SubtitleAudioProcessor(log);
        History = new SubtitleHistoryViewModel(configuration.Subtitles);
    }

    public SubtitleHistoryViewModel History { get; }

    public bool IsListening => _listeningCancellation is not null;

    public SubtitleListeningState ListeningState => _listeningState;

    public event EventHandler<SubtitleListeningStateChangedEventArgs>? ListeningStateChanged;

    public void ApplyConfiguration(AppConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _configuration = configuration;
        History.ApplyConfiguration(configuration.Subtitles);
        _transcriber?.Dispose();
        _transcriber = null;
        _vibeVoiceTranscriber?.Dispose();
        _vibeVoiceTranscriber = null;
    }

    public Task StartListeningAsync(
        Func<uint> currentSceneProcessId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(currentSceneProcessId);
        if (IsListening)
        {
            return Task.CompletedTask;
        }
        if (!ProcessLoopbackAudioCapture.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "进程音频字幕需要 Windows 10 build 20348 或更高版本。");
        }

        _speakerRegistry.Reset();
        _vibeVoiceSpeakers.Clear();
        _liveStopwatch = Stopwatch.StartNew();
        _liveSegments = Channel.CreateBounded<QueuedLiveSegment>(
            new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        _listeningCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetListeningState(SubtitleListeningState.WaitingForProcess, "正在等待 SteamVR 场景进程音频...");
        _segmentProcessorTask = ProcessLiveSegmentsAsync(
            _liveSegments.Reader,
            _listeningCancellation.Token);
        _captureMonitorTask = MonitorSceneProcessAsync(
            currentSceneProcessId,
            _listeningCancellation.Token);
        _log.Info("[subtitles] 实时字幕监听会话已启动，等待 SteamVR 当前场景进程。");
        return Task.CompletedTask;
    }

    public async Task StopListeningAsync()
    {
        var cancellation = _listeningCancellation;
        if (cancellation is null)
        {
            return;
        }

        _listeningCancellation = null;
        cancellation.Cancel();
        await StopCaptureAsync();
        _liveSegments?.Writer.TryComplete();
        await AwaitStoppedTaskAsync(_captureMonitorTask, "进程音频监听");
        await AwaitStoppedTaskAsync(_segmentProcessorTask, "实时字幕分段处理");
        _captureMonitorTask = null;
        _segmentProcessorTask = null;
        _liveSegments = null;
        _liveStopwatch?.Stop();
        _liveStopwatch = null;
        _capturedProcessId = 0;
        cancellation.Dispose();
        SetListeningState(SubtitleListeningState.Stopped, "实时字幕监听已停止。");
        _log.Info("[subtitles] 实时字幕监听会话已停止。");
    }

    public async Task<SubtitleReplayResult> ReplayFileAsync(
        string audioPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stopwatch = Stopwatch.StartNew();
        _log.Info($"[subtitles] 文件重放开始：{audioPath}");
        if (string.Equals(
                _configuration.Subtitles.AsrBackend,
                SubtitleAsrBackends.VibeVoiceApi,
                StringComparison.OrdinalIgnoreCase))
        {
            return await ReplayVibeVoiceFileAsync(
                audioPath,
                stopwatch,
                progress,
                cancellationToken);
        }

        var document = await _audioProcessor.AnalyzeAsync(
            audioPath,
            _configuration.Subtitles.Diarization,
            _speakerRegistry,
            cancellationToken);
        var transcriber = _transcriber ??= new SenseVoiceCommandTranscriber(
            _configuration.Speech,
            _log);
        var translationTasks = new List<Task>();
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
                var entry = History.Add(
                    segment.Speaker,
                    TimeSpan.FromSeconds(segment.StartSeconds),
                    TimeSpan.FromSeconds(segment.EndSeconds),
                    _configuration.Subtitles.ShowOriginalText ? sourceText : string.Empty);
                if (_configuration.Subtitles.TranslateText)
                {
                    translationTasks.Add(TranslateEntryAsync(entry, sourceText, cancellationToken));
                }
                else
                {
                    entry.IsTranslating = false;
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopListeningAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _log.Error("[subtitles] 释放实时字幕监听会话失败。", exception);
        }
        _transcriber?.Dispose();
        _transcriber = null;
        _vibeVoiceTranscriber?.Dispose();
        _vibeVoiceTranscriber = null;
    }

    private async Task MonitorSceneProcessAsync(
        Func<uint> currentSceneProcessId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var processId = currentSceneProcessId();
                if (processId == 0)
                {
                    if (_capturedProcessId != 0)
                    {
                        await StopCaptureAsync();
                        _capturedProcessId = 0;
                    }
                    SetListeningState(
                        SubtitleListeningState.WaitingForProcess,
                        "正在等待 SteamVR 场景进程音频...");
                }
                else if (processId != _capturedProcessId)
                {
                    await StopCaptureAsync();
                    SetListeningState(
                        SubtitleListeningState.Starting,
                        $"正在连接场景进程音频 (PID {processId})...");
                    var capture = new ProcessLoopbackAudioCapture(processId, _log);
                    try
                    {
                        var baseTime = _liveStopwatch?.Elapsed ?? TimeSpan.Zero;
                        capture.SegmentReady += (_, segment) => QueueLiveSegment(segment, baseTime);
                        await capture.StartAsync(cancellationToken);
                        _liveCapture = capture;
                    }
                    catch
                    {
                        await capture.DisposeAsync();
                        throw;
                    }
                    _capturedProcessId = processId;
                    SetListeningState(
                        SubtitleListeningState.Listening,
                        $"正在监听场景进程音频 (PID {processId})");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.Error("[subtitles] 连接场景进程音频失败，将自动重试。", exception);
                SetListeningState(SubtitleListeningState.Error, exception.Message);
                await StopCaptureAsync();
                _capturedProcessId = 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private void QueueLiveSegment(ProcessLoopbackAudioSegment segment, TimeSpan baseTime)
    {
        var channel = _liveSegments;
        if (channel is null || !channel.Writer.TryWrite(new QueuedLiveSegment(segment, baseTime)))
        {
            _log.Warning("[subtitles] 实时字幕处理队列已满，已丢弃最旧音频分段。");
        }
    }

    private async Task ProcessLiveSegmentsAsync(
        ChannelReader<QueuedLiveSegment> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var queued in reader.ReadAllAsync(cancellationToken))
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "SteamVRTranslator",
                "subtitles",
                $"segment-{Guid.NewGuid():N}.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var writer = new WaveFileWriter(path, queued.Segment.Format))
            {
                writer.Write(queued.Segment.PcmBytes, 0, queued.Segment.PcmBytes.Length);
            }

            try
            {
                await ProcessLiveFileAsync(path, queued, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.Error("[subtitles] 实时音频分段识别失败。", exception);
                SetListeningState(SubtitleListeningState.Error, exception.Message);
            }
            finally
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private async Task ProcessLiveFileAsync(
        string audioPath,
        QueuedLiveSegment queued,
        CancellationToken cancellationToken)
    {
        var baseTime = queued.BaseTime;
        if (string.Equals(
                _configuration.Subtitles.AsrBackend,
                SubtitleAsrBackends.VibeVoiceApi,
                StringComparison.OrdinalIgnoreCase))
        {
            var transcriber = _vibeVoiceTranscriber ??= new VibeVoiceApiTranscriber(
                _configuration.Subtitles,
                _log);
            var result = await transcriber.TranscribeAsync(
                audioPath,
                _configuration.Speech.EffectiveRecognitionLanguage,
                "[subtitles:live:vibevoice]",
                cancellationToken);
            foreach (var segment in result.Segments)
            {
                await AddLiveEntryAsync(
                    VibeVoiceSpeakerId(segment.Speaker),
                    baseTime + queued.Segment.Start + TimeSpan.FromSeconds(segment.StartSeconds),
                    baseTime + queued.Segment.Start + TimeSpan.FromSeconds(segment.EndSeconds),
                    segment.Text,
                    cancellationToken);
            }
            return;
        }

        var document = await _audioProcessor.AnalyzeAsync(
            audioPath,
            _configuration.Subtitles.Diarization,
            _speakerRegistry,
            cancellationToken);
        var transcriberLocal = _transcriber ??= new SenseVoiceCommandTranscriber(
            _configuration.Speech,
            _log);
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
                segment.Speaker,
                baseTime + queued.Segment.Start + TimeSpan.FromSeconds(segment.StartSeconds),
                baseTime + queued.Segment.Start + TimeSpan.FromSeconds(segment.EndSeconds),
                transcription.Text,
                cancellationToken);
        }
    }

    private async Task AddLiveEntryAsync(
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

        var entry = History.Add(
            speaker,
            start,
            end < start ? start : end,
            _configuration.Subtitles.ShowOriginalText ? sourceText : string.Empty);
        if (_configuration.Subtitles.TranslateText)
        {
            await TranslateEntryAsync(entry, sourceText, cancellationToken);
        }
        else
        {
            entry.IsTranslating = false;
        }
    }

    private async Task StopCaptureAsync()
    {
        var capture = _liveCapture;
        _liveCapture = null;
        if (capture is not null)
        {
            await capture.DisposeAsync();
        }
    }

    private async Task AwaitStoppedTaskAsync(Task? task, string description)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log.Error($"[subtitles] 停止{description}失败。", exception);
        }
    }

    private void SetListeningState(SubtitleListeningState state, string message)
    {
        if (_listeningState == state && state != SubtitleListeningState.Error)
        {
            return;
        }
        _listeningState = state;
        ListeningStateChanged?.Invoke(
            this,
            new SubtitleListeningStateChangedEventArgs(state, message, _capturedProcessId));
    }

    private async Task<SubtitleReplayResult> ReplayVibeVoiceFileAsync(
        string audioPath,
        Stopwatch stopwatch,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        _vibeVoiceSpeakers.Clear();
        var transcriber = _vibeVoiceTranscriber ??= new VibeVoiceApiTranscriber(
            _configuration.Subtitles,
            _log);
        var result = await transcriber.TranscribeAsync(
            audioPath,
            _configuration.Speech.EffectiveRecognitionLanguage,
            "[subtitles:vibevoice]",
            cancellationToken);
        var translationTasks = new List<Task>();
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
            var entry = History.Add(
                VibeVoiceSpeakerId(segment.Speaker),
                TimeSpan.FromSeconds(segment.StartSeconds),
                TimeSpan.FromSeconds(Math.Max(segment.StartSeconds, segment.EndSeconds)),
                _configuration.Subtitles.ShowOriginalText ? sourceText : string.Empty);
            if (_configuration.Subtitles.TranslateText)
            {
                translationTasks.Add(TranslateEntryAsync(entry, sourceText, cancellationToken));
            }
            else
            {
                entry.IsTranslating = false;
            }
        }

        progress?.Report(1);
        await Task.WhenAll(translationTasks);
        stopwatch.Stop();
        _log.Info(
            $"[subtitles] VibeVoice 文件重放完成：总耗时={stopwatch.Elapsed.TotalSeconds:F2}s，" +
            $"说话人={_vibeVoiceSpeakers.Count}，有效字幕={recognized}。");
        return new SubtitleReplayResult(result.Segments.Count, recognized, stopwatch.Elapsed);
    }

    private int VibeVoiceSpeakerId(string speaker)
    {
        var normalized = string.IsNullOrWhiteSpace(speaker) ? "A" : speaker.Trim();
        if (_vibeVoiceSpeakers.TryGetValue(normalized, out var id))
        {
            return id;
        }

        id = _vibeVoiceSpeakers.Count;
        _vibeVoiceSpeakers[normalized] = id;
        return id;
    }

    private async Task TranslateEntryAsync(
        SubtitleHistoryEntry entry,
        string sourceText,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = _configuration.Translation.GetProviderFor(
                    PromptProviderPurpose.SubtitleTranslation)
                ?? throw new InvalidOperationException("字幕翻译没有可用的模型提供商。");
            using var lease = await _providerConcurrency.AcquireAsync(
                provider.Id,
                provider.MaxConcurrency,
                cancellationToken);
            ITranslationBackend backend = provider.IsMock
                ? new MockTranslationBackend()
                : new OpenAiCompatibleVisionBackend(
                    _configuration.Translation,
                    _httpClient,
                    providerId: provider.Id);
            var translated = await backend.TranslateTextAsync(
                sourceText,
                _configuration.Subtitles.TargetLanguage,
                _configuration.Subtitles.TranslationSystemPrompt,
                _configuration.Subtitles.TranslationPrompt,
                onPartialResult: null,
                cancellationToken);
            entry.TranslatedText = translated?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(entry.TranslatedText))
            {
                entry.Error = "翻译模型没有返回文本。";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            entry.Error = exception.Message;
            _log.Error($"[subtitles] 字幕翻译失败：说话人={entry.DisplaySpeaker}。", exception);
        }
        finally
        {
            entry.IsTranslating = false;
        }
    }
}

public enum SubtitleListeningState
{
    Stopped,
    WaitingForProcess,
    Starting,
    Listening,
    Error
}

public sealed class SubtitleListeningStateChangedEventArgs(
    SubtitleListeningState state,
    string message,
    uint processId) : EventArgs
{
    public SubtitleListeningState State { get; } = state;

    public string Message { get; } = message;

    public uint ProcessId { get; } = processId;
}

internal sealed record QueuedLiveSegment(
    ProcessLoopbackAudioSegment Segment,
    TimeSpan BaseTime);

public sealed record SubtitleReplayResult(
    int SegmentCount,
    int RecognizedCount,
    TimeSpan Elapsed);
