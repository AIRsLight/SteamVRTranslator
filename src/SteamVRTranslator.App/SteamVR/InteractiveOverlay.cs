using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Translation;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Valve.VR;

namespace SteamVRTranslator.App.SteamVR;

internal enum InteractiveOverlayKind
{
    Capture,
    Result,
    Window
}

internal enum OverlayToolbarAction
{
    Translate,
    LayoutTranslate,
    CustomCommand,
    Close,
    Send
}

internal enum OverlayToolbarSide
{
    Left,
    Right
}

internal static class OverlayToolbarPlacement
{
    public static OverlayToolbarSide OppositeHoldingHand(ETrackedControllerRole hand) => hand switch
    {
        ETrackedControllerRole.LeftHand => OverlayToolbarSide.Right,
        ETrackedControllerRole.RightHand => OverlayToolbarSide.Left,
        _ => OverlayToolbarSide.Right
    };
}

internal static class ResultOverlayLayout
{
    public const float ChatWidthMeters = 0.30f;
    public const float ChatHeightMeters = 0.44f;

    public static SpatialSelectionPlane ApplyModeDimensions(
        SpatialSelectionPlane plane,
        AssistantRequestMode mode) =>
        mode == AssistantRequestMode.CustomCommand
            ? plane with
            {
                Width = ChatWidthMeters,
                Height = ChatHeightMeters,
                OverlayExtent = ChatHeightMeters * 1.15f
            }
            : plane;
}

internal sealed class InteractiveOverlay
{
    public required long Id { get; init; }

    public required InteractiveOverlayKind Kind { get; init; }

    public required SpatialSelectionPlane Plane { get; set; }

    public CapturedFrame? Capture { get; init; }

    public ResultOverlayState? Result { get; init; }

    public AssistantConversation? Conversation { get; init; }

    public bool IsShown { get; set; }

    public bool IsDirty { get; set; } = true;

    public bool IsChromeDirty { get; set; } = true;

    public bool IsProgressDirty { get; set; } = true;

    public bool IsHighlighted { get; set; }

    public bool IsCommandRecording { get; set; }

    public bool IsPresentationDeferred { get; set; }

    public OverlayToolbarSide ToolbarSide { get; set; } = OverlayToolbarSide.Right;

    public double? CloseHoldProgress { get; set; }

    public bool SynchronizeTextureBuffers { get; set; } = true;

    public bool SynchronizeVideoTextureBuffers { get; set; } = true;

    public bool SynchronizeChromeTextureBuffers { get; set; } = true;

    public bool SynchronizeProgressTextureBuffers { get; set; } = true;

    public bool SynchronizePointerTextureBuffers { get; set; } = true;

    public bool SynchronizeRayTextureBuffers { get; set; } = true;

    public double MaximumScroll { get; set; }

    public DateTimeOffset LastRenderAt { get; set; }

    public int RenderCount { get; set; }

    public OverlayPointerVisual? Pointer { get; set; }

    public bool? RenderedPointerPressed { get; set; }

    public WpfWindowOverlaySource? WindowSource { get; init; }

    public bool CanGrab { get; init; } = true;

    public bool ShowToolbarWhenGrabbed { get; init; } = true;
}

internal readonly record struct OverlayContact(long OverlayId, float Distance);

internal readonly record struct HandOverlayContact(
    ETrackedControllerRole Hand,
    OverlayContact Contact);

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

internal sealed class OverlayGrabState
{
    private readonly Dictionary<ETrackedControllerRole, OverlayGrab> _byHand = [];

    public int Count => _byHand.Count;

    public bool HasMultiple => Count > 1;

    public OverlayGrab? Single => Count == 1 ? _byHand.Values.First() : null;

    public IEnumerable<OverlayGrab> Active => _byHand.Values;

    public bool ContainsHand(ETrackedControllerRole hand) => _byHand.ContainsKey(hand);

    public bool ContainsOverlay(long overlayId) =>
        _byHand.Values.Any(grab => grab.OverlayId == overlayId);

    public bool TryGet(ETrackedControllerRole hand, out OverlayGrab grab) =>
        _byHand.TryGetValue(hand, out grab);

    public bool TryBegin(OverlayGrab grab)
    {
        if (_byHand.ContainsKey(grab.Hand) || ContainsOverlay(grab.OverlayId))
        {
            return false;
        }

        _byHand.Add(grab.Hand, grab);
        return true;
    }

    public bool Release(ETrackedControllerRole hand, out OverlayGrab grab) =>
        _byHand.Remove(hand, out grab);

    public bool RemoveOverlay(long overlayId)
    {
        var hands = _byHand
            .Where(pair => pair.Value.OverlayId == overlayId)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var hand in hands)
        {
            _byHand.Remove(hand);
        }

        return hands.Length > 0;
    }

    public void Clear() => _byHand.Clear();
}

internal readonly record struct OverlayPointerVisual(
    ETrackedControllerRole Hand,
    NormalizedPoint TexturePoint,
    NormalizedPoint ContentPoint,
    bool IsPressed,
    OverlayPointerRayVisual? Ray = null,
    Vector3f? WorldPoint = null);

