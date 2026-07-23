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

    [Fact]
    public void PointerDirectionUsesControllerForwardWithDownwardPitch()
    {
        var hand = new TrackedHandPose(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1));

        var direction = SpatialOverlayInteraction.PointerDirection(hand);

        Assert.Equal(0, direction.X, 5);
        Assert.Equal(-MathF.Sqrt(0.5f), direction.Y, 5);
        Assert.Equal(-MathF.Sqrt(0.5f), direction.Z, 5);
        Assert.Equal(1, direction.Length, 5);
    }

    [Fact]
    public void ExistingContactHandRemainsStableWhenOtherHandMovesCloser()
    {
        var selected = OverlayContactSelection.Select(
            new OverlayContact(12, 0.04f),
            new OverlayContact(12, 0.01f),
            12,
            Valve.VR.ETrackedControllerRole.LeftHand);

        Assert.NotNull(selected);
        Assert.Equal(Valve.VR.ETrackedControllerRole.LeftHand, selected.Value.Hand);
        Assert.Equal(12, selected.Value.Contact.OverlayId);
    }

    [Fact]
    public void ContactTransfersOnlyAfterCurrentHandLeaves()
    {
        var selected = OverlayContactSelection.Select(
            null,
            new OverlayContact(12, 0.02f),
            12,
            Valve.VR.ETrackedControllerRole.LeftHand);

        Assert.NotNull(selected);
        Assert.Equal(Valve.VR.ETrackedControllerRole.RightHand, selected.Value.Hand);
        Assert.Equal(12, selected.Value.Contact.OverlayId);
    }

    [Fact]
    public void ContactHighlightsIncludeDifferentOverlaysTouchedByBothHands()
    {
        var highlighted = OverlayContactHighlights.Select(
            new OverlayContact(12, 0.02f),
            new OverlayContact(27, 0.03f));

        Assert.Equal(2, highlighted.Count);
        Assert.Contains(12, highlighted);
        Assert.Contains(27, highlighted);
    }

    [Fact]
    public void ContactHighlightsDeduplicateBothHandsTouchingTheSameOverlay()
    {
        var highlighted = OverlayContactHighlights.Select(
            new OverlayContact(12, 0.02f),
            new OverlayContact(12, 0.03f));

        Assert.Single(highlighted);
        Assert.Contains(12, highlighted);
    }

    [Fact]
    public void ContactHighlightsIncludeGrabbedOverlayWithoutGeometricContact()
    {
        var grab = new OverlayGrab(
            42,
            Valve.VR.ETrackedControllerRole.LeftHand,
            Plane,
            new TrackedHandPose(
                default,
                new Vector3f(1, 0, 0),
                new Vector3f(0, 1, 0),
                new Vector3f(0, 0, 1)));

        var highlighted = OverlayContactHighlights.Select(null, null, [grab]);

        Assert.Single(highlighted);
        Assert.Contains(42, highlighted);
    }

    [Fact]
    public void HeldGripCanAcquireOverlayReleasedByOtherHandInSameFrame()
    {
        var released = new OverlayGrab(
            42,
            Valve.VR.ETrackedControllerRole.LeftHand,
            Plane,
            new TrackedHandPose(
                default,
                new Vector3f(1, 0, 0),
                new Vector3f(0, 1, 0),
                new Vector3f(0, 0, 1)));

        Assert.True(OverlayGrabTransition.ShouldBegin(
            gripPressed: true,
            wasGripPressed: true,
            contact: new OverlayContact(42, 0.01f),
            releasedByOtherHand: released));
        Assert.False(OverlayGrabTransition.ShouldBegin(
            gripPressed: true,
            wasGripPressed: true,
            contact: new OverlayContact(43, 0.01f),
            releasedByOtherHand: released));
    }

    [Fact]
    public void GrabStateAllowsDifferentOverlaysOnBothHands()
    {
        var state = new OverlayGrabState();
        var pose = new TrackedHandPose(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1));

        Assert.True(state.TryBegin(new OverlayGrab(
            10,
            Valve.VR.ETrackedControllerRole.LeftHand,
            Plane,
            pose)));
        Assert.True(state.TryBegin(new OverlayGrab(
            20,
            Valve.VR.ETrackedControllerRole.RightHand,
            Plane,
            pose)));

        Assert.Equal(2, state.Count);
        Assert.True(state.HasMultiple);
        Assert.True(state.ContainsOverlay(10));
        Assert.True(state.ContainsOverlay(20));
        Assert.Null(state.Single);
    }

    [Fact]
    public void GrabStateRejectsSameOverlayForBothHandsAndRestoresSingleAfterRelease()
    {
        var state = new OverlayGrabState();
        var pose = new TrackedHandPose(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1));

        Assert.True(state.TryBegin(new OverlayGrab(
            10,
            Valve.VR.ETrackedControllerRole.LeftHand,
            Plane,
            pose)));
        Assert.False(state.TryBegin(new OverlayGrab(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            Plane,
            pose)));
        Assert.True(state.TryBegin(new OverlayGrab(
            20,
            Valve.VR.ETrackedControllerRole.RightHand,
            Plane,
            pose)));

        Assert.True(state.Release(Valve.VR.ETrackedControllerRole.LeftHand, out var released));
        Assert.Equal(10, released.OverlayId);
        Assert.False(state.HasMultiple);
        Assert.Equal(20, state.Single?.OverlayId);
    }

    [Fact]
    public void PointerStabilizerSuppressesJitterAndRateLimitsHighResolutionUpdates()
    {
        var stabilizer = new OverlayPointerStabilizer();
        var now = DateTimeOffset.UtcNow;
        var first = stabilizer.Update(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.5f, 0.5f),
            new NormalizedPoint(0.5f, 0.5f),
            false,
            now,
            OverlayPointerStabilizer.HighResolutionUpdateInterval);
        var rateLimited = stabilizer.Update(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.7f, 0.7f),
            new NormalizedPoint(0.7f, 0.7f),
            false,
            now.AddMilliseconds(10),
            OverlayPointerStabilizer.HighResolutionUpdateInterval);
        var jitter = stabilizer.Update(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.5003f, 0.5003f),
            new NormalizedPoint(0.5003f, 0.5003f),
            false,
            now.AddMilliseconds(60),
            OverlayPointerStabilizer.HighResolutionUpdateInterval);

        Assert.True(first.Changed);
        Assert.False(rateLimited.Changed);
        Assert.False(jitter.Changed);
        Assert.Equal(new NormalizedPoint(0.5003f, 0.5003f), jitter.TexturePoint);
    }

    [Fact]
    public void PointerStabilizerPublishesPressStateImmediately()
    {
        var stabilizer = new OverlayPointerStabilizer();
        var now = DateTimeOffset.UtcNow;
        _ = stabilizer.Update(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.5f, 0.5f),
            new NormalizedPoint(0.5f, 0.5f),
            false,
            now,
            OverlayPointerStabilizer.HighResolutionUpdateInterval);

        var pressed = stabilizer.Update(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.5f, 0.5f),
            new NormalizedPoint(0.5f, 0.5f),
            true,
            now.AddMilliseconds(1),
            OverlayPointerStabilizer.HighResolutionUpdateInterval);

        Assert.True(pressed.Changed);
    }

    [Fact]
    public void PointerSmoothingStrengthFiltersTheControllerRayInsteadOfWindowCoordinates()
    {
        var now = DateTimeOffset.UtcNow;
        var disabled = new OverlayPointerRayStabilizer();
        var maximum = new OverlayPointerRayStabilizer();
        foreach (var stabilizer in new[] { disabled, maximum })
        {
            _ = stabilizer.Update(
                10,
                Valve.VR.ETrackedControllerRole.RightHand,
                new Vector3f(0f, 1f, 0f),
                new Vector3f(0f, 0f, -1f),
                now,
                smoothingStrength: 0);
        }

        var raw = disabled.Update(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            new Vector3f(0.1f, 1f, 0f),
            new Vector3f(0f, -1f, 0f),
            now.AddMilliseconds(20),
            smoothingStrength: 0);
        var smoothed = maximum.Update(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            new Vector3f(0.1f, 1f, 0f),
            new Vector3f(0f, -1f, 0f),
            now.AddMilliseconds(20),
            smoothingStrength: 100);

        Assert.Equal(new Vector3f(0.1f, 1f, 0f), raw.Source);
        Assert.Equal(new Vector3f(0.1f, 1f, 0f), smoothed.Source);
        Assert.Equal(-1f, raw.Direction.Y, 5);
        Assert.True(smoothed.Direction.Y < 0f);
        Assert.True(smoothed.Direction.Z < 0f);
    }

    [Fact]
    public void PointerStabilizerResetDoesNotPullAReenteredRayTowardTheOldPoint()
    {
        var stabilizer = new OverlayPointerStabilizer();
        var now = DateTimeOffset.UtcNow;
        _ = stabilizer.Update(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.1f, 0.1f),
            new NormalizedPoint(0.1f, 0.1f),
            false,
            now,
            OverlayPointerStabilizer.DefaultUpdateInterval);

        stabilizer.Reset();
        var reentered = stabilizer.Update(
            10,
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.9f, 0.9f),
            new NormalizedPoint(0.9f, 0.9f),
            false,
            now.AddMilliseconds(20),
            OverlayPointerStabilizer.DefaultUpdateInterval);

        Assert.Equal(0.9f, reentered.TexturePoint.X, 5);
        Assert.Equal(0.9f, reentered.TexturePoint.Y, 5);
    }

    [Fact]
    public void PointerDirectionMatchesTheFixedControllerLocalFortyFiveDegreeRay()
    {
        var hand = new TrackedHandPose(
            new Vector3f(0f, 1f, 0f),
            new Vector3f(1f, 0f, 0f),
            new Vector3f(0f, 1f, 0f),
            new Vector3f(0f, 0f, 1f));

        var direction = SpatialOverlayInteraction.PointerDirection(hand);
        var expected = MathF.Sqrt(0.5f);

        Assert.Equal(0f, direction.X, 5);
        Assert.Equal(-expected, direction.Y, 5);
        Assert.Equal(-expected, direction.Z, 5);
        Assert.Equal(1f, direction.LengthSquared, 5);
    }

    [Fact]
    public void SwipeStartsOnlyAfterAPrimarilyVerticalEightPixelDrag()
    {
        var pressed = new NormalizedPoint(0.5f, 0.5f);

        Assert.False(OverlaySwipeGesture.ShouldStart(
            pressed,
            new NormalizedPoint(0.5f, 0.486f),
            viewportWidth: 1000,
            viewportHeight: 500));
        Assert.False(OverlaySwipeGesture.ShouldStart(
            pressed,
            new NormalizedPoint(0.488f, 0.482f),
            viewportWidth: 1000,
            viewportHeight: 500));
        Assert.True(OverlaySwipeGesture.ShouldStart(
            pressed,
            new NormalizedPoint(0.496f, 0.482f),
            viewportWidth: 1000,
            viewportHeight: 500));
    }

    [Fact]
    public void SwipeOffsetMakesContentFollowThePointerDirection()
    {
        var dragUp = OverlaySwipeGesture.CalculateOffsetDelta(
            new NormalizedPoint(0.5f, 0.6f),
            new NormalizedPoint(0.5f, 0.4f),
            viewportHeight: 500);
        var dragDown = OverlaySwipeGesture.CalculateOffsetDelta(
            new NormalizedPoint(0.5f, 0.4f),
            new NormalizedPoint(0.5f, 0.6f),
            viewportHeight: 500);

        Assert.Equal(100, dragUp, 3);
        Assert.Equal(-100, dragDown, 3);
    }

    [Fact]
    public void CloseHoldCompletesAtConfiguredRuntimeThreshold()
    {
        var duration = SteamVrTranslationRuntime.SingleOverlayCloseHoldDuration;
        var tracker = new OverlayCloseHoldTracker(duration);
        var now = DateTimeOffset.UtcNow;
        tracker.Press(42, now);

        var halfway = tracker.Update(now.AddTicks(duration.Ticks / 2));
        var completed = tracker.Update(now.Add(duration));
        var repeated = tracker.Update(now.Add(duration).AddMilliseconds(100));

        Assert.Equal(2d / 3d, duration.TotalSeconds, 6);
        Assert.Equal(42, halfway.OverlayId);
        Assert.Equal(0.5, halfway.Progress, 3);
        Assert.False(halfway.CompletedNow);
        Assert.True(completed.CompletedNow);
        Assert.False(repeated.CompletedNow);
        Assert.True(tracker.Release().Completed);
    }

    [Fact]
    public void CloseHoldReleaseBeforeThresholdCancelsTarget()
    {
        var tracker = new OverlayCloseHoldTracker(
            SteamVrTranslationRuntime.SingleOverlayCloseHoldDuration);
        var now = DateTimeOffset.UtcNow;
        tracker.Press(7, now);
        _ = tracker.Update(now.AddMilliseconds(500));

        var release = tracker.Release();

        Assert.Equal(7, release.OverlayId);
        Assert.False(release.Completed);
        Assert.False(tracker.IsPressed);
        Assert.Null(tracker.OverlayId);
    }
}
