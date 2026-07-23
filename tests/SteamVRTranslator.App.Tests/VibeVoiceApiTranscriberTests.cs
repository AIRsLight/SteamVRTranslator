using SteamVRTranslator.App.Subtitles;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class VibeVoiceApiTranscriberTests
{
    [Fact]
    public void ParseResponsePreservesNativeSpeakerSegments()
    {
        const string payload = """
        {
          "text": "Hello. World.",
          "segments": [
            { "start": 0.1, "end": 1.8, "speaker": "A", "text": "Hello." },
            { "start": 2.0, "end": 3.4, "speaker": "B", "text": "World." }
          ]
        }
        """;

        var result = VibeVoiceApiTranscriber.ParseResponse(payload);

        Assert.Equal("Hello. World.", result.Text);
        Assert.Collection(
            result.Segments,
            first =>
            {
                Assert.Equal("A", first.Speaker);
                Assert.Equal(0.1, first.StartSeconds);
                Assert.Equal("Hello.", first.Text);
            },
            second =>
            {
                Assert.Equal("B", second.Speaker);
                Assert.Equal(3.4, second.EndSeconds);
                Assert.Equal("World.", second.Text);
            });
    }

    [Fact]
    public void ParseResponseHandlesCrispAsrNestedVibeVoiceJson()
    {
        const string payload = """
        {
          "text": "[{\"STart\":0.0,\"ENd\":2.5,\"SPeaker\":0,\"Content\":\"你好。\"},{\"STart\":2.5,\"ENd\":4.0,\"SPeaker\":1,\"Content\":\"こんにちは。\"}].",
          "segments": [
            {
              "id": 0,
              "start": 0.0,
              "end": 4.0,
              "text": "[{\"STart\":0.0,\"ENd\":2.5,\"SPeaker\":0,\"Content\":\"你好。\"},{\"STart\":2.5,\"ENd\":4.0,\"SPeaker\":1,\"Content\":\"こんにちは。\"}]."
            }
          ]
        }
        """;

        var result = VibeVoiceApiTranscriber.ParseResponse(payload);

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("0", result.Segments[0].Speaker);
        Assert.Equal("你好。 こんにちは。", result.Text);
    }

    [Fact]
    public void ParseResponseDropsNonSpeechMarkers()
    {
        const string payload = """
        { "text": "", "segments": [{ "start": 0, "end": 1, "speaker": "A", "text": "[Silence]" }] }
        """;

        var result = VibeVoiceApiTranscriber.ParseResponse(payload);

        Assert.Empty(result.Segments);
    }
}
