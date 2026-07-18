using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SpatialOverlayInteractionTests
{
    private static readonly SpatialSelectionPlane Plane = new(
        new Vector3f(0, 1, -1),
        new Vector3f(1, 0, 0),
        new Vector3f(0, 1, 0),
        new Vector3f(0, 0, 1),
        0.6f,
        0.4f,
        0.7f,
        new NormalizedPoint(0.1f, 0.1f),
        new NormalizedPoint(0.9f, 0.9f),
        0);

    [Fact]
    public void ContactRequiresProximityAndPointInsidePanel()
    {
        Assert.True(SpatialOverlayInteraction.TryContact(
            Plane,
            new Vector3f(0.1f, 1.1f, -0.97f),
            out _));
        Assert.False(SpatialOverlayInteraction.TryContact(
            Plane,
            new Vector3f(0.5f, 1.1f, -0.97f),
            out _));
        Assert.False(SpatialOverlayInteraction.TryContact(
            Plane,
            new Vector3f(0.1f, 1.1f, -0.8f),
            out _));
    }

    [Fact]
    public void MoveAppliesHandTranslationAndRotationWithoutSnapping()
    {
        var initialHand = new TrackedHandPose(
            new Vector3f(0.1f, 1f, -1f),
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1));
        var grab = new OverlayGrab(1, Valve.VR.ETrackedControllerRole.RightHand, Plane, initialHand);
        var currentHand = new TrackedHandPose(
            new Vector3f(0.3f, 1.2f, -0.8f),
            new Vector3f(0, 0, -1),
            new Vector3f(0, 1, 0),
            new Vector3f(1, 0, 0));

        var moved = SpatialOverlayInteraction.Move(grab, currentHand);

        Assert.Equal(new Vector3f(0.3f, 1.2f, -0.7f), moved.Center);
        Assert.Equal(new Vector3f(0, 0, -1), moved.Right);
        Assert.Equal(Plane.Up, moved.Up);
        Assert.Equal(new Vector3f(1, 0, 0), moved.Normal);
    }
}
