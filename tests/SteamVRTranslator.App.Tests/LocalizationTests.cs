using SteamVRTranslator.App.Localization;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData(ApplicationLanguages.English)]
    [InlineData(ApplicationLanguages.Chinese)]
    [InlineData(ApplicationLanguages.Japanese)]
    public void EveryInterfaceLanguageContainsEveryCanonicalResource(string language)
    {
        Assert.All(
            AppLocalization.TranslationKeys,
            key => Assert.True(
                AppLocalization.HasExactTranslation(language, key),
                $"Missing '{key}' in '{language}'."));
    }

    [Fact]
    public void SwitchingLanguageChangesRuntimeAndOverlayText()
    {
        Assert.Equal("Ready", AppLocalization.Translate("en", "Selection.Ready"));
        Assert.Equal("就绪", AppLocalization.Translate("zh", "Selection.Ready"));
        Assert.Equal("準備完了", AppLocalization.Translate("ja", "Selection.Ready"));
        Assert.Equal(
            "Release either trigger to capture",
            AppLocalization.Translate("en", "Overlay.Selection.Release"));
        Assert.Equal(
            "どちらかのトリガーを離すとキャプチャ",
            AppLocalization.Translate("ja", "Overlay.Selection.Release"));
    }

    [Theory]
    [InlineData(ApplicationLanguages.English)]
    [InlineData(ApplicationLanguages.Chinese)]
    [InlineData(ApplicationLanguages.Japanese)]
    public void LanguageChoicesAlwaysUseTheirNativeNames(string interfaceLanguage)
    {
        Assert.Equal("English", AppLocalization.Translate(interfaceLanguage, "Language.English"));
        Assert.Equal("简体中文", AppLocalization.Translate(interfaceLanguage, "Language.Chinese"));
        Assert.Equal("日本語", AppLocalization.Translate(interfaceLanguage, "Language.Japanese"));
    }
}
