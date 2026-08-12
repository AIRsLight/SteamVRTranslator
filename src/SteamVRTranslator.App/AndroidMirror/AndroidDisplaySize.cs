using System.Text.RegularExpressions;

namespace SteamVRTranslator.App.AndroidMirror;

internal readonly record struct AndroidDisplaySize(int Width, int Height)
{
    public int LongSide => Math.Max(Width, Height);

    public int ShortSide => Math.Min(Width, Height);
}

internal static partial class AndroidDisplaySizeResolver
{
    public static bool TryParseEffectiveSize(string? output, out AndroidDisplaySize size)
    {
        size = default;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        AndroidDisplaySize? physical = null;
        AndroidDisplaySize? displayOverride = null;
        foreach (Match match in LabeledSizePattern().Matches(output))
        {
            if (!TryCreateSize(match, out var candidate))
            {
                continue;
            }

            if (string.Equals(match.Groups["kind"].Value, "Override", StringComparison.OrdinalIgnoreCase))
            {
                displayOverride = candidate;
            }
            else
            {
                physical = candidate;
            }
        }

        if ((displayOverride ?? physical) is { } labeled)
        {
            size = labeled;
            return true;
        }

        var fallback = UnlabeledSizePattern().Matches(output).Cast<Match>().LastOrDefault();
        return fallback is not null && TryCreateSize(fallback, out size);
    }

    public static int CalculateScrcpyMaximumSize(int targetShortSide, AndroidDisplaySize display)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetShortSide, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(display.Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(display.Height, 1);

        if (targetShortSide >= display.ShortSide)
        {
            return display.LongSide;
        }

        var scaledLongSide = (int)Math.Round(
            (double)targetShortSide * display.LongSide / display.ShortSide,
            MidpointRounding.AwayFromZero);
        return Math.Clamp(scaledLongSide, 1, display.LongSide);
    }

    private static bool TryCreateSize(Match match, out AndroidDisplaySize size)
    {
        size = default;
        if (!int.TryParse(match.Groups["width"].Value, out var width) ||
            !int.TryParse(match.Groups["height"].Value, out var height) ||
            width <= 0 ||
            height <= 0)
        {
            return false;
        }

        size = new AndroidDisplaySize(width, height);
        return true;
    }

    [GeneratedRegex(
        @"(?<kind>Physical|Override)\s+size:\s*(?<width>\d+)\s*x\s*(?<height>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabeledSizePattern();

    [GeneratedRegex(
        @"(?<width>\d+)\s*x\s*(?<height>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnlabeledSizePattern();
}
