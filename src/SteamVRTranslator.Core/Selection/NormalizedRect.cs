namespace SteamVRTranslator.Core.Selection;

public readonly record struct NormalizedRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;

    public float Height => Bottom - Top;

    public bool IsUsable(float minimumSpan = 0.02f) =>
        Width >= minimumSpan && Height >= minimumSpan;

    public static NormalizedRect FromCorners(NormalizedPoint first, NormalizedPoint second)
    {
        first = first.Clamp();
        second = second.Clamp();
        return new NormalizedRect(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Max(first.X, second.X),
            Math.Max(first.Y, second.Y));
    }
}

