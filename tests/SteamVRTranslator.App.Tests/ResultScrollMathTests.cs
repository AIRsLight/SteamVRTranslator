using SteamVRTranslator.App.SteamVR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class ResultScrollMathTests
{
    [Fact]
    public void FullAxisProducesContinuousTimeBasedMovement()
    {
        var delta = ResultScrollMath.CalculateDelta(1, false, TimeSpan.FromMilliseconds(16));

        Assert.InRange(delta, 5.0, 5.2);
    }

    [Fact]
    public void InversionFlipsDirection()
    {
        var normal = ResultScrollMath.CalculateDelta(0.8, false, TimeSpan.FromMilliseconds(20));
        var inverted = ResultScrollMath.CalculateDelta(0.8, true, TimeSpan.FromMilliseconds(20));

        Assert.Equal(-normal, inverted, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(-0.18)]
    [InlineData(0.23)]
    public void DeadZoneSuppressesJitter(double axis)
    {
        Assert.Equal(0, ResultScrollMath.CalculateDelta(axis, false, TimeSpan.FromMilliseconds(16)));
    }

    [Fact]
    public void NeutralMatchesScrollDeadZone()
    {
        Assert.True(ResultScrollMath.IsNeutral(0.24));
        Assert.False(ResultScrollMath.IsNeutral(0.25));
    }
}
