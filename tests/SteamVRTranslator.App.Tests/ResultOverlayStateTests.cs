using SteamVRTranslator.App.SteamVR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class ResultOverlayStateTests
{
    [Fact]
    public void NewResultReplacesPreviousAndBecomesVisible()
    {
        var state = new ResultOverlayState();
        var first = state.Begin("first");
        state.Complete(first, "result-1", isError: false);
        Assert.True(state.ToggleVisibility());

        var second = state.Begin("second");
        state.Complete(second, "result-2", isError: false);

        var result = Assert.IsType<ResultOverlaySnapshot>(state.Snapshot());
        Assert.Equal("result-2", result.Text);
        Assert.True(result.IsVisible);
        Assert.Equal(0, result.ScrollOffset);
    }

    [Fact]
    public void VisibilityCanBeToggledWithoutDeletingResult()
    {
        var state = new ResultOverlayState();
        state.Begin("persistent");

        Assert.True(state.ToggleVisibility());
        Assert.False(state.IsVisible);
        Assert.True(state.HasResult);
        Assert.True(state.ToggleVisibility());
        Assert.True(state.IsVisible);
    }

    [Fact]
    public void LateStreamingUpdateCannotOverwriteCompletedResult()
    {
        var state = new ResultOverlayState();
        var requestId = state.Begin("pending");
        state.Update(requestId, "partial");
        state.Complete(requestId, "final", isError: false);

        Assert.False(state.Update(requestId, "late partial"));
        var result = Assert.IsType<ResultOverlaySnapshot>(state.Snapshot());
        Assert.Equal("final", result.Text);
        Assert.Equal(ResultStatus.Completed, result.Status);
    }

    [Fact]
    public void ScrollOnlyChangesVisibleResultAndIsClamped()
    {
        var state = new ResultOverlayState();
        state.Begin("long text");

        Assert.True(state.Scroll(5000, 600));
        Assert.Equal(600, state.Snapshot()!.ScrollOffset);
        Assert.True(state.Scroll(-42, 600));
        Assert.Equal(558, state.Snapshot()!.ScrollOffset);
        Assert.True(state.ToggleVisibility());
        Assert.False(state.Scroll(-42, 600));
        Assert.Equal(558, state.Snapshot()!.ScrollOffset);
    }

    [Fact]
    public void ScrollingBackNearTopDoesNotJumpUntilClampedAtOrigin()
    {
        var state = new ResultOverlayState();
        state.Begin("long text");
        state.Scroll(100, 600);

        Assert.True(state.Scroll(-90, 600));
        Assert.Equal(10, state.Snapshot()!.ScrollOffset);
        Assert.True(state.Scroll(-20, 600));
        Assert.Equal(0, state.Snapshot()!.ScrollOffset);
    }
}
