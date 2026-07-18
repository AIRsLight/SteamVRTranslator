using System.Text.Json;
using SteamVRTranslator.App.Configuration;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class ConfigurationMigrationTests
{
    [Fact]
    public void LegacySingleProviderIsMigratedToProviderList()
    {
        const string legacy = """
            {
              "translation": {
                "backend": "openai-compatible",
                "endpoint": "https://example.invalid/v1",
                "model": "vision-model",
                "apiKeyEnvironmentVariable": "MISSING_TEST_KEY"
              }
            }
            """;

        var migrated = ConfigurationStore.MigrateLegacyProviderConfiguration(legacy);
        using var document = JsonDocument.Parse(migrated);
        var translation = document.RootElement.GetProperty("translation");
        var providers = translation.GetProperty("providers");
        var mockProvider = providers.EnumerateArray().Single(provider =>
            provider.GetProperty("type").GetString() == TranslationProviderConfiguration.MockType);
        var provider = providers.EnumerateArray().Single(item =>
            item.GetProperty("type").GetString() == TranslationProviderConfiguration.OpenAiCompatibleType);

        Assert.Equal("openai", translation.GetProperty("activeProviderId").GetString());
        Assert.Equal(TranslationProviderConfiguration.MockProviderId, mockProvider.GetProperty("id").GetString());
        Assert.Equal("https://example.invalid/v1", provider.GetProperty("baseUrl").GetString());
        Assert.Equal("vision-model", provider.GetProperty("model").GetString());
        Assert.False(translation.TryGetProperty("backend", out _));
        Assert.False(translation.TryGetProperty("endpoint", out _));
        Assert.False(translation.TryGetProperty("apiKeyEnvironmentVariable", out _));
    }

    [Theory]
    [InlineData("mock-fixed")]
    [InlineData("capture-only")]
    public void LegacyMockBackendBecomesActiveMockProvider(string backend)
    {
        var legacy = $$"""
            {
              "translation": {
                "backend": "{{backend}}",
                "providers": [
                  {
                    "id": "cloud",
                    "baseUrl": "https://example.invalid/v1",
                    "apiKey": "",
                    "model": "vision-model"
                  }
                ],
                "activeProviderId": "cloud"
              }
            }
            """;

        var migrated = ConfigurationStore.MigrateLegacyProviderConfiguration(legacy);
        using var document = JsonDocument.Parse(migrated);
        var translation = document.RootElement.GetProperty("translation");

        Assert.Equal(
            TranslationProviderConfiguration.MockProviderId,
            translation.GetProperty("activeProviderId").GetString());
        Assert.Contains(
            translation.GetProperty("providers").EnumerateArray(),
            provider => provider.GetProperty("type").GetString() == TranslationProviderConfiguration.MockType);
        Assert.False(translation.TryGetProperty("backend", out _));
    }
}
