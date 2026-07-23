namespace SteamVRTranslator.App.Translation;

public interface ITranslationBackend
{
    Task<string?> TranslateAsync(
        byte[] imageBytes,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken);

    Task<string?> TranslateLayoutAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken);

    Task<string?> TranslateTextAsync(
        string sourceText,
        string targetLanguage,
        string systemPrompt,
        string taskPrompt,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken);

    Task<string?> ExecuteCustomCommandAsync(
        byte[] imageBytes,
        string command,
        IReadOnlyList<AssistantConversationTurn> history,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken);
}

public sealed record AssistantConversationTurn(string User, string Assistant);
