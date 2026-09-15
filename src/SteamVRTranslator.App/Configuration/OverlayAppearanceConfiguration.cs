namespace SteamVRTranslator.App.Configuration;

public sealed record OverlayAppearanceConfiguration
{
    public string Preset { get; set; } = "classic";
    public string Background { get; set; } = "#18201D";
    public string Foreground { get; set; } = "#F4F8F6";
    public string Accent { get; set; } = "#4DE0C1";
    public string Border { get; set; } = "#8FA19C";

    public OverlayAppearanceConfiguration Normalized() => new()
    {
        Preset = Preset is "light" or "monochrome" or "custom" ? Preset : "classic",
        Background = NormalizeColor(Background, "#18201D"),
        Foreground = NormalizeColor(Foreground, "#F4F8F6"),
        Accent = NormalizeColor(Accent, "#4DE0C1"),
        Border = NormalizeColor(Border, "#8FA19C")
    };

    public static bool IsValidColor(string? value) => value?.Trim() is { Length: 7 } color &&
        color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static string NormalizeColor(string? value, string fallback) =>
        IsValidColor(value) ? value!.Trim().ToUpperInvariant() : fallback;
}
