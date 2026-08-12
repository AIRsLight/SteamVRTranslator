using SteamVRTranslator.App.Speech;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class WasapiErrorClassifierTests
{
    [Fact]
    public void DetectsAccessDeniedAtTopLevel()
    {
        Assert.True(WasapiErrorClassifier.IsMicrophoneAccessDenied(
            new UnauthorizedAccessException("Access denied.")));
    }

    [Fact]
    public void DetectsAccessDeniedInNestedAndAggregateExceptions()
    {
        var exception = new AggregateException(
            new InvalidOperationException("Other failure."),
            new InvalidOperationException(
                "WASAPI initialization failed.",
                new UnauthorizedAccessException("Access denied.")));

        Assert.True(WasapiErrorClassifier.IsMicrophoneAccessDenied(exception));
    }

    [Fact]
    public void DoesNotTreatOtherAudioFailuresAsPrivacyDenials()
    {
        Assert.False(WasapiErrorClassifier.IsMicrophoneAccessDenied(
            new InvalidOperationException("The selected device is unavailable.")));
    }
}
