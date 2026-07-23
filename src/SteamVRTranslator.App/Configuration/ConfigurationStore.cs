using System.Text.Json;
using System.Text.Json.Nodes;
using SteamVRTranslator.App.Localization;

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
        DirectoryPath = ApplicationDataPaths.RootDirectory;
        FilePath = Path.Combine(DirectoryPath, "appsettings.json");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public AppConfiguration Load()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = new AppConfiguration
            {
                UiLanguage = ApplicationLanguages.DetectSystem()
            };
            NormalizeProviders(defaults.Translation);
            NormalizePrompts(defaults);
            return defaults;
        }

        var configurationText = MigrateLegacyProviderConfiguration(File.ReadAllText(FilePath));
        configurationText = MigrateLegacyLocalizationConfiguration(configurationText);
        var configuration = JsonSerializer.Deserialize<AppConfiguration>(configurationText, Options)
            ?? new AppConfiguration();

        // Captures are portable runtime data and always stay beside the application.
        if (string.IsNullOrWhiteSpace(configuration.CaptureDirectory) ||
            !string.Equals(configuration.CaptureDirectory, "captures", StringComparison.OrdinalIgnoreCase))
        {
            configuration.CaptureDirectory = "captures";
        }

        NormalizeProviders(configuration.Translation);
        NormalizePrompts(configuration);

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
        if (!translation.ContainsKey("promptGeneration"))
        {
            var disableThinking = translation["disableThinking"]?.GetValue<bool>() ?? true;
            translation["promptGeneration"] = JsonSerializer.SerializeToNode(
                PromptGenerationSettingsConfiguration.FromLegacyDisableThinking(disableThinking),
                Options);
        }

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
                ["name"] = TranslationProviderConfiguration.SuggestedName(
                    baseUrl,
                    translation["model"]?.GetValue<string>()),
                ["type"] = TranslationProviderConfiguration.OpenAiCompatibleType,
                ["baseUrl"] = baseUrl,
                ["apiKey"] = apiKey,
                ["model"] = translation["model"]?.GetValue<string>() ?? string.Empty,
                ["maxConcurrency"] = TranslationProviderConfiguration.DefaultMaxConcurrency
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
                    ["name"] = "模拟提供商",
                    ["type"] = TranslationProviderConfiguration.MockType,
                    ["baseUrl"] = string.Empty,
                    ["apiKey"] = string.Empty,
                    ["model"] = string.Empty,
                    ["maxConcurrency"] = TranslationProviderConfiguration.DefaultMaxConcurrency
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

    internal static string MigrateLegacyLocalizationConfiguration(string configurationText)
    {
        var root = JsonNode.Parse(configurationText)?.AsObject();
        if (root is null)
        {
            return configurationText;
        }

        if (!root.ContainsKey("uiLanguage"))
        {
            // Previous releases had a Chinese-only interface and a single Chinese prompt set.
            root["uiLanguage"] = ApplicationLanguages.Chinese;
        }
        else
        {
            root["uiLanguage"] = ApplicationLanguages.Normalize(
                root["uiLanguage"]?.GetValue<string>());
        }

        var translation = root["translation"]?.AsObject();
        var speech = root["speech"]?.AsObject();
        if (!root.ContainsKey("prompts"))
        {
            var defaults = BuiltInPromptDefaults.Create(ApplicationLanguages.Chinese);
            var legacyLayoutPrompt = ReadLegacyPrompt(
                translation,
                "layoutTranslationPrompt",
                defaults.LayoutTranslationPrompt);
            if (string.Equals(
                    legacyLayoutPrompt,
                    BuiltInPromptDefaults.LegacyLayoutTranslationPrompt,
                    StringComparison.Ordinal))
            {
                legacyLayoutPrompt = defaults.LayoutTranslationPrompt;
            }

            root["prompts"] = new JsonObject
            {
                [ApplicationLanguages.Chinese] = JsonSerializer.SerializeToNode(
                    new LocalizedPromptConfiguration
                    {
                        MarkdownSystemPrompt = ReadLegacyPrompt(
                            translation,
                            "systemPrompt",
                            defaults.MarkdownSystemPrompt),
                        MarkdownTranslationPrompt = ReadLegacyPrompt(
                            translation,
                            "markdownTranslationPrompt",
                            defaults.MarkdownTranslationPrompt),
                        LayoutSystemPrompt = ReadLegacyPrompt(
                            translation,
                            "layoutTranslationSystemPrompt",
                            defaults.LayoutSystemPrompt),
                        LayoutTranslationPrompt = legacyLayoutPrompt,
                        CustomCommandSystemPrompt = ReadLegacyPrompt(
                            speech,
                            "customCommandSystemPrompt",
                            defaults.CustomCommandSystemPrompt),
                        CustomCommandPrompt = ReadLegacyPrompt(
                            speech,
                            "customCommandPrompt",
                            defaults.CustomCommandPrompt)
                    },
                    Options)
            };
        }

        translation?.Remove("systemPrompt");
        translation?.Remove("markdownTranslationPrompt");
        translation?.Remove("layoutTranslationSystemPrompt");
        translation?.Remove("layoutTranslationPrompt");
        speech?.Remove("customCommandSystemPrompt");
        speech?.Remove("customCommandPrompt");
        return root.ToJsonString(Options);
    }

    private static string ReadLegacyPrompt(JsonObject? owner, string key, string fallback) =>
        owner?[key]?.GetValue<string>() ?? fallback;

    internal static void NormalizeProviders(TranslationConfiguration translation)
    {
        translation.PromptProviders ??= new PromptProviderConfiguration();
        translation.PromptGeneration ??= new PromptGenerationSettingsConfiguration();
        translation.PromptGeneration.Normalize();
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
            mock.Name = string.Empty;
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
            if (provider.IsMock)
            {
                provider.Name = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(provider.Name))
            {
                provider.Name = TranslationProviderConfiguration.SuggestedName(
                    provider.BaseUrl,
                    provider.Model,
                    provider.IsMock);
            }
            provider.MaxConcurrency = Math.Clamp(
                provider.MaxConcurrency,
                1,
                TranslationProviderConfiguration.MaximumMaxConcurrency);
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

        foreach (var purpose in Enum.GetValues<PromptProviderPurpose>())
        {
            var providerId = translation.PromptProviders.GetProviderId(purpose);
            if (!string.IsNullOrWhiteSpace(providerId) &&
                !translation.Providers.Any(provider =>
                    string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase)))
            {
                translation.PromptProviders.SetProviderId(purpose, null);
            }
        }
    }

    internal static void NormalizePrompts(AppConfiguration configuration)
    {
        configuration.UiLanguage = ApplicationLanguages.Normalize(configuration.UiLanguage);
        configuration.Prompts ??= BuiltInPromptDefaults.CreateAll();
        var normalizedPrompts = new Dictionary<string, LocalizedPromptConfiguration>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (language, prompts) in configuration.Prompts)
        {
            var normalizedLanguage = ApplicationLanguages.Normalize(language);
            if (!normalizedPrompts.ContainsKey(normalizedLanguage) && prompts is not null)
            {
                normalizedPrompts[normalizedLanguage] = prompts;
            }
        }
        foreach (var language in ApplicationLanguages.Supported)
        {
            var defaults = BuiltInPromptDefaults.Create(language);
            if (!normalizedPrompts.TryGetValue(language, out var prompts))
            {
                normalizedPrompts[language] = defaults;
                continue;
            }

            prompts.MarkdownSystemPrompt ??= defaults.MarkdownSystemPrompt;
            prompts.MarkdownTranslationPrompt ??= defaults.MarkdownTranslationPrompt;
            prompts.LayoutSystemPrompt ??= defaults.LayoutSystemPrompt;
            prompts.LayoutTranslationPrompt ??= defaults.LayoutTranslationPrompt;
            prompts.CustomCommandSystemPrompt ??= defaults.CustomCommandSystemPrompt;
            prompts.CustomCommandPrompt ??= defaults.CustomCommandPrompt;
            if (string.IsNullOrWhiteSpace(prompts.VoiceTranslationSystemPrompt))
            {
                prompts.VoiceTranslationSystemPrompt = defaults.VoiceTranslationSystemPrompt;
            }
            if (string.IsNullOrWhiteSpace(prompts.VoiceTranslationPrompt))
            {
                prompts.VoiceTranslationPrompt = defaults.VoiceTranslationPrompt;
            }
            if (string.IsNullOrWhiteSpace(prompts.SubtitleTranslationSystemPrompt))
            {
                prompts.SubtitleTranslationSystemPrompt = defaults.SubtitleTranslationSystemPrompt;
            }
            if (string.IsNullOrWhiteSpace(prompts.SubtitleTranslationPrompt))
            {
                prompts.SubtitleTranslationPrompt = defaults.SubtitleTranslationPrompt;
            }
            if (language == ApplicationLanguages.Chinese &&
                string.Equals(
                    prompts.LayoutTranslationPrompt,
                    BuiltInPromptDefaults.LegacyLayoutTranslationPrompt,
                    StringComparison.Ordinal))
            {
                prompts.LayoutTranslationPrompt = defaults.LayoutTranslationPrompt;
            }
        }
        configuration.Prompts = normalizedPrompts;

        configuration.Speech.SenseVoiceBackend = string.Equals(
            configuration.Speech.SenseVoiceBackend,
            "vulkan",
            StringComparison.OrdinalIgnoreCase)
            ? "vulkan"
            : "cpu";
        if (string.IsNullOrWhiteSpace(configuration.Speech.SenseVoiceVulkanExecutablePath))
        {
            configuration.Speech.SenseVoiceVulkanExecutablePath =
                "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe";
        }
        if (configuration.Speech.SenseVoiceVulkanDeviceIndex < 0)
        {
            configuration.Speech.SenseVoiceVulkanDeviceIndex = null;
            configuration.Speech.SenseVoiceVulkanDeviceName = null;
        }
        configuration.Speech.RecognitionLanguage = SpeechRecognitionLanguages.Normalize(
            configuration.Speech.RecognitionLanguage);
        configuration.Speech.EffectiveRecognitionLanguage = SpeechRecognitionLanguages.Resolve(
            configuration.Speech.RecognitionLanguage,
            configuration.UiLanguage);
        configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds = Math.Clamp(
            configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds,
            VrChatVoiceInputConfiguration.MinimumStreamingChunkIntervalMilliseconds,
            VrChatVoiceInputConfiguration.MaximumStreamingChunkIntervalMilliseconds);
        configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey =
            configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey is >= 1 and <= 254
                ? configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey
                : 0xA2;
        configuration.VrChatVoiceInput.TranslationDisplayMode = VoiceTranslationDisplayModes.Normalize(
            configuration.VrChatVoiceInput.TranslationDisplayMode);
        if (string.IsNullOrWhiteSpace(configuration.VrChatVoiceInput.TranslationTargetLanguage))
        {
            configuration.VrChatVoiceInput.TranslationTargetLanguage = "zh-CN";
        }
        configuration.AndroidMirror ??= new AndroidMirrorConfiguration();
        configuration.AndroidMirror.MaximumSize = AndroidMirrorConfiguration.NormalizeMaximumSize(
            configuration.AndroidMirror.MaximumSize);
        configuration.AndroidMirror.MaximumFramesPerSecond =
            AndroidMirrorConfiguration.NormalizeMaximumFramesPerSecond(
                configuration.AndroidMirror.MaximumFramesPerSecond);
        configuration.AndroidMirror.VideoDecoder = AndroidVideoDecoders.Normalize(
            configuration.AndroidMirror.VideoDecoder);
        configuration.AndroidMirror.WindowWidthMeters = AndroidMirrorConfiguration.NormalizeWindowWidthMeters(
            configuration.AndroidMirror.WindowWidthMeters);
        configuration.Subtitles ??= new SubtitleConfiguration();
        configuration.Subtitles.Diarization ??= new SubtitleDiarizationConfiguration();
        configuration.Subtitles.AsrBackend = SubtitleAsrBackends.Normalize(
            configuration.Subtitles.AsrBackend);
        configuration.Subtitles.VibeVoiceServiceUrl = string.IsNullOrWhiteSpace(
            configuration.Subtitles.VibeVoiceServiceUrl)
            ? "http://127.0.0.1:5090"
            : configuration.Subtitles.VibeVoiceServiceUrl.Trim().TrimEnd('/');
        configuration.Subtitles.VibeVoiceApiKey ??= string.Empty;
        configuration.Subtitles.MaximumHistoryEntries = Math.Clamp(
            configuration.Subtitles.MaximumHistoryEntries,
            10,
            1000);
        configuration.Subtitles.MaximumHistoryCharacters = Math.Clamp(
            configuration.Subtitles.MaximumHistoryCharacters,
            1000,
            500000);
        if (Math.Abs(configuration.Subtitles.WindowWidthMeters - 0.78) < 0.001)
        {
            configuration.Subtitles.WindowWidthMeters = 0.42;
        }
        configuration.Subtitles.WindowWidthMeters = Math.Clamp(
            configuration.Subtitles.WindowWidthMeters,
            0.28,
            1.5);
        configuration.Subtitles.WindowDistanceMeters = Math.Clamp(
            configuration.Subtitles.WindowDistanceMeters,
            0.35,
            2.5);
        configuration.Subtitles.WindowOpacity = Math.Clamp(
            configuration.Subtitles.WindowOpacity,
            0.5,
            1.0);
        configuration.Subtitles.Diarization.CpuThreadCount = Math.Clamp(
            configuration.Subtitles.Diarization.CpuThreadCount,
            1,
            Math.Max(1, Environment.ProcessorCount));
        configuration.Subtitles.Diarization.ClusteringThreshold = Math.Clamp(
            configuration.Subtitles.Diarization.ClusteringThreshold,
            0.1,
            1.5);
        if (string.IsNullOrWhiteSpace(configuration.Subtitles.TargetLanguage))
        {
            configuration.Subtitles.TargetLanguage = "zh-CN";
        }
        configuration.ApplyPromptLanguage();
    }

    public void Save(AppConfiguration configuration)
    {
        NormalizeProviders(configuration.Translation);
        NormalizePrompts(configuration);
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, Options));
        File.Move(temporaryPath, FilePath, true);
    }
}
