using System.Net;
using System.Net.Sockets;
using System.Text;
using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.Output;

public sealed class VrChatOscOutput : IDisposable
{
    private readonly UdpClient _client = new();
    private readonly IPEndPoint _endpoint;
    private readonly VrChatVoiceInputConfiguration _configuration;

    public VrChatOscOutput(VrChatVoiceInputConfiguration configuration)
    {
        _configuration = configuration;
        if (!IPAddress.TryParse(configuration.Host, out var address))
        {
            throw new InvalidOperationException("VRChat OSC 主机必须是有效 IP 地址。");
        }

        if (configuration.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("VRChat OSC 端口必须介于 1 和 65535 之间。");
        }

        _endpoint = new IPEndPoint(address, configuration.Port);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        foreach (var chunk in TextChunker.Split(text, _configuration.MaxChatboxCharacters))
        {
            var packet = OscChatboxMessage.Create(chunk, _configuration.SendImmediately);
            await _client.SendAsync(packet, _endpoint, cancellationToken);
        }
    }

    public Task SetTypingAsync(bool isTyping, CancellationToken cancellationToken = default) =>
        _client.SendAsync(OscTypingMessage.Create(isTyping), _endpoint, cancellationToken).AsTask();

    public void Dispose() => _client.Dispose();
}

public static class TextChunker
{
    public static IReadOnlyList<string> Split(string text, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == maximumCharacters)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
                count = 0;
            }

            current.Append(rune);
            count++;
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString().Trim());
        }

        return chunks.Where(chunk => chunk.Length > 0).ToArray();
    }
}

public static class OscChatboxMessage
{
    public static byte[] Create(string text, bool sendImmediately)
    {
        using var stream = new MemoryStream();
        OscEncoding.WritePaddedString(stream, "/chatbox/input");
        OscEncoding.WritePaddedString(stream, sendImmediately ? ",sT" : ",sF");
        OscEncoding.WritePaddedString(stream, text);
        return stream.ToArray();
    }
}

public static class OscTypingMessage
{
    public static byte[] Create(bool isTyping)
    {
        using var stream = new MemoryStream();
        OscEncoding.WritePaddedString(stream, "/chatbox/typing");
        OscEncoding.WritePaddedString(stream, isTyping ? ",T" : ",F");
        return stream.ToArray();
    }
}

internal static class OscEncoding
{
    public static void WritePaddedString(Stream stream, string value)
    {
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}
