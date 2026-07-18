namespace SteamVRTranslator.App.Translation;

public interface ITranslationBackend
{
    Task<string?> TranslateAsync(
        byte[] imageBytes,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken);
}
