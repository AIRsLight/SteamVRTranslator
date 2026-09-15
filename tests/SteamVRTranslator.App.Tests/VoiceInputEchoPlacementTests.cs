using SteamVRTranslator.App.SteamVR;
using Valve.VR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class VoiceInputEchoPlacementTests
{
    [Fact]
    public void ResizingKeepsTheTopLeftCornerAndDoesNotAccumulateDrift()
    {
        var placement = new VoiceInputEchoPlacement();
        Assert.True(placement.TryPlace(Pose(0, 1.7f, 0), 0.6f, 0.2f));
        var original = placement.TransformForSize(0.6f, 0.2f);
        var grown = placement.TransformForSize(1.6f, 0.5f);
        Assert.Equal(original.m3 - 0.3f, grown.m3 - 0.8f, 5);
        Assert.Equal(original.m7 + 0.1f, grown.m7 + 0.25f, 5);
        Assert.Equal(original.m11, grown.m11);
        Assert.Equal(original, placement.TransformForSize(0.6f, 0.2f));
    }

    [Fact]
    public void HeadMovementAndRepeatedInputKeepTheExistingWorldAnchor()
    {
        var placement = new VoiceInputEchoPlacement();
        Assert.True(placement.TryPlace(Pose(1, 1.7f, 2)));
        var original = placement.Transform!.Value;
        Assert.Equal(1, original.m3);
        Assert.Equal(1.5f, original.m7, 5);
        Assert.Equal(0.8f, original.m11, 5);
        var moved = Pose(8, 2.1f, -5);
        moved.mDeviceToAbsoluteTracking.m0 = 0;
        moved.mDeviceToAbsoluteTracking.m2 = 1;
        moved.mDeviceToAbsoluteTracking.m8 = -1;
        moved.mDeviceToAbsoluteTracking.m10 = 0;
        Assert.True(placement.TryPlace(moved));
        Assert.Equal(original, placement.Transform!.Value);
        // An ongoing display also keeps its world position during temporary tracking loss.
        Assert.True(placement.TryPlace(default));
        Assert.Equal(original, placement.Transform!.Value);

        placement.Reset();
        Assert.True(placement.TryPlace(moved));
        Assert.Equal(6.8f, placement.Transform!.Value.m3, 5);
        Assert.Equal(-5, placement.Transform.Value.m11);
    }

    [Fact]
    public void FirstDisplayWaitsForAValidConnectedPose()
    {
        var placement = new VoiceInputEchoPlacement();
        Assert.False(placement.TryPlace(default));
        Assert.Null(placement.Transform);
        var pose = Pose(0, 1.7f, 0);
        pose.bDeviceIsConnected = false;
        Assert.False(placement.TryPlace(pose));
        pose.bDeviceIsConnected = true;
        pose.mDeviceToAbsoluteTracking.m3 = float.NaN;
        Assert.False(placement.TryPlace(pose));
        Assert.Null(placement.Transform);
        Assert.True(placement.TryPlace(Pose(0, 1.7f, 0)));
    }

    [Fact]
    public void LookingUpStillPlacesAnUprightPanelWithoutPitchOrRoll()
    {
        var placement = new VoiceInputEchoPlacement();
        var pose = Pose(0, 1.7f, 0);
        pose.mDeviceToAbsoluteTracking.m5 = 0;
        pose.mDeviceToAbsoluteTracking.m6 = -1;
        pose.mDeviceToAbsoluteTracking.m9 = 1;
        pose.mDeviceToAbsoluteTracking.m10 = 0;
        Assert.True(placement.TryPlace(pose));
        var transform = placement.Transform!.Value;
        Assert.Equal(1, transform.m5);
        Assert.Equal(0, transform.m1);
        Assert.Equal(0, transform.m9);
        Assert.Equal(-1.2f, transform.m11, 5);
        Assert.Equal(1.5f, transform.m7, 5);
    }

    private static TrackedDevicePose_t Pose(float x, float y, float z) => new()
    {
        bPoseIsValid = true, bDeviceIsConnected = true,
        mDeviceToAbsoluteTracking = new HmdMatrix34_t { m0 = 1, m5 = 1, m10 = 1, m3 = x, m7 = y, m11 = z }
    };
}
