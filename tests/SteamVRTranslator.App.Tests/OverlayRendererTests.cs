using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class OverlayRendererTests
{
    [Fact]
    public void ArmedSelectionRendersAnEntryHint()
    {
        var renderer = new OverlayRenderer();
        var snapshot = new SelectionSnapshot(
            SelectionState.Armed,
            false,
            false,
            null,
            null,
            null,
            DateTimeOffset.Now);

        var pixels = renderer.RenderSelection(snapshot);

        Assert.Contains(
            Enumerable.Range(0, pixels.Length / 4),
            pixel => pixels[(pixel * 4) + 3] > 0);
    }

    [Fact]
    public void SingleResultUsesCapturedPlaneAspectRatioAndIsCentered()
    {
        var plane = new SpatialSelectionPlane(
            new Vector3f(0, 1.2f, -0.5f),
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.2f,
            0.1f,
            0.23f,
            new NormalizedPoint(0.1f, 0.2f),
            new NormalizedPoint(0.9f, 0.8f),
            0);

        var panel = OverlayRenderer.CalculateResultPanel(plane);

        Assert.Equal(2, panel.Width / panel.Height, 5);
        Assert.Equal(OverlayRenderer.Width / 2d, panel.Left + (panel.Width / 2), 4);
        Assert.Equal(OverlayRenderer.Height / 2d, panel.Top + (panel.Height / 2), 4);
    }

    [Fact]
    public void SelectionHintStaysImmediatelyAboveAndWithinFrameWidth()
    {
        var frame = new System.Windows.Rect(90, 120, 280, 240);

        var hint = OverlayRenderer.CalculateSelectionHintBounds(frame);

        Assert.False(hint.IsEmpty);
        Assert.True(hint.Bottom <= frame.Top);
        Assert.Equal(5, frame.Top - hint.Bottom, 5);
        Assert.True(hint.Width <= frame.Width);
        Assert.True(hint.Left >= 0);
        Assert.True(hint.Right <= OverlayRenderer.Width);
    }

    [Fact]
    public void SelectionHintIsOmittedInsteadOfOverlappingFrame()
    {
        var frame = new System.Windows.Rect(90, 12, 280, 240);

        var hint = OverlayRenderer.CalculateSelectionHintBounds(frame);

        Assert.True(hint.IsEmpty);
    }

    [Fact]
    public void NativeTextBoxScrollCanReturnExactlyToFirstLine()
    {
        var renderer = new OverlayRenderer();
        var state = new ResultOverlayState();
        var requestId = state.Begin("loading");
        state.Complete(
            requestId,
            string.Join('\n', Enumerable.Range(1, 80).Select(index => $"line {index:D2} native scrolling")),
            isError: false);
        var plane = new SpatialSelectionPlane(
            new Vector3f(0, 1.2f, -0.5f),
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.42f,
            0.16f,
            0.42f,
            new NormalizedPoint(0.1f, 0.2f),
            new NormalizedPoint(0.9f, 0.8f),
            0);

        var top = renderer.RenderResults(state.Snapshot()!, plane);
        Assert.True(top.MaximumScrollOffset > 0);
        Assert.True(state.Scroll(top.MaximumScrollOffset, top.MaximumScrollOffset));
        var bottom = renderer.RenderResults(state.Snapshot()!, plane);
        Assert.False(top.Pixels.SequenceEqual(bottom.Pixels));

        Assert.True(state.Scroll(-double.MaxValue, bottom.MaximumScrollOffset));
        var topAgain = renderer.RenderResults(state.Snapshot()!, plane);

        Assert.Equal(0, state.Snapshot()!.ScrollOffset);
        Assert.True(top.Pixels.SequenceEqual(topAgain.Pixels));
    }

    [Fact]
    public void PointerIsCompositedIntoCaptureTexture()
    {
        var renderer = new OverlayRenderer();
        var plane = new SpatialSelectionPlane(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.4f,
            0.3f,
            0.46f,
            default,
            default,
            0);
        var state = new ResultOverlayState();
        var requestId = state.Begin("pointer test");
        state.Complete(requestId, "pointer test", false);

        var plain = renderer.RenderResults(state.Snapshot()!, plane).Pixels;
        var pointer = new OverlayPointerVisual(
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.5f, 0.5f),
            new NormalizedPoint(0.5f, 0.5f),
            false);
        var withPointer = renderer.RenderResults(state.Snapshot()!, plane, pointer: pointer).Pixels;

        Assert.False(plain.SequenceEqual(withPointer));
    }
}
