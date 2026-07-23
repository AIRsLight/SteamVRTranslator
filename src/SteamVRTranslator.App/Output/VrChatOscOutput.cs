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
    private int _streamingChunkIntervalMilliseconds;

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

        ValidateStreamingChunkInterval(configuration.StreamingChunkIntervalMilliseconds);
        _streamingChunkIntervalMilliseconds = configuration.StreamingChunkIntervalMilliseconds;
        _endpoint = new IPEndPoint(address, configuration.Port);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        await SendAsync(text, _configuration.SendImmediately, cancellationToken);
    }

    public async Task SendAsync(
        string text,
        bool sendImmediately,
        CancellationToken cancellationToken = default)
    {
        var stream = PlanStream(text);
        foreach (var chunk in stream)
        {
            if (chunk.DelayBefore > TimeSpan.Zero)
            {
                await Task.Delay(chunk.DelayBefore, cancellationToken);
            }

            var packet = OscChatboxMessage.Create(chunk.Text, sendImmediately);
            await _client.SendAsync(packet, _endpoint, cancellationToken);
        }
    }

    public Task UpdateSubmittedChatboxAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var visibleText = TextChunker.Tail(text, _configuration.MaxChatboxCharacters);
        return visibleText.Length == 0
            ? Task.CompletedTask
            : _client.SendAsync(
                OscChatboxMessage.Create(
                    visibleText,
                    sendImmediately: true,
                    notificationSfx: false),
                _endpoint,
                cancellationToken).AsTask();
    }

    public Task SendPreviewAsync(string text, CancellationToken cancellationToken = default)
    {
        var preview = PlanPreview(text);
        return preview.Length == 0
            ? Task.CompletedTask
            : _client.SendAsync(
                OscChatboxMessage.Create(preview, sendImmediately: false),
                _endpoint,
                cancellationToken).AsTask();
    }

    public void UpdateStreamingChunkInterval(int milliseconds)
    {
        ValidateStreamingChunkInterval(milliseconds);
        Volatile.Write(ref _streamingChunkIntervalMilliseconds, milliseconds);
    }

    internal IReadOnlyList<OscChatboxStreamChunk> PlanStream(string text) =>
        OscChatboxStream.Create(
            text,
            _configuration.MaxChatboxCharacters,
            TimeSpan.FromMilliseconds(Volatile.Read(ref _streamingChunkIntervalMilliseconds)));

    internal string PlanPreview(string text) =>
        TextChunker.Split(text, _configuration.MaxChatboxCharacters).FirstOrDefault() ?? string.Empty;

    public Task SetTypingAsync(bool isTyping, CancellationToken cancellationToken = default) =>
        _client.SendAsync(OscTypingMessage.Create(isTyping), _endpoint, cancellationToken).AsTask();

    public void Dispose() => _client.Dispose();

    private static void ValidateStreamingChunkInterval(int milliseconds)
    {
        if (milliseconds is
            < VrChatVoiceInputConfiguration.MinimumStreamingChunkIntervalMilliseconds or
            > VrChatVoiceInputConfiguration.MaximumStreamingChunkIntervalMilliseconds)
        {
            throw new InvalidOperationException(
                $"OSC 分段间隔必须介于 " +
                $"{VrChatVoiceInputConfiguration.MinimumStreamingChunkIntervalMilliseconds} 和 " +
                $"{VrChatVoiceInputConfiguration.MaximumStreamingChunkIntervalMilliseconds} 毫秒之间。");
        }
    }
}

internal static class OscChatboxStream
{
    public static IReadOnlyList<OscChatboxStreamChunk> Create(
        string text,
        int maximumCharacters,
        TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        return TextChunker.Split(text, maximumCharacters)
            .Select((chunk, index) => new OscChatboxStreamChunk(
                chunk,
                index == 0 ? TimeSpan.Zero : interval))
            .ToArray();
    }
}

internal readonly record struct OscChatboxStreamChunk(string Text, TimeSpan DelayBefore);

internal readonly record struct VoiceTranslationOscPlan(
    bool ShowOriginal,
    bool SendOriginalImmediately,
    bool SendTranslationImmediately,
    bool UpdateSubmittedOriginal)
{
    public static VoiceTranslationOscPlan Create(string displayMode, bool sendImmediately)
    {
        var showOriginal = string.Equals(
            VoiceTranslationDisplayModes.Normalize(displayMode),
            VoiceTranslationDisplayModes.OriginalThenTranslation,
            StringComparison.OrdinalIgnoreCase);
        return new VoiceTranslationOscPlan(
            showOriginal,
            showOriginal && sendImmediately,
            !showOriginal && sendImmediately,
            showOriginal && sendImmediately);
    }
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

    public static string Tail(string text, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        var runes = text.Trim().EnumerateRunes().ToArray();
        return runes.Length <= maximumCharacters
            ? text.Trim()
            : string.Concat(runes[^maximumCharacters..]);
    }
}

public static class OscChatboxMessage
{
    public static byte[] Create(
        string text,
        bool sendImmediately,
        bool? notificationSfx = null)
    {
        using var stream = new MemoryStream();
        OscEncoding.WritePaddedString(stream, "/chatbox/input");
        var typeTags = sendImmediately ? ",sT" : ",sF";
        if (notificationSfx.HasValue)
        {
            typeTags += notificationSfx.Value ? "T" : "F";
        }

        OscEncoding.WritePaddedString(stream, typeTags);
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
