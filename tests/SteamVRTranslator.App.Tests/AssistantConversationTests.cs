using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class AssistantConversationTests
{
    [Fact]
    public void FollowUpIsAppendedToTheSameTranscriptAndChatView()
    {
        var conversation = new AssistantConversation(
            new CapturedFrame("capture.jpg", [1, 2, 3], 640, 360));

        _ = conversation.Append("第一个问题", "第一个答案");
        var transcript = conversation.Append("第二个问题", "第二个答案");

        Assert.Contains("第一个问题", transcript, StringComparison.Ordinal);
        Assert.Contains("第一个答案", transcript, StringComparison.Ordinal);
        Assert.Contains("第二个问题", transcript, StringComparison.Ordinal);
        Assert.Contains("第二个答案", transcript, StringComparison.Ordinal);
        Assert.Equal(2, conversation.Snapshot().Count);
        var view = conversation.CreateView();
        Assert.Equal(2, view.Turns.Count);
        Assert.Null(view.PendingQuestion);
        Assert.Null(view.PendingAnswer);
    }

    [Fact]
    public void PendingFollowUpDoesNotMutateCompletedHistory()
    {
        var conversation = new AssistantConversation(
            new CapturedFrame("capture.jpg", [], 640, 360));
        _ = conversation.Append("已有问题", "已有答案");

        var pending = conversation.FormatPending("新的追问", "正在生成...");

        Assert.Contains("新的追问", pending, StringComparison.Ordinal);
        Assert.Contains("正在生成...", pending, StringComparison.Ordinal);
        Assert.Single(conversation.Snapshot());
        var view = conversation.CreateView("新的追问", "正在生成...");
        Assert.Equal("新的追问", view.PendingQuestion);
        Assert.Equal("正在生成...", view.PendingAnswer);
        Assert.Single(view.Turns);
    }
}
