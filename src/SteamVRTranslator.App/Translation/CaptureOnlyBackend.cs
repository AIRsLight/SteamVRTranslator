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

    public Task<string?> TranslateLayoutAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task<string?> TranslateTextAsync(
        string sourceText,
        string targetLanguage,
        string systemPrompt,
        string taskPrompt,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task<string?> ExecuteCustomCommandAsync(
        byte[] imageBytes,
        string command,
        IReadOnlyList<AssistantConversationTurn> history,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}
