using SteamVRTranslator.Core.Selection;

namespace SteamVRTranslator.Core.Geometry;

public readonly record struct SpatialSelectionPlane(
    Vector3f Center,
    Vector3f Right,
    Vector3f Up,
    Vector3f Normal,
    float Width,
    float Height,
    float OverlayExtent,
    NormalizedPoint LeftPointer,
    NormalizedPoint RightPointer,
    float ViewAngleDegrees)
{
    private const float OverlayMargin = 1.15f;
    public const float MinimumWidthMeters = 0.10f;
    public const float MinimumHeightMeters = 0.10f;
    public const float MaximumViewAngleDegrees = 35f;

    public bool HasUsableDimensions =>
        Width >= MinimumWidthMeters && Height >= MinimumHeightMeters;

    public bool HasUsableOrientation => ViewAngleDegrees <= MaximumViewAngleDegrees;

    public bool IsUsable => HasUsableDimensions && HasUsableOrientation;

    public IReadOnlyList<Vector3f> Corners =>
    [
        Center - (Right * (Width / 2f)) + (Up * (Height / 2f)),
        Center + (Right * (Width / 2f)) + (Up * (Height / 2f)),
        Center - (Right * (Width / 2f)) - (Up * (Height / 2f)),
        Center + (Right * (Width / 2f)) - (Up * (Height / 2f))
    ];

    public static bool TryCreate(
        Vector3f hmdPosition,
        Vector3f hmdForward,
        Vector3f hmdRight,
        Vector3f leftControllerPosition,
        Vector3f rightControllerPosition,
        out SpatialSelectionPlane plane)
    {
        if (hmdForward.Length < 0.5f || hmdRight.Length < 0.5f)
        {
            plane = default;
            return false;
        }

        var diagonal = rightControllerPosition - leftControllerPosition;
        if (diagonal.Length < 0.06f)
        {
            plane = default;
            return false;
        }

        var center = (leftControllerPosition + rightControllerPosition) * 0.5f;
        var diagonalDirection = diagonal.Normalized();
        var viewDirection = hmdPosition - center;
        var normalCandidate = viewDirection -
                              (diagonalDirection * Vector3f.Dot(viewDirection, diagonalDirection));
        if (normalCandidate.Length < 0.02f)
        {
            plane = default;
            return false;
        }

        var normal = normalCandidate.Normalized();
        var planeFacingDirection = hmdForward.Normalized() * -1f;
        var viewAngleDegrees = RadiansToDegrees(MathF.Acos(Math.Clamp(
            Vector3f.Dot(normal, planeFacingDirection),
            -1f,
            1f)));
        // Project the HMD-local horizontal axis onto the plane. This avoids the
        // world-up singularity when the user looks down at a horizontal frame.
        var normalizedHmdRight = hmdRight.Normalized();
        var rightCandidate = normalizedHmdRight -
                             (normal * Vector3f.Dot(normalizedHmdRight, normal));
        if (rightCandidate.Length < 0.02f)
        {
            rightCandidate = Vector3f.Cross(new Vector3f(0f, 1f, 0f), normal);
            if (rightCandidate.Length < 0.02f)
            {
                rightCandidate = Vector3f.Cross(new Vector3f(0f, 0f, -1f), normal);
            }
        }

        var right = rightCandidate.Normalized();
        if (Vector3f.Dot(right, normalizedHmdRight) < 0)
        {
            right *= -1f;
        }

        var up = Vector3f.Cross(normal, right).Normalized();
        var diagonalX = Vector3f.Dot(diagonal, right);
        var diagonalY = Vector3f.Dot(diagonal, up);
        var width = MathF.Abs(diagonalX);
        var height = MathF.Abs(diagonalY);
        var extent = MathF.Max(MathF.Max(width, height) * OverlayMargin, 0.12f);
        var leftPointer = new NormalizedPoint(
            0.5f - (diagonalX / (2f * extent)),
            0.5f + (diagonalY / (2f * extent))).Clamp();
        var rightPointer = new NormalizedPoint(
            0.5f + (diagonalX / (2f * extent)),
            0.5f - (diagonalY / (2f * extent))).Clamp();

        plane = new SpatialSelectionPlane(
            center,
            right,
            up,
            normal,
            width,
            height,
            extent,
            leftPointer,
            rightPointer,
            viewAngleDegrees);
        return true;
    }

    private static float RadiansToDegrees(float radians) => radians * (180f / MathF.PI);
}
