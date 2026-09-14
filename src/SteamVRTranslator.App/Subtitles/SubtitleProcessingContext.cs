using System.Text.Json;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Speech;

namespace SteamVRTranslator.App.Subtitles;

/// <summary>Recognition resources used serially by one live session or file replay.</summary>
internal sealed class SubtitleProcessingContext(
    Func<SpeechConfiguration, CancellationToken, ISubtitleLocalTranscriber> createLocal, AppLog log,
    SubtitleAudioProcessor audioProcessor) : IAsyncDisposable
{
    private string? _engineKey;
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
        if (!string.Equals(key, _engineKey, StringComparison.Ordinal))
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
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        }
        if (diarizationKey is not null)
            _diarization ??= await audioProcessor.AcquireModelsAsync(diarization, token).ConfigureAwait(false);
        if (Configuration.Subtitles.AsrBackend == SubtitleAsrBackends.VibeVoiceApi)
            Api ??= new VibeVoiceApiTranscriber(Configuration.Subtitles, log);
        else if (Local is null)
        {
            Local = await Task.Run(() => createLocal(Configuration.Speech, token), token).ConfigureAwait(false);
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

    public Task<SubtitleAudioDocument> AnalyzeAudioAsync(string path, CancellationToken token) =>
        audioProcessor.AnalyzeAsync(path, Configuration.Subtitles.Diarization, Speakers, _diarization, token);

    private async ValueTask DisposeRecognitionAsync()
    {
        var local = Local;
        Local = null;
        try { if (local is not null) await local.DisposeAsync().ConfigureAwait(false); }
        finally { Api?.Dispose(); Api = null; }
    }
}
