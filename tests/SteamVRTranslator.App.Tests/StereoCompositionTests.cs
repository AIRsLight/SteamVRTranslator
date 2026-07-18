using SteamVRTranslator.App.Capture;
using Valve.VR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SingleEyeCaptureTests
{
    [Theory]
    [InlineData("left-eye", EVREye.Eye_Left)]
    [InlineData("right-eye", EVREye.Eye_Right)]
    [InlineData("primary-eye", EVREye.Eye_Left)]
    [InlineData("both-eyes-blend", EVREye.Eye_Left)]
    public void ParsesEyeAndMigratesLegacyModes(string value, EVREye expected)
    {
        Assert.Equal(expected, SteamVrCompositorCaptureService.ParseCaptureEye(value));
    }
}
