namespace SteamVRTranslator.App.Translation;

public sealed class CaptureOnlyBackend : ITranslationBackend
{
    public Task<string?> TranslateAsync(
        byte[] imageBytes,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}
