using SteamVRTranslator.Core.Geometry;
using Valve.VR;

namespace SteamVRTranslator.App.SteamVR;

/// <summary>One world-space anchor for a continuous display, independent of later head poses.</summary>
internal sealed class VoiceInputEchoPlacement
{
    public const float WidthMeters = 1.6f;
    public const float DistanceMeters = 1.2f;
    public const float VerticalOffsetMeters = -0.2f;
    public HmdMatrix34_t? Transform { get; private set; }
    private float _initialWidth, _initialHeight;

    public bool TryPlace(TrackedDevicePose_t pose, float widthMeters = WidthMeters, float heightMeters = 0.35f)
    {
        if (Transform.HasValue) return true;
        if (!pose.bPoseIsValid || !pose.bDeviceIsConnected) return false;
        if (!float.IsFinite(widthMeters) || !float.IsFinite(heightMeters) || widthMeters <= 0 || heightMeters <= 0) return false;
        var matrix = pose.mDeviceToAbsoluteTracking;
        var position = new Vector3f(matrix.m3, matrix.m7, matrix.m11);
        var up = new Vector3f(0, 1, 0);
        var forward = new Vector3f(-matrix.m2, 0, -matrix.m10);
        // Looking straight up/down still gives an upright, stable panel from the head's right axis.
        if (forward.LengthSquared < 0.000001f)
            forward = Vector3f.Cross(up, new Vector3f(matrix.m0, 0, matrix.m8));
        if (!float.IsFinite(position.LengthSquared) || !float.IsFinite(forward.LengthSquared) ||
            forward.LengthSquared < 0.000001f) return false;
        forward = forward.Normalized();
        var normal = forward * -1;
        var right = Vector3f.Cross(up, normal).Normalized();
        var center = position + forward * DistanceMeters + up * VerticalOffsetMeters;
        Transform = new HmdMatrix34_t
        {
            m0 = right.X, m1 = up.X, m2 = normal.X, m3 = center.X,
            m4 = right.Y, m5 = up.Y, m6 = normal.Y, m7 = center.Y,
            m8 = right.Z, m9 = up.Z, m10 = normal.Z, m11 = center.Z
        };
        _initialWidth = widthMeters;
        _initialHeight = heightMeters;
        return true;
    }

    public HmdMatrix34_t TransformForSize(float widthMeters, float heightMeters)
    {
        var transform = Transform ?? throw new InvalidOperationException("语音回显尚未定位。");
        // Resize around the original upper-left corner, never around the viewer's current pose.
        var offset = new Vector3f(transform.m0, transform.m4, transform.m8) * ((widthMeters - _initialWidth) / 2)
            - new Vector3f(transform.m1, transform.m5, transform.m9) * ((heightMeters - _initialHeight) / 2);
        transform.m3 += offset.X; transform.m7 += offset.Y; transform.m11 += offset.Z;
        return transform;
    }

    public void Reset() => Transform = null;
}
