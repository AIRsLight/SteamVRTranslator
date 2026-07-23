using System.Globalization;

namespace SteamVRTranslator.App.Localization;

public static class ApplicationLanguages
{
    public const string Chinese = "zh";
    public const string English = "en";
    public const string Japanese = "ja";

    public static IReadOnlyList<string> Supported { get; } = [English, Japanese, Chinese];

    public static string DetectSystem() => FromCulture(CultureInfo.CurrentUICulture);

    public static string FromCulture(CultureInfo culture) =>
        Normalize(culture.TwoLetterISOLanguageName);

    public static string Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "zh-hans" or "zh-sg" => Chinese,
        "ja" or "ja-jp" => Japanese,
        "en" or "en-us" or "en-gb" => English,
        _ => English
    };
}

public static class SpeechRecognitionLanguages
{
    public const string FollowInterface = "follow-ui";
    public const string Automatic = "auto";
    public const string Chinese = "zh";
    public const string English = "en";
    public const string Japanese = "ja";
    public const string Cantonese = "yue";
    public const string Korean = "ko";

    public static string Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        Automatic => Automatic,
        Chinese => Chinese,
        English => English,
        Japanese => Japanese,
        Cantonese => Cantonese,
        Korean => Korean,
        FollowInterface => FollowInterface,
        _ => FollowInterface
    };

    public static string Resolve(string? setting, string? uiLanguage)
    {
        var normalized = Normalize(setting);
        return normalized == FollowInterface
            ? ApplicationLanguages.Normalize(uiLanguage)
            : normalized;
    }
}
