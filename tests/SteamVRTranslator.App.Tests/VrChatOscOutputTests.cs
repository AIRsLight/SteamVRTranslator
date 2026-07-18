using System.Text;
using SteamVRTranslator.App.Output;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class VrChatOscOutputTests
{
    [Fact]
    public void ChatboxPacketPreservesUtf8TextAndPadding()
    {
        var packet = OscChatboxMessage.Create("你好 VRChat", sendImmediately: true);

        Assert.Equal(0, packet.Length % 4);
        Assert.Contains("/chatbox/input", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
        Assert.Contains(",sT", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
        Assert.Contains("你好 VRChat", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkerCountsUnicodeRunesInsteadOfUtf16CodeUnits()
    {
        var chunks = TextChunker.Split("A😀BC", 2);

        Assert.Equal(["A😀", "BC"], chunks);
    }
}