internal readonly record struct OverlayPointerRayVisual(
    Vector3f Source,
    Vector3f Target,
    Vector3f Viewer);

internal readonly record struct OverlayPointerCapture(
    long OverlayId,
    ETrackedControllerRole Hand,
    NormalizedPoint LastTexturePoint,
    NormalizedPoint LastContentPoint,
    Vector3f LastWorldPoint,
    NormalizedPoint PressedContentPoint,
    bool IsSwiping,
    OverlayToolbarAction? ToolbarAction,
    string? WindowControlName);

internal static class OverlaySwipeGesture
{
    public const double ActivationDistancePixels = 8;

    public static bool ShouldStart(
        NormalizedPoint pressedPoint,
        NormalizedPoint currentPoint,
        double viewportWidth,
        double viewportHeight)
    {
        var horizontal = Math.Abs(currentPoint.X - pressedPoint.X) * Math.Max(1, viewportWidth);
        var vertical = Math.Abs(currentPoint.Y - pressedPoint.Y) * Math.Max(1, viewportHeight);
        return vertical >= ActivationDistancePixels && vertical >= horizontal;
    }

    public static double CalculateOffsetDelta(
        NormalizedPoint previousPoint,
        NormalizedPoint currentPoint,
        double viewportHeight) =>
        (previousPoint.Y - currentPoint.Y) * Math.Max(1, viewportHeight);
}

internal readonly record struct StabilizedOverlayPointer(
    NormalizedPoint TexturePoint,
    NormalizedPoint ContentPoint,
    bool Changed);

internal readonly record struct StabilizedPointerRay(
    Vector3f Source,
    Vector3f Direction);

internal readonly record struct OverlayCloseHoldUpdate(
    long? OverlayId,
    double Progress,
    bool CompletedNow);

internal readonly record struct OverlayCloseHoldRelease(
    long? OverlayId,
    bool Completed);

internal sealed class OverlayCloseHoldTracker
{
    private readonly TimeSpan _duration;
    private DateTimeOffset _pressedAt;

    public OverlayCloseHoldTracker(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _duration = duration;
    }

    public bool IsPressed { get; private set; }

    public bool IsCompleted { get; private set; }

    public long? OverlayId { get; private set; }

    public void Press(long? overlayId, DateTimeOffset now)
    {
        IsPressed = true;
        IsCompleted = false;
        OverlayId = overlayId;
        _pressedAt = now;
    }

    public OverlayCloseHoldUpdate Update(DateTimeOffset now)
    {
        if (!IsPressed)
        {
            return default;
        }

        var progress = Math.Clamp((now - _pressedAt).TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        var completedNow = OverlayId is not null && !IsCompleted && progress >= 1;
        if (completedNow)
        {
            IsCompleted = true;
        }

        return new OverlayCloseHoldUpdate(OverlayId, progress, completedNow);
    }

    public OverlayCloseHoldRelease Release()
    {
        var result = new OverlayCloseHoldRelease(OverlayId, IsCompleted);
        Reset();
        return result;
    }

    public void Reset()
    {
        IsPressed = false;
        IsCompleted = false;
        OverlayId = null;
        _pressedAt = default;
    }
}

internal sealed class OverlayPointerStabilizer
{
    internal const float DeadZonePixels = 0.25f;
    internal static readonly TimeSpan DefaultUpdateInterval = TimeSpan.FromMilliseconds(16);
    internal static readonly TimeSpan HighResolutionUpdateInterval = TimeSpan.FromMilliseconds(16);

    private long? _overlayId;
    private ETrackedControllerRole? _hand;
    private NormalizedPoint _texturePoint;
    private NormalizedPoint _contentPoint;
    private bool _pressed;
    private bool _hasPoint;
    private DateTimeOffset _lastUpdateAt;

    public StabilizedOverlayPointer Update(
        long overlayId,
        ETrackedControllerRole hand,
        NormalizedPoint measuredTexturePoint,
        NormalizedPoint measuredContentPoint,
        bool pressed,
        DateTimeOffset now,
        TimeSpan updateInterval)
    {
        var nextTexture = measuredTexturePoint.Clamp();
        var nextContent = measuredContentPoint.Clamp();
        var targetChanged = !_hasPoint || _overlayId != overlayId || _hand != hand;
        var pressedChanged = _hasPoint && _pressed != pressed;
        if (targetChanged)
        {
            _overlayId = overlayId;
            _hand = hand;
            _texturePoint = nextTexture;
            _contentPoint = nextContent;
            _pressed = pressed;
            _hasPoint = true;
            _lastUpdateAt = now;
            return new StabilizedOverlayPointer(_texturePoint, _contentPoint, true);
        }

        if (!pressedChanged && now - _lastUpdateAt < updateInterval)
        {
            return new StabilizedOverlayPointer(nextTexture, nextContent, false);
        }

        var deltaX = MathF.Abs(nextTexture.X - _texturePoint.X) * OverlayRenderer.Width;
        var deltaY = MathF.Abs(nextTexture.Y - _texturePoint.Y) * OverlayRenderer.Height;
        if (!pressedChanged && MathF.Max(deltaX, deltaY) <= DeadZonePixels)
        {
            return new StabilizedOverlayPointer(nextTexture, nextContent, false);
        }

        _texturePoint = nextTexture;
        _contentPoint = nextContent;
        _pressed = pressed;
        _lastUpdateAt = now;
        return new StabilizedOverlayPointer(_texturePoint, _contentPoint, true);
    }

