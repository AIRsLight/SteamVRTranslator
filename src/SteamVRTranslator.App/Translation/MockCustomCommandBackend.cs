using System.Text;

namespace SteamVRTranslator.App.Translation;

public sealed class MockCustomCommandBackend : ICustomCommandBackend
{
    public const int RepetitionCount = 10;
    private const int ChunkLength = 10;
    private static readonly TimeSpan ChunkDelay = TimeSpan.FromMilliseconds(55);

    public async Task<string?> ExecuteAsync(
        byte[] imageBytes,
        string command,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("自定义命令为空。");
        }

        var original = command.Trim();
        var response = string.Join(
            Environment.NewLine + Environment.NewLine,
            Enumerable.Repeat(original, RepetitionCount));
        var streamed = new StringBuilder(response.Length);
        for (var offset = 0; offset < response.Length; offset += ChunkLength)
        {
            await Task.Delay(ChunkDelay, cancellationToken);
            var length = Math.Min(ChunkLength, response.Length - offset);
            streamed.Append(response, offset, length);
            onPartialResult?.Invoke(streamed.ToString());
        }

        return streamed.ToString();
    }
}
