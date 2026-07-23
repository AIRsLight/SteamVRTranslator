using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Translation;
using SteamVRTranslator.Core.Geometry;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class ResultOverlayLayoutTests
{
    private static readonly SpatialSelectionPlane Source = new(
        default,
        new Vector3f(1, 0, 0),
        new Vector3f(0, 1, 0),
        new Vector3f(0, 0, 1),
        0.72f,
        0.24f,
        0.82f,
        default,
        default,
        0);

    [Fact]
    public void CustomCommandUsesFixedPortraitTabletDimensions()
    {
        var result = ResultOverlayLayout.ApplyModeDimensions(
            Source,
            AssistantRequestMode.CustomCommand);

        Assert.Equal(ResultOverlayLayout.ChatWidthMeters, result.Width);
        Assert.Equal(ResultOverlayLayout.ChatHeightMeters, result.Height);
        Assert.True(result.Height > result.Width);
        Assert.InRange(result.Width / result.Height, 0.66f, 0.69f);
    }

    [Theory]
    [InlineData(AssistantRequestMode.Translate)]
    [InlineData(AssistantRequestMode.LayoutTranslate)]
    public void OtherResultModesKeepTheCaptureDimensions(AssistantRequestMode mode)
    {
        var result = ResultOverlayLayout.ApplyModeDimensions(Source, mode);

        Assert.Equal(Source.Width, result.Width);
        Assert.Equal(Source.Height, result.Height);
    }
}
