using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.Translation;

internal static class TranslationBackendFactory
{
    public static ITranslationBackend Create(
        TranslationConfiguration configuration,
        TranslationProviderConfiguration provider,
        HttpClient httpClient,
        string? customCommandSystemPrompt = null,
        string? customCommandPrompt = null,
        PromptProviderPurpose textTranslationPurpose = PromptProviderPurpose.VoiceTranslation)
    {
        if (provider.IsMock)
        {
            return new MockTranslationBackend();
        }

        if (string.Equals(
                provider.Type,
                TranslationProviderConfiguration.OpenAiCompatibleType,
                StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAiCompatibleVisionBackend(
                configuration,
                httpClient,
                customCommandSystemPrompt,
                customCommandPrompt,
                providerId: provider.Id,
                textTranslationPurpose: textTranslationPurpose);
        }

        if (provider.IsGoogleAiStudio)
        {
            return new GoogleAiStudioVisionBackend(
                configuration,
                httpClient,
                customCommandSystemPrompt,
                customCommandPrompt,
                providerId: provider.Id,
                textTranslationPurpose: textTranslationPurpose);
        }

        throw new InvalidOperationException($"不支持的翻译提供商类型：{provider.Type}");
    }
}
