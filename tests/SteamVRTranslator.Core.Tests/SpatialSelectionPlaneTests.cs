using SteamVRTranslator.Core.Geometry;
using Xunit;

namespace SteamVRTranslator.Core.Tests;

public sealed class SpatialSelectionPlaneTests
{
    [Fact]
    public void ViewAngleLimitIsStrictEnoughToAvoidSeverePerspectiveCropping()
    {
        Assert.Equal(35f, SpatialSelectionPlane.MaximumViewAngleDegrees);
    }

    [Theory]
    [InlineData(0.09f, 0.30f)]
    [InlineData(0.30f, 0.09f)]
    public void WidthAndHeightAreRejectedIndependentlyWhenTooNarrow(float width, float height)
    {
        var plane = new SpatialSelectionPlane(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            width,
            height,
            MathF.Max(width, height) * 1.15f,
            default,
            default,
            0);

        Assert.False(plane.HasUsableDimensions);
        Assert.False(plane.IsUsable);
    }

    [Fact]
    public void ControllersBecomeOppositeRectangleCorners()
    {
        var left = new Vector3f(-0.30f, 1.10f, -0.80f);
        var right = new Vector3f(0.35f, 1.55f, -1.00f);

        var created = SpatialSelectionPlane.TryCreate(
            new Vector3f(0f, 1.65f, 0f),
            new Vector3f(0f, -0.4f, -1f),
            new Vector3f(1f, 0f, 0f),
            left,
            right,
            out var plane);

        Assert.True(created);
        Assert.True(plane.IsUsable);
        Assert.InRange(MathF.Abs(Vector3f.Dot(right - left, plane.Normal)), 0f, 0.0001f);
        Assert.Equal(left, Reconstruct(plane, plane.LeftPointer), new Vector3fComparer(0.0002f));
        Assert.Equal(right, Reconstruct(plane, plane.RightPointer), new Vector3fComparer(0.0002f));
    }

    [Fact]
    public void NearlyCoincidentControllersCannotCreatePlane()
    {
        Assert.False(SpatialSelectionPlane.TryCreate(
            new Vector3f(0f, 1.6f, 0f),
            new Vector3f(0f, 0f, -1f),
            new Vector3f(1f, 0f, 0f),
            new Vector3f(0f, 1.2f, -0.8f),
            new Vector3f(0.01f, 1.2f, -0.8f),
            out _));
    }

    [Fact]
    public void HorizontalFrameIsRejectedWhenLookingForward()
    {
        var created = SpatialSelectionPlane.TryCreate(
            new Vector3f(0f, 1.6f, 0f),
            new Vector3f(0f, 0f, -1f),
            new Vector3f(1f, 0f, 0f),
            new Vector3f(-0.3f, 1f, -0.2f),
            new Vector3f(0.3f, 1f, 0.2f),
            out var plane);

        Assert.True(created);
        Assert.True(plane.HasUsableDimensions);
        Assert.False(plane.HasUsableOrientation);
        Assert.InRange(plane.ViewAngleDegrees, 89.9f, 90f);
    }

    [Fact]
    public void HorizontalFrameIsAcceptedWhenLookingDown()
    {
        var created = SpatialSelectionPlane.TryCreate(
            new Vector3f(0f, 1.6f, 0f),
            new Vector3f(0f, -1f, 0f),
            new Vector3f(1f, 0f, 0f),
            new Vector3f(-0.3f, 1f, -0.2f),
            new Vector3f(0.3f, 1f, 0.2f),
            out var plane);

        Assert.True(created);
        Assert.True(plane.HasUsableDimensions);
        Assert.True(plane.HasUsableOrientation);
        Assert.InRange(plane.ViewAngleDegrees, 0f, 0.1f);
    }

    [Fact]
    public void HorizontalFrameAxesFollowHeadYawInsteadOfWorldAxes()
    {
        var hmdRight = new Vector3f(0f, 0f, -1f);
        var created = SpatialSelectionPlane.TryCreate(
            new Vector3f(0f, 1.6f, 0f),
            new Vector3f(0f, -1f, 0f),
            hmdRight,
            new Vector3f(-0.2f, 1f, 0.3f),
            new Vector3f(0.2f, 1f, -0.3f),
            out var plane);

        Assert.True(created);
        Assert.True(plane.IsUsable);
        Assert.InRange(Vector3f.Dot(plane.Right, hmdRight), 0.999f, 1f);
    }

    private static Vector3f Reconstruct(SpatialSelectionPlane plane, Core.Selection.NormalizedPoint point)
    {
        var x = (point.X - 0.5f) * plane.OverlayExtent;
        var y = (0.5f - point.Y) * plane.OverlayExtent;
        return plane.Center + (plane.Right * x) + (plane.Up * y);
    }

    private sealed class Vector3fComparer(float tolerance) : IEqualityComparer<Vector3f>
    {
        public bool Equals(Vector3f left, Vector3f right) =>
            (left - right).Length <= tolerance;

        public int GetHashCode(Vector3f value) => value.GetHashCode();
    }
}
