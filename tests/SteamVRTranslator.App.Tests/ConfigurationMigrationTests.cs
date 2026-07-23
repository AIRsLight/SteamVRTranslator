using System.Globalization;
using System.IO;
using System.Text.Json;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Localization;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class ConfigurationMigrationTests
{
    [Fact]
    public void NewTranslationConfigurationContainsOnlyTheMockProvider()
    {
        var translation = new TranslationConfiguration();

        var provider = Assert.Single(translation.Providers);
        Assert.True(provider.IsMock);
        Assert.Equal(TranslationProviderConfiguration.MockProviderId, translation.ActiveProviderId);
    }

    [Fact]
    public void PromptProviderFallsBackToGlobalWhenNotAssignedOrMissing()
    {
        var translation = new TranslationConfiguration
        {
            ActiveProviderId = "global",
            Providers =
            [
                new TranslationProviderConfiguration { Id = "global" },
                new TranslationProviderConfiguration { Id = "voice" }
            ]
        };

        Assert.Equal("global", translation.GetProviderFor(PromptProviderPurpose.VoiceTranslation)?.Id);

        translation.PromptProviders.VoiceTranslationProviderId = "voice";
        Assert.Equal("voice", translation.GetProviderFor(PromptProviderPurpose.VoiceTranslation)?.Id);

        translation.PromptProviders.VoiceTranslationProviderId = "deleted";
        Assert.Equal("global", translation.GetProviderFor(PromptProviderPurpose.VoiceTranslation)?.Id);
    }

    [Fact]
    public void RuntimeDataAndCapturesResolveInsideInstallationDirectory()
    {
        Assert.Equal(
            Path.GetFullPath(AppContext.BaseDirectory),
            ApplicationDataPaths.RootDirectory,
            ignoreCase: true);
        Assert.Equal(
            Path.Combine(ApplicationDataPaths.RootDirectory, "captures"),
            ApplicationDataPaths.ResolveCaptureDirectory("captures"),
            ignoreCase: true);
        Assert.Equal(
            Path.Combine(ApplicationDataPaths.RootDirectory, "captures"),
            ApplicationDataPaths.ResolveCaptureDirectory(@"D:\external-captures"),
            ignoreCase: true);
        Assert.StartsWith(
            ApplicationDataPaths.RootDirectory,
            ApplicationDataPaths.RuntimeDataDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyDirectoryMigrationMovesFilesWithoutOverwritingInstallationData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"svt-migration-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "legacy");
        var destination = Path.Combine(root, "portable");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "move.txt"), "legacy");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "legacy");
        File.WriteAllText(Path.Combine(source, "nested", "child.txt"), "child");
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "portable");

        try
        {
            ApplicationDataPaths.MigrateDirectory(source, destination);

            Assert.Equal("legacy", File.ReadAllText(Path.Combine(destination, "move.txt")));
            Assert.Equal("portable", File.ReadAllText(Path.Combine(destination, "keep.txt")));
            Assert.Equal("child", File.ReadAllText(Path.Combine(destination, "nested", "child.txt")));
            Assert.True(File.Exists(Path.Combine(source, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(source, "move.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExperimentalFeaturesAreDisabledByDefault()
    {
        var configuration = new AppConfiguration();

        Assert.False(configuration.Subtitles.Enabled);
        Assert.False(configuration.AndroidMirror.Enabled);
    }

    [Theory]
    [InlineData(15, 30)]
    [InlineData(30, 30)]
    [InlineData(45, 60)]
    [InlineData(60, 60)]
    [InlineData(75, 90)]
    [InlineData(90, 90)]
    [InlineData(105, 120)]
    [InlineData(120, 120)]
    [InlineData(144, 120)]
    public void AndroidMirrorFrameRateUsesSupportedSlots(int value, int expected)
    {
        Assert.Equal(
            expected,
            AndroidMirrorConfiguration.NormalizeMaximumFramesPerSecond(value));
    }

    [Fact]
    public void NewConfigurationContainsEditablePromptDefaultsForAllModes()
    {
        var configuration = new AppConfiguration();
        var defaults = BuiltInPromptDefaults.Create(ApplicationLanguages.English);

        Assert.Equal(ApplicationLanguages.English, configuration.UiLanguage);
        Assert.Equal(defaults.MarkdownSystemPrompt, configuration.Translation.SystemPrompt);
        Assert.Equal(
            defaults.MarkdownTranslationPrompt,
            configuration.Translation.MarkdownTranslationPrompt);
        Assert.Equal(
            defaults.LayoutSystemPrompt,
            configuration.Translation.LayoutTranslationSystemPrompt);
        Assert.Equal(
            defaults.LayoutTranslationPrompt,
            configuration.Translation.LayoutTranslationPrompt);
        Assert.Equal(
            defaults.CustomCommandSystemPrompt,
            configuration.Speech.CustomCommandSystemPrompt);
        Assert.Equal(
            defaults.CustomCommandPrompt,
            configuration.Speech.CustomCommandPrompt);
        Assert.Equal(
            defaults.SubtitleTranslationSystemPrompt,
            configuration.Subtitles.TranslationSystemPrompt);
        Assert.Equal(
            defaults.SubtitleTranslationPrompt,
            configuration.Subtitles.TranslationPrompt);
        Assert.Equal(ApplicationLanguages.Supported.Count, configuration.Prompts.Count);
        Assert.Equal(
            VrChatVoiceInputConfiguration.DefaultStreamingChunkIntervalMilliseconds,
            configuration.VrChatVoiceInput.StreamingChunkIntervalMilliseconds);
        Assert.All(
            configuration.Translation.Providers,
            provider => Assert.Equal(
                TranslationProviderConfiguration.DefaultMaxConcurrency,
                provider.MaxConcurrency));
    }

    [Fact]
    public void LegacyConfigurationWithoutSubtitlesReceivesUsableExperimentalDefaults()
    {
        var configuration = JsonSerializer.Deserialize<AppConfiguration>("{}")!;

        ConfigurationStore.NormalizePrompts(configuration);

        Assert.NotNull(configuration.Subtitles);
        Assert.NotNull(configuration.Subtitles.Diarization);
        Assert.InRange(configuration.Subtitles.Diarization.CpuThreadCount, 1, 16);
        Assert.Equal("zh-CN", configuration.Subtitles.TargetLanguage);
        Assert.NotEmpty(configuration.Subtitles.TranslationSystemPrompt);
        Assert.NotEmpty(configuration.Subtitles.TranslationPrompt);
        Assert.Equal(SubtitleAsrBackends.SenseVoice, configuration.Subtitles.AsrBackend);
        Assert.Equal("http://127.0.0.1:5090", configuration.Subtitles.VibeVoiceServiceUrl);
        Assert.Equal(0.42, configuration.Subtitles.WindowWidthMeters, 3);
    }

    [Theory]
    [InlineData(0xA2, 0xA2)]
    [InlineData(0xA3, 0xA3)]
    [InlineData(0x14, 0x14)]
    [InlineData(0x77, 0x77)]
    [InlineData(0x41, 0x41)]
    [InlineData(1, 1)]
    [InlineData(254, 254)]
    [InlineData(0, 0xA2)]
    [InlineData(255, 0xA2)]
    public void DesktopVoiceHotKeyKeepsAnyValidCapturedVirtualKey(
        int configured,
        int expected)
    {
        var configuration = new AppConfiguration();
        configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey = configured;

        ConfigurationStore.NormalizePrompts(configuration);

        Assert.Equal(expected, configuration.VrChatVoiceInput.DesktopHotKeyVirtualKey);
    }

    [Fact]
    public void LegacyOversizedSubtitleDefaultMigratesToVrScale()
    {
        var configuration = new AppConfiguration();
        configuration.Subtitles.WindowWidthMeters = 0.78;

        ConfigurationStore.NormalizePrompts(configuration);

        Assert.Equal(0.42, configuration.Subtitles.WindowWidthMeters, 3);
    }

    [Fact]
    public void SubtitlePromptProviderAssignmentIsIndependentAndCloned()
    {
        var source = new PromptProviderConfiguration
        {
            VoiceTranslationProviderId = "voice",
            SubtitleTranslationProviderId = "subtitle"
        };

        var clone = source.Clone();
        clone.SetProviderId(PromptProviderPurpose.SubtitleTranslation, "changed");

        Assert.Equal("subtitle", source.GetProviderId(PromptProviderPurpose.SubtitleTranslation));
        Assert.Equal("changed", clone.GetProviderId(PromptProviderPurpose.SubtitleTranslation));
        Assert.Equal("voice", clone.GetProviderId(PromptProviderPurpose.VoiceTranslation));
    }

    [Fact]
    public void ProviderConcurrencyIsClampedPerProvider()
    {
        var translation = new TranslationConfiguration
        {
            ActiveProviderId = "low",
            Providers =
            [
                new TranslationProviderConfiguration { Id = "low", MaxConcurrency = 0 },
                new TranslationProviderConfiguration { Id = "high", MaxConcurrency = 100 }
            ]
        };

        ConfigurationStore.NormalizeProviders(translation);

        Assert.Equal(1, translation.Providers.Single(provider => provider.Id == "low").MaxConcurrency);
        Assert.Equal(
            TranslationProviderConfiguration.MaximumMaxConcurrency,
            translation.Providers.Single(provider => provider.Id == "high").MaxConcurrency);
        Assert.Empty(translation.Providers.Single(provider => provider.IsMock).Name);
        Assert.All(
            translation.Providers.Where(provider => !provider.IsMock),
            provider => Assert.False(string.IsNullOrWhiteSpace(provider.Name)));
    }

    [Fact]
    public void PromptEditsAreIsolatedByInterfaceLanguage()
    {
        var configuration = new AppConfiguration();
        var chineseBefore = configuration.GetPromptSet(ApplicationLanguages.Chinese).MarkdownSystemPrompt;
        var japaneseBefore = configuration.GetPromptSet(ApplicationLanguages.Japanese).MarkdownSystemPrompt;

        configuration.GetPromptSet(ApplicationLanguages.English).MarkdownSystemPrompt = "custom English";
        configuration.UiLanguage = ApplicationLanguages.Japanese;
        configuration.ApplyPromptLanguage();

        Assert.Equal("custom English", configuration.GetPromptSet(ApplicationLanguages.English).MarkdownSystemPrompt);
        Assert.Equal(chineseBefore, configuration.GetPromptSet(ApplicationLanguages.Chinese).MarkdownSystemPrompt);
        Assert.Equal(japaneseBefore, configuration.GetPromptSet(ApplicationLanguages.Japanese).MarkdownSystemPrompt);
        Assert.Equal(japaneseBefore, configuration.Translation.SystemPrompt);
    }

    [Theory]
    [InlineData("zh-CN", "zh")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]
    public void SystemLanguageUsesSupportedLanguageOrEnglishFallback(string cultureName, string expected)
    {
        Assert.Equal(expected, ApplicationLanguages.FromCulture(new CultureInfo(cultureName)));
    }

    [Theory]
    [InlineData("zh", "人物、房间", "海报")]
    [InlineData("en", "Ignore people, rooms", "poster")]
    [InlineData("ja", "人物、部屋", "ポスター")]
    public void LayoutPromptKeepsPrimaryTextSubjectAndIgnoresBackground(
        string language,
        string ignoredBackgroundText,
        string subjectText)
    {
        var prompt = BuiltInPromptDefaults.Create(language).LayoutTranslationPrompt;

        Assert.Contains(ignoredBackgroundText, prompt, StringComparison.Ordinal);
        Assert.Contains(subjectText, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyChinesePromptsMigrateWithoutOverwritingOtherLanguages()
    {
        const string legacy = """
            {
              "translation": {
                "systemPrompt": "旧系统提示词",
                "markdownTranslationPrompt": "旧翻译提示词"
              },
              "speech": {
                "customCommandPrompt": "旧命令提示词"
              }
            }
            """;

        var migrated = ConfigurationStore.MigrateLegacyLocalizationConfiguration(legacy);
        using var document = JsonDocument.Parse(migrated);
        var root = document.RootElement;
        var chinese = root.GetProperty("prompts").GetProperty(ApplicationLanguages.Chinese);

        Assert.Equal(ApplicationLanguages.Chinese, root.GetProperty("uiLanguage").GetString());
        Assert.Equal("旧系统提示词", chinese.GetProperty("markdownSystemPrompt").GetString());
        Assert.Equal("旧翻译提示词", chinese.GetProperty("markdownTranslationPrompt").GetString());
        Assert.Equal("旧命令提示词", chinese.GetProperty("customCommandPrompt").GetString());
        Assert.False(root.GetProperty("translation").TryGetProperty("systemPrompt", out _));
        Assert.False(root.GetProperty("speech").TryGetProperty("customCommandPrompt", out _));
    }

    [Fact]
    public void UnsupportedLanguageFallsBackToEnglishAndFollowUiRecognition()
    {
        var configuration = new AppConfiguration
        {
            UiLanguage = "fr",
            Speech = new SpeechConfiguration { RecognitionLanguage = SpeechRecognitionLanguages.FollowInterface }
        };

        ConfigurationStore.NormalizePrompts(configuration);

        Assert.Equal(ApplicationLanguages.English, configuration.UiLanguage);
        Assert.Equal(SpeechRecognitionLanguages.English, configuration.Speech.EffectiveRecognitionLanguage);
        Assert.Equal(
            BuiltInPromptDefaults.Create(ApplicationLanguages.English).MarkdownSystemPrompt,
            configuration.Translation.SystemPrompt);
    }

    [Fact]
    public void ExplicitProviderNameOverridesEndpointAndModelDisplayName()
    {
        var provider = new TranslationProviderConfiguration
        {
            Name = "家庭视觉服务",
            BaseUrl = "https://example.invalid/v1",
            Model = "vision-large"
        };

        Assert.Equal("家庭视觉服务", provider.DisplayName);
    }

    [Theory]
    [InlineData("qwen", "Qwen2.5-VL-7B", true)]
    [InlineData("VL-7", "Qwen2.5-VL-7B", true)]
    [InlineData("gpt", "Qwen2.5-VL-7B", false)]
    [InlineData("", "Qwen2.5-VL-7B", true)]
    public void ProviderModelFilterIsCaseInsensitiveSubstringSearch(
        string filter,
        string model,
        bool expected)
    {
        Assert.Equal(expected, ProviderModelSearch.Matches(model, filter));
    }

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
        Assert.False(string.IsNullOrWhiteSpace(provider.GetProperty("name").GetString()));
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
