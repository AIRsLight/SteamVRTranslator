using SteamVRTranslator.App.Translation;
using SteamVRTranslator.Core.Geometry;
using Valve.VR;

namespace SteamVRTranslator.App.SteamVR;

internal enum InteractiveOverlayKind
{
    Capture,
    Result
}

internal sealed class InteractiveOverlay
{
    public required long Id { get; init; }

    public required InteractiveOverlayKind Kind { get; init; }

    public required SpatialSelectionPlane Plane { get; set; }

    public CapturedFrame? Capture { get; init; }

    public ResultOverlayState? Result { get; init; }

    public bool IsShown { get; set; }

    public bool IsDirty { get; set; } = true;

    public double MaximumScroll { get; set; }

    public DateTimeOffset LastRenderAt { get; set; }

    public int RenderCount { get; set; }
}

internal readonly record struct OverlayContact(long OverlayId, float Distance);

internal readonly record struct TrackedHandPose(
    Vector3f Position,
    Vector3f Right,
    Vector3f Up,
    Vector3f Forward);

internal readonly record struct OverlayGrab(
    long OverlayId,
    ETrackedControllerRole Hand,
    SpatialSelectionPlane InitialPlane,
    TrackedHandPose InitialHand);

internal static class SpatialOverlayInteraction
{
    public const float ContactDistance = 0.05f;
    private const float EdgeTolerance = 0.025f;

    public static bool TryContact(
        SpatialSelectionPlane plane,
        Vector3f handPosition,
        out float distance)
    {
        var delta = handPosition - plane.Center;
        distance = MathF.Abs(Vector3f.Dot(delta, plane.Normal));
        if (distance > ContactDistance)
        {
            return false;
        }

        var horizontal = MathF.Abs(Vector3f.Dot(delta, plane.Right));
        var vertical = MathF.Abs(Vector3f.Dot(delta, plane.Up));
        return horizontal <= (plane.Width / 2f) + EdgeTolerance &&
               vertical <= (plane.Height / 2f) + EdgeTolerance;
    }

    public static SpatialSelectionPlane Move(OverlayGrab grab, TrackedHandPose currentHand)
    {
        var initialOffset = grab.InitialPlane.Center - grab.InitialHand.Position;
        return grab.InitialPlane with
        {
            Center = currentHand.Position + Rotate(initialOffset, grab.InitialHand, currentHand),
            Right = Rotate(grab.InitialPlane.Right, grab.InitialHand, currentHand).Normalized(),
            Up = Rotate(grab.InitialPlane.Up, grab.InitialHand, currentHand).Normalized(),
            Normal = Rotate(grab.InitialPlane.Normal, grab.InitialHand, currentHand).Normalized()
        };
    }

    private static Vector3f Rotate(
        Vector3f value,
        TrackedHandPose initial,
        TrackedHandPose current)
    {
        var localX = Vector3f.Dot(value, initial.Right);
        var localY = Vector3f.Dot(value, initial.Up);
        var localZ = Vector3f.Dot(value, initial.Forward);
        return (current.Right * localX) +
               (current.Up * localY) +
               (current.Forward * localZ);
    }
}
