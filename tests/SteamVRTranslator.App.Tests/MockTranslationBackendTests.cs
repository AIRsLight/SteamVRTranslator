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
    }
}
