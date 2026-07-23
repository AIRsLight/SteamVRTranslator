using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SpatialQuadOverlayMathTests
{
    private static readonly SpatialSelectionPlane Plane = new(
        new Vector3f(1, 2, 3),
        new Vector3f(1, 0, 0),
        new Vector3f(0, 1, 0),
        new Vector3f(0, 0, 1),
        0.6f,
        0.3f,
        0.75f,
        default,
        default,
        0);

    [Fact]
    public void OpenVrLowerLeftCoordinatesMapToTopLeftTextureCoordinates()
    {
        var topLeft = SpatialQuadOverlayMath.OpenVrToTexturePoint(
            new NormalizedPoint(0.2f, 1f));
        var bottomRight = SpatialQuadOverlayMath.OpenVrToTexturePoint(
            new NormalizedPoint(0.8f, 0f));

        Assert.Equal(new NormalizedPoint(0.2f, 0f), topLeft);
        Assert.Equal(new NormalizedPoint(0.8f, 1f), bottomRight);
    }

    [Fact]
    public void TransformPreservesPlaneBasisAndCenter()
    {
        var transform = SpatialQuadOverlayMath.ToTransform(Plane);

        Assert.Equal(1, transform.m0);
        Assert.Equal(1, transform.m5);
        Assert.Equal(1, transform.m10);
        Assert.Equal(1, transform.m3);
        Assert.Equal(2, transform.m7);
        Assert.Equal(3, transform.m11);
    }

    [Fact]
    public void TextureCoordinatesMapDirectlyIntoAspectMatchedContent()
    {
        var center = SpatialQuadOverlayMath.MapTextureToContent(
            new NormalizedPoint(0.5f, 0.5f));
        Assert.Equal(0.5f, center.X, 5);
        Assert.Equal(0.5f, center.Y, 5);

        var point = SpatialQuadOverlayMath.MapTextureToContent(
            new NormalizedPoint(0.1f, 0.3f));
        Assert.Equal(0.1f, point.X, 5);
        Assert.Equal(0.3f, point.Y, 5);
    }

    [Theory]
    [InlineData(-0.1f, 0.5f, 0f, 0.5f)]
    [InlineData(1.1f, 0.5f, 1f, 0.5f)]
    [InlineData(0.5f, -0.1f, 0.5f, 0f)]
    [InlineData(0.5f, 1.1f, 0.5f, 1f)]
    public void TextureCoordinatesAreClampedAtContentEdges(
        float x,
        float y,
        float expectedX,
        float expectedY)
    {
        var point = SpatialQuadOverlayMath.MapTextureToContent(new NormalizedPoint(x, y));

        Assert.Equal(expectedX, point.X, 5);
        Assert.Equal(expectedY, point.Y, 5);
    }

    [Theory]
    [InlineData(0.25f, 0.75f)]
    [InlineData(-0.20f, 1.20f)]
    public void RayProjectionUsesTheSameTopLeftCoordinatesAsTheRenderedWindow(
        float expectedX,
        float expectedY)
    {
        var target = Plane.Center +
                     (Plane.Right * ((expectedX - 0.5f) * Plane.Width)) +
                     (Plane.Up * ((0.5f - expectedY) * Plane.Height));
        var source = target + (Plane.Normal * 0.5f);

        Assert.True(SpatialQuadOverlayMath.TryProjectRayToPlane(
            source,
            Plane.Normal * -1,
            Plane,
            out var projected,
            out var distance));
        Assert.Equal(expectedX, projected.X, 5);
        Assert.Equal(expectedY, projected.Y, 5);
        Assert.Equal(0.5f, distance, 5);
    }

    [Fact]
    public void BoundedRayProjectionAcceptsEntireTallWindowAndRejectsOutside()
    {
        var plane = new SpatialSelectionPlane(
            new Vector3f(0f, 0f, -1f),
            new Vector3f(1f, 0f, 0f),
            new Vector3f(0f, 1f, 0f),
            new Vector3f(0f, 0f, 1f),
            0.28f,
            0.60f,
            0.60f,
            default,
            default,
            0f);

        Assert.True(SpatialQuadOverlayMath.TryProjectRayInsidePlane(
            default,
            new Vector3f(0f, 0.285f, -1f),
            plane,
            out var upper,
            out _));
        Assert.InRange(upper.Y, 0f, 0.03f);

        Assert.True(SpatialQuadOverlayMath.TryProjectRayInsidePlane(
            default,
            new Vector3f(0f, -0.285f, -1f),
            plane,
            out var lower,
            out _));
        Assert.InRange(lower.Y, 0.97f, 1f);

        Assert.False(SpatialQuadOverlayMath.TryProjectRayInsidePlane(
            default,
            new Vector3f(0f, 0.32f, -1f),
            plane,
            out _,
            out _));
    }

    [Fact]
    public void ChildLayerMapsTextureCoordinatesIntoTheParentPlane()
    {
        var child = SpatialQuadOverlayMath.CreateSquareChildPlane(
            Plane,
            new NormalizedPoint(0.75f, 0.25f),
            OverlayRenderer.PointerLogicalExtent,
            0.001f);

        Assert.Equal(1.15f, child.Center.X, 5);
        Assert.Equal(2.075f, child.Center.Y, 5);
        Assert.Equal(3.001f, child.Center.Z, 5);
        Assert.Equal(0.0375f, child.Width, 5);
        Assert.Equal(child.Width, child.Height, 5);
    }

    [Fact]
    public void PointerLayerUsesTheOpenVrWorldIntersectionWithoutUvReprojection()
    {
        var intersection = new Vector3f(1.12f, 1.91f, 3f);
        var pointer = new OverlayPointerVisual(
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.95f, 0.05f),
            new NormalizedPoint(0.95f, 0.05f),
            false,
            WorldPoint: intersection);

        var child = SpatialQuadOverlayMath.CreatePointerPlane(
            Plane,
            pointer,
            OverlayRenderer.PointerLogicalExtent,
            0.001f);

        Assert.Equal(intersection + (Plane.Normal * 0.001f), child.Center);
        Assert.Equal(0.0375f, child.Width, 5);
        Assert.Equal(child.Width, child.Height, 5);
    }

    [Fact]
    public void WorldIntersectionMapsToTheSubmittedTextureAspectForInput()
    {
        const float physicalHeight = 0.4f;
        var intersection = Plane.Center +
                           (Plane.Right * (0.2f * Plane.Width)) +
                           (Plane.Up * (0.25f * physicalHeight));

        var point = SpatialQuadOverlayMath.WorldPointToTexture(
            Plane,
            intersection,
            physicalHeight);

        Assert.Equal(0.7f, point.X, 5);
        Assert.Equal(0.25f, point.Y, 5);
    }

    [Fact]
    public void WorldIntersectionInputCoordinatesAreClampedToTheVisibleQuad()
    {
        var intersection = Plane.Center +
                           (Plane.Right * Plane.Width) +
                           (Plane.Up * Plane.Height);

        var point = SpatialQuadOverlayMath.WorldPointToTexture(
            Plane,
            intersection,
            Plane.Height);

        Assert.Equal(1f, point.X, 5);
        Assert.Equal(0f, point.Y, 5);
    }

    [Fact]
    public void DirectVideoRegionOccupiesOnlyItsWindowViewport()
    {
        var child = SpatialQuadOverlayMath.CreateChildPlane(
            Plane,
            new DirectOverlayPixelRegion(0.1f, 0.05f, 0.8f, 0.75f),
            -0.0002f);

        Assert.Equal(1f, child.Center.X, 5);
        Assert.Equal(2.0225f, child.Center.Y, 5);
        Assert.Equal(2.9998f, child.Center.Z, 5);
        Assert.Equal(0.48f, child.Width, 5);
        Assert.Equal(0.225f, child.Height, 5);
    }

    [Fact]
    public void PointerRayPlaneConnectsControllerAndOpenVrHitPoint()
    {
        var source = new Vector3f(0f, 1f, 0f);
        var target = new Vector3f(0f, 1f, -0.8f);
        var viewer = new Vector3f(0.2f, 1.6f, 0.2f);

        Assert.True(SpatialQuadOverlayMath.TryCreateRayPlane(
            source,
            target,
            viewer,
            0.0035f,
            out var ray));

        Assert.Equal(new Vector3f(0f, 1f, -0.4f), ray.Center);
        Assert.Equal(0.8f, ray.Width, 5);
        Assert.Equal(0.0035f, ray.Height, 5);
        Assert.True(Vector3f.Dot(ray.Normal, viewer - ray.Center) > 0f);
        Assert.Equal(0f, Vector3f.Dot(ray.Right, ray.Up), 5);
    }

    [Fact]
    public void ParentDepthOrderAlwaysWinsOverFartherChildLayers()
    {
        var farContent = SpatialQuadOverlayMath.CalculateSortOrder(
            110,
            0,
            SpatialOverlayLayer.Content);
        var farPointer = SpatialQuadOverlayMath.CalculateSortOrder(
            110,
            0,
            SpatialOverlayLayer.Pointer);
        var farRay = SpatialQuadOverlayMath.CalculateSortOrder(
            110,
            0,
            SpatialOverlayLayer.Ray);
        var nearContent = SpatialQuadOverlayMath.CalculateSortOrder(
            110,
            1,
            SpatialOverlayLayer.Content);

        Assert.Equal(111u, farContent);
        Assert.Equal(114u, farPointer);
        Assert.Equal(115u, farRay);
        Assert.Equal(117u, nearContent);
        Assert.True(farContent < farPointer);
        Assert.True(farPointer < farRay);
        Assert.True(farPointer < nearContent);
    }
}
