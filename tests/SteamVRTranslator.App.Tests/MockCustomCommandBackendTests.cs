using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class MockCustomCommandBackendTests
{
    [Fact]
    public async Task StreamsRecognizedCommandRepeatedTenTimes()
    {
        const string command = "解释这个谜题";
        var backend = new MockCustomCommandBackend();
        var updates = new List<string>();

        var result = await backend.ExecuteAsync([], command, updates.Add, CancellationToken.None);
        var text = Assert.IsType<string>(result);

        Assert.Equal(MockCustomCommandBackend.RepetitionCount, Count(text, command));
        Assert.True(updates.Count > 1);
        Assert.Equal(text, updates[^1]);
        Assert.All(
            updates.Zip(updates.Skip(1)),
            pair => Assert.True(pair.First.Length < pair.Second.Length));
    }

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
