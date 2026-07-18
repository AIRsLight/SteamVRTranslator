using SteamVRTranslator.App.Interaction;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class CommandPressTrackerTests
{
    [Fact]
    public void ShortPressSelectsTranslation()
    {
        var start = DateTimeOffset.UtcNow;
        var tracker = new CommandPressTracker(TimeSpan.FromMilliseconds(350));

        tracker.Press(start);

        Assert.Equal(CommandPressKind.Translate, tracker.Release(start.AddMilliseconds(349)));
    }

    [Fact]
    public void ThresholdSelectsCustomCommandAndActivatesOnce()
    {
        var start = DateTimeOffset.UtcNow;
        var tracker = new CommandPressTracker(TimeSpan.FromMilliseconds(350));
        tracker.Press(start);

        Assert.False(tracker.ActivateLongHold(start.AddMilliseconds(349)));
        Assert.True(tracker.ActivateLongHold(start.AddMilliseconds(350)));
        Assert.False(tracker.ActivateLongHold(start.AddMilliseconds(500)));
        Assert.Equal(CommandPressKind.CustomCommand, tracker.Release(start.AddMilliseconds(500)));
    }
}
