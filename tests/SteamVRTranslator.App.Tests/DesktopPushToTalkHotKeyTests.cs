using SteamVRTranslator.App.Input;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class DesktopPushToTalkHotKeyTests
{
    [Theory]
    [InlineData(0x41)]
    [InlineData(0x14)]
    [InlineData(0x77)]
    [InlineData(0xA2)]
    [InlineData(0xA3)]
    public void CapturedKeyboardKeysHaveReadableNames(int virtualKey)
    {
        var name = DesktopPushToTalkHotKey.GetDisplayName(virtualKey);

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.DoesNotContain("0x", name, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 0xA2)]
    [InlineData(1, 1)]
    [InlineData(0x41, 0x41)]
    [InlineData(254, 254)]
    [InlineData(255, 0xA2)]
    public void VirtualKeyNormalizationKeepsCapturedValues(int configured, int expected)
    {
        Assert.Equal(expected, DesktopPushToTalkHotKey.NormalizeVirtualKey(configured));
    }

    [Fact]
    public void LeftAndRightModifierKeysRemainDistinguishable()
    {
        Assert.Equal("Left Ctrl", DesktopPushToTalkHotKey.GetDisplayName(0xA2));
        Assert.Equal("Right Ctrl", DesktopPushToTalkHotKey.GetDisplayName(0xA3));
        Assert.Equal("Left Shift", DesktopPushToTalkHotKey.GetDisplayName(0xA0));
        Assert.Equal("Right Shift", DesktopPushToTalkHotKey.GetDisplayName(0xA1));
    }
}
