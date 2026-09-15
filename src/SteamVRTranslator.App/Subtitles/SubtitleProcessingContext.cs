using System.Text.Json;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Speech;

namespace SteamVRTranslator.App.Subtitles;

/// <summary>Recognition resources used serially by one live session or file replay.</summary>
internal sealed class SubtitleProcessingContext(
    Func<SpeechConfiguration, CancellationToken, ISubtitleLocalTranscriber> createLocal, AppLog log,
    SubtitleAudioProcessor audioProcessor, string kind = "live") : IAsyncDisposable
{
    public SubtitleTrace Trace { get; } = new(log, kind);
    private string? _engineKey;
    private string? _settingsSummary;
    private SubtitleDiarizationModelCache.Lease? _diarization;
    public AppConfiguration Configuration { get; private set; } = new();
    public ISubtitleLocalTranscriber? Local { get; private set; }
    public VibeVoiceApiTranscriber? Api { get; private set; }
    public SpeakerIdentityRegistry Speakers { get; } = new();
    public Dictionary<string, int> ApiSpeakers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Task> Translations { get; } = [];

    public async Task ApplyConfigurationAsync(AppConfiguration configuration)
    {
        var key = JsonSerializer.Serialize(new
        {
            Model = configuration.Subtitles.AsrBackend == SubtitleAsrBackends.VibeVoiceApi
                ? null : SenseVoiceModelKey.From(configuration.Speech), configuration.Subtitles.AsrBackend,
            configuration.Subtitles.VibeVoiceServiceUrl, configuration.Subtitles.VibeVoiceApiKey
        });
        var recognitionChanged = !string.Equals(key, _engineKey, StringComparison.Ordinal);
        var summary = $"backend={configuration.Subtitles.AsrBackend} diarization={configuration.Subtitles.Diarization.Enabled} translate={configuration.Subtitles.TranslateText} showOriginal={configuration.Subtitles.ShowOriginalText} targetLanguage={JsonSerializer.Serialize(configuration.Subtitles.TargetLanguage)}";
        if (recognitionChanged || summary != _settingsSummary)
        {
            Trace.Info($"stage=configuration status=apply {summary} recognitionChanged={recognitionChanged}");
            _settingsSummary = summary;
        }
        if (recognitionChanged)
        {
            await DisposeRecognitionAsync().ConfigureAwait(false);
            _engineKey = key;
        }
        Configuration = configuration;
    }

    public async Task PrepareAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var diarization = Configuration.Subtitles.Diarization;
        var diarizationKey = Configuration.Subtitles.AsrBackend != SubtitleAsrBackends.VibeVoiceApi && diarization.Enabled
            ? SubtitleDiarizationModelKey.From(diarization) : null;
        if (_diarization?.Key != diarizationKey)
        {
            var old = _diarization; _diarization = null;
            if (old is not null)
                await SubtitleTrace.MeasureAsync(log, Trace.Tag, "diarization-release", () => old.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        if (diarizationKey is not null && _diarization is null)
            _diarization = await SubtitleTrace.MeasureAsync(log, Trace.Tag, "diarization-acquire",
                () => audioProcessor.AcquireModelsAsync(diarization, token)).ConfigureAwait(false);
        if (Configuration.Subtitles.AsrBackend == SubtitleAsrBackends.VibeVoiceApi)
        {
            if (Api is null)
            {
                Api = new VibeVoiceApiTranscriber(Configuration.Subtitles, log);
                Trace.Info("stage=asr-acquire status=complete backend=VibeVoiceApi");
            }
        }
        else if (Local is null)
        {
            Local = await SubtitleTrace.MeasureAsync(log, Trace.Tag, "asr-acquire",
                () => Task.Run(() => createLocal(Configuration.Speech, token), token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisposeRecognitionAsync().ConfigureAwait(false); }
        finally
        {
            var diarization = _diarization; _diarization = null;
            if (diarization is not null) await diarization.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<SubtitleAudioDocument> AnalyzeAudioAsync(string path, CancellationToken token, string? tag = null) =>
        SubtitleTrace.MeasureAsync(log, tag ?? Trace.Tag, "audio-analyze",
            () => audioProcessor.AnalyzeAsync(path, Configuration.Subtitles.Diarization, Speakers, _diarization, token, tag ?? Trace.Tag),
            document => $"diarization={Configuration.Subtitles.Diarization.Enabled} parts={document.Segments.Count} durationMs={document.Samples.Length * 1000d / SubtitleAudioProcessor.SampleRate:F0}");

    private async ValueTask DisposeRecognitionAsync()
    {
        var local = Local;
        Local = null;
        try { if (local is not null) await local.DisposeAsync().ConfigureAwait(false); }
        finally { Api?.Dispose(); Api = null; }
    }
}