    public void Reset()
    {
        _overlayId = null;
        _hand = null;
        _hasPoint = false;
        _pressed = false;
        _lastUpdateAt = default;
    }
}

internal sealed class OverlayPointerRayStabilizer
{
    private long? _overlayId;
    private ETrackedControllerRole? _hand;
    private Vector3f _direction;
    private bool _hasDirection;
    private DateTimeOffset _lastUpdateAt;

    public StabilizedPointerRay Update(
        long overlayId,
        ETrackedControllerRole hand,
        Vector3f source,
        Vector3f measuredDirection,
        DateTimeOffset now,
        int smoothingStrength)
    {
        var direction = measuredDirection.Normalized();
        var targetChanged = !_hasDirection || _overlayId != overlayId || _hand != hand;
        var normalizedStrength = AppConfiguration.NormalizePointerSmoothingStrength(
            smoothingStrength);
        if (targetChanged || normalizedStrength == 0)
        {
            _overlayId = overlayId;
            _hand = hand;
            _direction = direction;
            _hasDirection = true;
            _lastUpdateAt = now;
            return new StabilizedPointerRay(source, direction);
        }

        var elapsedSeconds = Math.Clamp(
            (float)(now - _lastUpdateAt).TotalSeconds,
            0.001f,
            0.1f);
        var timeConstantSeconds = 0.01f + (normalizedStrength * 0.0006f);
        var alpha = 1f - MathF.Exp(-elapsedSeconds / timeConstantSeconds);
        var blended = (_direction * (1f - alpha)) + (direction * alpha);
        _direction = blended.LengthSquared > 0.0001f
            ? blended.Normalized()
            : direction;
        _lastUpdateAt = now;
        return new StabilizedPointerRay(source, _direction);
    }

    public void Reset()
    {
        _overlayId = null;
        _hand = null;
        _direction = default;
        _hasDirection = false;
        _lastUpdateAt = default;
    }
}

internal static class SpatialOverlayInteraction
{
    public const float ContactDistance = 0.05f;
    public const float PointerPitchDegrees = 45f;
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

    public static Vector3f PointerDirection(TrackedHandPose hand)
    {
        var radians = PointerPitchDegrees * (MathF.PI / 180f);
        return ((hand.Forward * -MathF.Cos(radians)) +
                (hand.Up * -MathF.Sin(radians))).Normalized();
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

internal static class OverlayContactSelection
{
    public static HandOverlayContact? Select(
        OverlayContact? left,
        OverlayContact? right,
        long? currentOverlayId,
        ETrackedControllerRole? currentHand)
    {
        if (currentOverlayId is { } overlayId)
        {
            if (currentHand == ETrackedControllerRole.LeftHand &&
                left is { } retainedLeft &&
                retainedLeft.OverlayId == overlayId)
            {
                return new HandOverlayContact(ETrackedControllerRole.LeftHand, retainedLeft);
            }

            if (currentHand == ETrackedControllerRole.RightHand &&
                right is { } retainedRight &&
                retainedRight.OverlayId == overlayId)
            {
                return new HandOverlayContact(ETrackedControllerRole.RightHand, retainedRight);
            }
        }

        if (left is null)
        {
            return right is { } rightOnly
                ? new HandOverlayContact(ETrackedControllerRole.RightHand, rightOnly)
                : null;
        }

        if (right is null || left.Value.Distance <= right.Value.Distance)
        {
            return new HandOverlayContact(ETrackedControllerRole.LeftHand, left.Value);
        }

        return new HandOverlayContact(ETrackedControllerRole.RightHand, right.Value);
    }
}

internal static class OverlayContactHighlights
{
    public static HashSet<long> Select(
        OverlayContact? left,
        OverlayContact? right,
        IEnumerable<OverlayGrab>? grabs = null)
    {
        HashSet<long> overlayIds = [];
        if (left is { } leftContact)
        {
            overlayIds.Add(leftContact.OverlayId);
        }

        if (right is { } rightContact)
        {
            overlayIds.Add(rightContact.OverlayId);
        }

        if (grabs is not null)
        {
            foreach (var grab in grabs)
            {
                overlayIds.Add(grab.OverlayId);
            }
        }

        return overlayIds;
    }
}

internal static class OverlayGrabTransition
{
    public static bool ShouldBegin(
        bool gripPressed,
        bool wasGripPressed,
        OverlayContact? contact,
        OverlayGrab? releasedByOtherHand) =>
        gripPressed &&
        contact is { } currentContact &&
        (!wasGripPressed || releasedByOtherHand?.OverlayId == currentContact.OverlayId);
}
