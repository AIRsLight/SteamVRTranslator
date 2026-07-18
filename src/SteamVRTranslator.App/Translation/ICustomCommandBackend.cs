namespace SteamVRTranslator.App.Translation;

public interface ICustomCommandBackend
{
    Task<string?> ExecuteAsync(
        byte[] imageBytes,
        string command,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken);
}
