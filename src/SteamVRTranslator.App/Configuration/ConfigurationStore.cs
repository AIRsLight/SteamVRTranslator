using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamVRTranslator.App.Configuration;

public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ConfigurationStore()
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamVRTranslator");
        FilePath = Path.Combine(DirectoryPath, "appsettings.json");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public AppConfiguration Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppConfiguration();
        }

        var configurationText = MigrateLegacyProviderConfiguration(File.ReadAllText(FilePath));
        var configuration = JsonSerializer.Deserialize<AppConfiguration>(configurationText, Options)
            ?? new AppConfiguration();

        // Migrate the first prototype defaults to the portable hardware-test defaults.
        if (string.Equals(configuration.CaptureDirectory, "captures", StringComparison.OrdinalIgnoreCase))
        {
            configuration.CaptureDirectory = ".";
        }

        NormalizeProviders(configuration.Translation);

        return configuration;
    }

    internal static string MigrateLegacyProviderConfiguration(string configurationText)
    {
        var root = JsonNode.Parse(configurationText)?.AsObject();
        var translation = root?["translation"]?.AsObject();
        if (root is null || translation is null)
        {
            return configurationText;
        }

        var legacyBackend = translation["backend"]?.GetValue<string>();
        if (!translation.ContainsKey("providers"))
        {
            var baseUrl = translation["baseUrl"]?.GetValue<string>() ??
                          translation["endpoint"]?.GetValue<string>() ??
                          "https://api.openai.com/v1";
            var apiKey = translation["apiKey"]?.GetValue<string>() ?? string.Empty;
            if (apiKey.Length == 0 &&
                translation["apiKeyEnvironmentVariable"]?.GetValue<string>() is { Length: > 0 } environmentVariable)
            {
                apiKey = Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty;
            }

            var provider = new JsonObject
            {
                ["id"] = "openai",
                ["type"] = TranslationProviderConfiguration.OpenAiCompatibleType,
                ["baseUrl"] = baseUrl,
                ["apiKey"] = apiKey,
                ["model"] = translation["model"]?.GetValue<string>() ?? string.Empty
            };
            translation["activeProviderId"] = string.Equals(
                legacyBackend,
                "openai-compatible",
                StringComparison.OrdinalIgnoreCase)
                ? "openai"
                : TranslationProviderConfiguration.MockProviderId;
            translation["providers"] = new JsonArray(provider);
        }

        var providers = translation["providers"]?.AsArray();
        if (providers is not null)
        {
            foreach (var providerNode in providers)
            {
                if (providerNode is JsonObject provider && !provider.ContainsKey("type"))
                {
                    provider["type"] = TranslationProviderConfiguration.OpenAiCompatibleType;
                }
            }

            var hasMock = providers.Any(node =>
                node is JsonObject provider &&
                string.Equals(
                    provider["type"]?.GetValue<string>(),
                    TranslationProviderConfiguration.MockType,
                    StringComparison.OrdinalIgnoreCase));
            if (!hasMock)
            {
                providers.Insert(0, new JsonObject
                {
                    ["id"] = TranslationProviderConfiguration.MockProviderId,
                    ["type"] = TranslationProviderConfiguration.MockType,
                    ["baseUrl"] = string.Empty,
                    ["apiKey"] = string.Empty,
                    ["model"] = string.Empty
                });
            }
        }

        if (string.Equals(legacyBackend, "mock-fixed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(legacyBackend, "capture-only", StringComparison.OrdinalIgnoreCase))
        {
            translation["activeProviderId"] = TranslationProviderConfiguration.MockProviderId;
        }
        else if (string.Equals(legacyBackend, "openai-compatible", StringComparison.OrdinalIgnoreCase) &&
                 providers is not null)
        {
            var activeProviderId = translation["activeProviderId"]?.GetValue<string>();
            var activeProviderExists = providers.Any(node =>
                node is JsonObject provider &&
                string.Equals(provider["id"]?.GetValue<string>(), activeProviderId, StringComparison.OrdinalIgnoreCase));
            if (!activeProviderExists)
            {
                translation["activeProviderId"] = providers
                    .OfType<JsonObject>()
                    .FirstOrDefault(provider => string.Equals(
                        provider["type"]?.GetValue<string>(),
                        TranslationProviderConfiguration.OpenAiCompatibleType,
                        StringComparison.OrdinalIgnoreCase))?["id"]?.GetValue<string>()
                    ?? TranslationProviderConfiguration.MockProviderId;
            }
        }

        translation.Remove("backend");
        translation.Remove("endpoint");
        translation.Remove("apiKeyEnvironmentVariable");
        translation.Remove("baseUrl");
        translation.Remove("apiKey");
        translation.Remove("model");
        return root.ToJsonString(Options);
    }

    private static void NormalizeProviders(TranslationConfiguration translation)
    {
        translation.Providers ??= [];
        var mockProviders = translation.Providers.Where(provider => provider.IsMock).ToList();
        var mock = mockProviders.FirstOrDefault();
        foreach (var duplicateMock in mockProviders.Skip(1))
        {
            translation.Providers.Remove(duplicateMock);
        }

        if (mock is null)
        {
            mock = TranslationProviderConfiguration.CreateMock();
            translation.Providers.Insert(0, mock);
        }
        else
        {
            mock.Id = TranslationProviderConfiguration.MockProviderId;
            mock.Type = TranslationProviderConfiguration.MockType;
            mock.BaseUrl = string.Empty;
            mock.ApiKey = string.Empty;
            mock.Model = string.Empty;
            translation.Providers.Remove(mock);
            translation.Providers.Insert(0, mock);
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in translation.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Id) || !usedIds.Add(provider.Id))
            {
                provider.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(provider.Id);
            }
        }

        if (!translation.Providers.Any(provider =>
                string.Equals(provider.Id, translation.ActiveProviderId, StringComparison.OrdinalIgnoreCase)))
        {
            translation.ActiveProviderId = translation.Providers[0].Id;
        }
    }

    public void Save(AppConfiguration configuration)
    {
        NormalizeProviders(configuration.Translation);
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, Options));
        File.Move(temporaryPath, FilePath, true);
    }
}
