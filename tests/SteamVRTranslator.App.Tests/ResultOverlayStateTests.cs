using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Translation;
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

    [Fact]
    public void CompletionPreservesSourceFormatVisibleTextAndRenderedImage()
    {
        var state = new ResultOverlayState();
        var requestId = state.Begin("rendering");
        byte[] image = [1, 2, 3];

        Assert.True(state.Complete(
            requestId,
            "<html><body>译文</body></html>",
            isError: false,
            ResultContentFormat.Html,
            "译文",
            image));

        var result = Assert.IsType<ResultOverlaySnapshot>(state.Snapshot());
        Assert.Equal(ResultContentFormat.Html, result.ContentFormat);
        Assert.Equal("译文", result.VisibleText);
        Assert.Same(image, result.RenderedImage);
    }

    [Fact]
    public void FailedFollowUpCanRestorePreviousCompletedResult()
    {
        var state = new ResultOverlayState();
        var first = state.Begin("pending");
        state.Complete(first, "completed answer", isError: false, ResultContentFormat.Markdown);
        var previous = Assert.IsType<ResultOverlaySnapshot>(state.Snapshot());

        _ = state.Begin("next question");
        state.Restore(previous);

        var restored = Assert.IsType<ResultOverlaySnapshot>(state.Snapshot());
        Assert.Equal("completed answer", restored.Text);
        Assert.Equal(ResultStatus.Completed, restored.Status);
    }

    [Fact]
    public void StreamingAndCompletionReplaceChatSnapshotWithoutLosingHistory()
    {
        var state = new ResultOverlayState();
        var history = new[] { new AssistantConversationTurn("first", "answer") };
        var pending = new AssistantConversationView(history, "follow up", "thinking");
        var requestId = state.Begin("thinking", ResultContentFormat.Markdown, pending);

        var streaming = new AssistantConversationView(history, "follow up", "partial answer");
        Assert.True(state.Update(requestId, "partial", streaming));
        Assert.Equal("partial answer", state.Snapshot()!.Chat!.PendingAnswer);

        var completed = new AssistantConversationView(
            [.. history, new AssistantConversationTurn("follow up", "final answer")],
            null,
            null);
        Assert.True(state.Complete(
            requestId,
            "final",
            isError: false,
            ResultContentFormat.Markdown,
            chat: completed));
        Assert.Equal(2, state.Snapshot()!.Chat!.Turns.Count);
        Assert.Null(state.Snapshot()!.Chat!.PendingQuestion);
    }
}
