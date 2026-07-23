using SteamVRTranslator.App.SteamVR;
using Valve.VR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SteamVrPassiveConnectionTests
{
    [Fact]
    public void OpenVrClientTypeNeverStartsSteamVr()
    {
        Assert.Equal(
            EVRApplicationType.VRApplication_Background,
            SteamVrTranslationRuntime.OpenVrApplicationType);
    }

    [Fact]
    public void RuntimeIsReadyOnlyWhenServerAndCompositorAreRunning()
    {
        Assert.False(SteamVrRuntimeProcessProbe.FromProcessNames([]).IsReady);
        Assert.False(SteamVrRuntimeProcessProbe.FromProcessNames(["vrserver.exe"]).IsReady);
        Assert.False(SteamVrRuntimeProcessProbe.FromProcessNames(["vrcompositor"]).IsReady);
        Assert.True(SteamVrRuntimeProcessProbe.FromProcessNames(
            ["VRServer.exe", "vrcompositor.exe"]).IsReady);
    }

    [Fact]
    public void RuntimeSnapshotReportsPartialShutdown()
    {
        var snapshot = SteamVrRuntimeProcessProbe.FromProcessNames(["vrserver.exe"]);

        Assert.True(snapshot.AnyRunning);
        Assert.False(snapshot.IsReady);
    }

    [Fact]
    public void AlreadyRunningRuntimeCanAttachBeforeApplicationIdentityIndexRefreshes()
    {
        var registration = new SteamVrApplicationRegistrationStatus(
            EVRApplicationError.None,
            EVRApplicationError.UnknownApplication);

        Assert.True(registration.CanContinue);
    }

    [Fact]
    public void ManifestRegistrationFailureStillBlocksConnection()
    {
        var registration = new SteamVrApplicationRegistrationStatus(
            EVRApplicationError.InvalidManifest,
            EVRApplicationError.None);

        Assert.False(registration.CanContinue);
    }
}
