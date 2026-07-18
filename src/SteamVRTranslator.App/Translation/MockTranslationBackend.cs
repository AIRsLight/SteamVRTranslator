using System.Text;

namespace SteamVRTranslator.App.Translation;

public sealed class MockTranslationBackend : ITranslationBackend
{
    public const string FixedText =
        "这是模拟流式翻译结果，用于检查文字是否会随着接口返回逐段显示。\n\n" +
        "第一段：画面中的告示提醒访客，请沿着右侧通道前进，在入口处确认设备状态，并留意临时调整后的开放时间。" +
        "如果现场人数较多，请保持适当距离，不要在通道中央停留。\n\n" +
        "第二段：The translated notice explains that the service desk has moved to the next floor. " +
        "Visitors should follow the illuminated signs, keep their ticket available, and ask a staff member when assistance is required.\n\n" +
        "第三段：この文章はスクロール操作を確認するための長い模擬結果です。" +
        "右スティックを上下に動かすと、表示領域の続きを読むことができます。\n\n" +
        "第四段：截图和结果都是独立的空间对象，可以用抓握键自由移动和旋转。" +
        "按下右摇杆可以全局隐藏或重新显示空间对象和定位手柄。";

    private const int ChunkLength = 12;

    private static readonly TimeSpan ChunkDelay = TimeSpan.FromMilliseconds(70);

    public async Task<string?> TranslateAsync(
        byte[] imageBytes,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var streamed = new StringBuilder(FixedText.Length);
        for (var offset = 0; offset < FixedText.Length; offset += ChunkLength)
        {
            await Task.Delay(ChunkDelay, cancellationToken);
            var length = Math.Min(ChunkLength, FixedText.Length - offset);
            streamed.Append(FixedText, offset, length);
            onPartialResult?.Invoke(streamed.ToString());
        }

        return streamed.ToString();
    }
}
