using System.Text;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Output;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class VrChatOscOutputTests
{
    [Fact]
    public void ChatboxPacketPreservesUtf8TextAndPadding()
    {
        var packet = OscChatboxMessage.Create("你好 VRChat", sendImmediately: true);

        Assert.Equal(0, packet.Length % 4);
        Assert.Contains("/chatbox/input", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
        Assert.Contains(",sT", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
        Assert.Contains("你好 VRChat", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
    }

    [Fact]
    public void SubmittedChatboxUpdateUsesImmediateSendWithoutNotificationSound()
    {
        var packet = OscChatboxMessage.Create(
            "updated",
            sendImmediately: true,
            notificationSfx: false);

        Assert.Contains(",sTF", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkerCountsUnicodeRunesInsteadOfUtf16CodeUnits()
    {
        var chunks = TextChunker.Split("A😀BC", 2);

        Assert.Equal(["A😀", "BC"], chunks);
    }

    [Fact]
    public void LongChatboxTextIsPlannedAsPacedStream()
    {
        var interval = TimeSpan.FromMilliseconds(1100);

        var chunks = OscChatboxStream.Create("A😀BC", 2, interval);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("A😀", chunks[0].Text);
        Assert.Equal(TimeSpan.Zero, chunks[0].DelayBefore);
        Assert.Equal("BC", chunks[1].Text);
        Assert.Equal(interval, chunks[1].DelayBefore);
    }

    [Fact]
    public void ShortChatboxTextIsSentImmediately()
    {
        var chunks = OscChatboxStream.Create(
            "short text",
            144,
            TimeSpan.FromMilliseconds(
                VrChatVoiceInputConfiguration.DefaultStreamingChunkIntervalMilliseconds));

        var chunk = Assert.Single(chunks);
        Assert.Equal(TimeSpan.Zero, chunk.DelayBefore);
    }

    [Fact]
    public void OutputUsesConfiguredStreamingChunkInterval()
    {
        using var output = new VrChatOscOutput(new VrChatVoiceInputConfiguration
        {
            MaxChatboxCharacters = 2,
            StreamingChunkIntervalMilliseconds = 275
        });

        var chunks = output.PlanStream("abcdef");

        Assert.Equal(3, chunks.Count);
        Assert.Equal(TimeSpan.Zero, chunks[0].DelayBefore);
        Assert.Equal(TimeSpan.FromMilliseconds(275), chunks[1].DelayBefore);
        Assert.Equal(TimeSpan.FromMilliseconds(275), chunks[2].DelayBefore);
    }

    [Fact]
    public void PreviewUsesOnlyTheFirstChatboxChunk()
    {
        using var output = new VrChatOscOutput(new VrChatVoiceInputConfiguration
        {
            MaxChatboxCharacters = 3
        });

        Assert.Equal("原文A", output.PlanPreview("原文A后续"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void OriginalThenTranslationFollowsImmediateSettingOnlyForOriginal(
        bool sendImmediately,
        bool expectedOriginalImmediate,
        bool expectedTranslationImmediate)
    {
        var plan = VoiceTranslationOscPlan.Create(
            VoiceTranslationDisplayModes.OriginalThenTranslation,
            sendImmediately);

        Assert.True(plan.ShowOriginal);
        Assert.Equal(expectedOriginalImmediate, plan.SendOriginalImmediately);
        Assert.Equal(expectedTranslationImmediate, plan.SendTranslationImmediately);
        Assert.Equal(sendImmediately, plan.UpdateSubmittedOriginal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TranslationOnlyFollowsImmediateSettingForTranslation(bool sendImmediately)
    {
        var plan = VoiceTranslationOscPlan.Create(
            VoiceTranslationDisplayModes.TranslationOnly,
            sendImmediately);

        Assert.False(plan.ShowOriginal);
        Assert.False(plan.SendOriginalImmediately);
        Assert.Equal(sendImmediately, plan.SendTranslationImmediately);
        Assert.False(plan.UpdateSubmittedOriginal);
    }

    [Fact]
    public void TailKeepsTheNewestUnicodeRunesForChatboxUpdates()
    {
        Assert.Equal("😀BC", TextChunker.Tail("A😀BC", 3));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public void OutputRejectsInvalidStreamingChunkInterval(int milliseconds)
    {
        var configuration = new VrChatVoiceInputConfiguration
        {
            StreamingChunkIntervalMilliseconds = milliseconds
        };

        Assert.Throws<InvalidOperationException>(() => new VrChatOscOutput(configuration));
    }
}
