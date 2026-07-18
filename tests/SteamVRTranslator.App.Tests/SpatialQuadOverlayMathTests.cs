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
    public void TextureCoordinatesMapIntoCenteredContent()
    {
        Assert.True(SpatialQuadOverlayMath.TryMapTextureToContent(
            Plane,
            new NormalizedPoint(0.5f, 0.5f),
            out var center));
        Assert.Equal(0.5f, center.X, 5);
        Assert.Equal(0.5f, center.Y, 5);

        Assert.True(SpatialQuadOverlayMath.TryMapTextureToContent(
            Plane,
            new NormalizedPoint(0.1f, 0.3f),
            out var topLeft));
        Assert.Equal(0, topLeft.X, 5);
        Assert.Equal(0, topLeft.Y, 5);
    }

    [Theory]
    [InlineData(0.05f, 0.5f)]
    [InlineData(0.95f, 0.5f)]
    [InlineData(0.5f, 0.1f)]
    [InlineData(0.5f, 0.9f)]
    public void TransparentMarginsDoNotReceivePointerInput(float x, float y)
    {
        Assert.False(SpatialQuadOverlayMath.TryMapTextureToContent(
            Plane,
            new NormalizedPoint(x, y),
            out _));
    }
}
