using System.Text.Json;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Output;
using SteamVRTranslator.App.SteamVR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class VoiceInputEchoTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExistingSettingsKeepEchoOptInAndNewSettingsPersistIt()
    {
        Assert.False(JsonSerializer.Deserialize<VrChatVoiceInputConfiguration>("{}")!.TextEchoEnabled);
        var settings = new VrChatVoiceInputConfiguration { TextEchoEnabled = true };
        Assert.True(JsonSerializer.Deserialize<VrChatVoiceInputConfiguration>(JsonSerializer.Serialize(settings))!.TextEchoEnabled);
        var echo = new VoiceInputEchoState();
        var request = echo.Begin();
        echo.Sent(request, new("hello", true, 1, 1, false));
        Assert.Null(echo.Snapshot(Now));
    }

    [Fact]
    public void RepeatedInputReplacesVisibleTextAndRestartsTheHideTimer()
    {
        var echo = new VoiceInputEchoState();
        echo.SetEnabled(true);
        var first = echo.Begin();
        echo.Sent(first, new("previous", true, 1, 1, false));
        echo.Complete(first, Now);
        var firstRevision = echo.Snapshot(Now)!.Revision;
        var second = echo.Begin();
        var recording = echo.Snapshot(Now.AddSeconds(10))!;
        Assert.True(recording.Revision > firstRevision);
        Assert.Equal("Voice.Echo.Recording", recording.StatusKey);
        Assert.Empty(recording.Text);
        Assert.Null(recording.ExpiresAt);
        // A stale callback or timer from the first utterance cannot restore its content.
        echo.Sent(first, new("stale", true, 1, 1, false));
        echo.Complete(first, Now);
        Assert.NotNull(echo.Snapshot(Now.AddSeconds(13)));
        echo.Sent(second, new("new text", true, 1, 1, false));
        echo.Complete(second, Now.AddSeconds(15));
        Assert.Equal("new text", echo.Snapshot(Now.AddSeconds(26))!.Text);
        Assert.Null(echo.Snapshot(Now.AddSeconds(27)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisableOrDisconnectPreventsLateResultsFromReopeningTheEcho(bool disconnect)
    {
        var echo = new VoiceInputEchoState();
        echo.SetEnabled(true);
        var request = echo.Begin();
        if (disconnect) echo.Reset(); else { echo.SetEnabled(false); echo.SetEnabled(true); }
        echo.Update(request, "Voice.Echo.Translating", "late recognition");
        echo.Sent(request, new("late translation", true, 1, 1, true));
        echo.Complete(request, Now);
        Assert.Null(echo.Snapshot(Now));
        echo.Begin();
        Assert.Equal("Voice.Echo.Recording", echo.Snapshot(Now)!.StatusKey);
    }

    [Fact]
    public void EchoTracksChunkAndPreviewSemanticsWithoutExpiringDuringWork()
    {
        var echo = new VoiceInputEchoState();
        echo.SetEnabled(true);
        var request = echo.Begin();
        echo.Update(request, "Voice.Echo.Translating", "recognized");
        Assert.Equal("recognized", echo.Snapshot(Now.AddMinutes(3))!.Text);
        echo.Sent(request, new("first", true, 1, 2, false));
        echo.Sent(request, new("second", true, 2, 2, false));
        var current = echo.Snapshot(Now)!;
        Assert.Equal("second", current.Text);
        Assert.Equal(2, current.ChunkNumber);
        echo.Sent(request, new("translated preview", false, 1, 1, false));
        Assert.Equal("Voice.Echo.Preview", echo.Snapshot(Now)!.StatusKey);
        echo.Sent(request, new("translated update", true, 1, 1, true));
        Assert.Equal("Voice.Echo.Updated", echo.Snapshot(Now)!.StatusKey);
    }
}
