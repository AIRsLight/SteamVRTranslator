using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class MockTranslationBackendTests
{
    [Fact]
    public async Task ProducesMultipleGrowingUpdatesAndScrollableText()
    {
        var backend = new MockTranslationBackend();
        var updates = new List<string>();

        var result = await backend.TranslateAsync([], updates.Add, CancellationToken.None);
        var text = Assert.IsType<string>(result);

        Assert.Equal(MockTranslationBackend.FixedText, text);
        Assert.True(text.Length > 300);
        Assert.True(updates.Count > 10);
        Assert.Equal(text, updates[^1]);
        Assert.All(
            updates.Zip(updates.Skip(1)),
            pair => Assert.True(pair.First.Length < pair.Second.Length));
        Assert.Contains("# 模拟翻译结果", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LayoutTranslationReturnsStandaloneHtml()
    {
        var backend = new MockTranslationBackend();

        var result = await backend.TranslateLayoutAsync([], CancellationToken.None);
        var html = Assert.IsType<string>(result);

        Assert.Equal(MockTranslationBackend.LayoutHtml, html);
        Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<style>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("模拟提供商 HTML 排版翻译", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomCommandRetainsMockStreamingBehavior()
    {
        var backend = new MockTranslationBackend();
        var updates = new List<string>();

        var result = await backend.ExecuteCustomCommandAsync(
            [],
            "检查这张截图",
            Array.Empty<AssistantConversationTurn>(),
            updates.Add,
            CancellationToken.None);

        var text = Assert.IsType<string>(result);
        Assert.Equal(MockCustomCommandBackend.RepetitionCount, Count(text, "检查这张截图"));
        Assert.True(updates.Count > 10);
        Assert.Equal(text, updates[^1]);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
