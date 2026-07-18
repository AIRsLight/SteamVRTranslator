namespace SteamVRTranslator.Core.Selection;

public readonly record struct NormalizedPoint(float X, float Y)
{
    public NormalizedPoint Clamp() => new(
        Math.Clamp(X, 0f, 1f),
        Math.Clamp(Y, 0f, 1f));
}

