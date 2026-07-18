namespace SteamVRTranslator.App.SteamVR;

internal static class ResultScrollMath
{
    private const double DeadZone = 0.24;
    private const double PixelsPerSecond = 320;

    public static double CalculateDelta(double axis, bool inverted, TimeSpan elapsed)
    {
        var magnitude = Math.Abs(axis);
        if (magnitude <= DeadZone || elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var normalized = Math.Clamp((magnitude - DeadZone) / (1 - DeadZone), 0, 1);
        var response = normalized * normalized * (3 - (2 * normalized));
        var direction = Math.Sign(axis) * (inverted ? -1 : 1);
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, 0.1);
        return direction * response * PixelsPerSecond * seconds;
    }

    public static bool IsNeutral(double axis) => Math.Abs(axis) <= DeadZone;
}
