using System.Text;

namespace SteamVRTranslator.App.Translation;

public sealed class MockTranslationBackend : ITranslationBackend
{
    public const string FixedText =
        "# 模拟翻译结果\n\n" +
        "这是用于验证 **Markdown 流式显示** 的测试内容。\n\n" +
        "## 画面内容\n\n" +
        "- 请沿着右侧通道前进，在入口处确认设备状态。\n" +
        "- 留意临时调整后的开放时间，并保持通道畅通。\n" +
        "- 需要帮助时，请联系附近的工作人员。\n\n" +
        "> The service desk has moved to the next floor. Keep your ticket available and follow the illuminated signs.\n\n" +
        "## 操作提示\n\n" +
        "1. 截图和结果是独立的空间对象。\n" +
        "2. 使用抓握键移动和旋转窗口。\n" +
        "3. 使用右摇杆平滑滚动长文本。\n\n" +
        "测试链接：[SteamVR Translator](https://example.invalid/)；测试代码：`READY`。\n\n" +
        "この文章はスクロール操作を確認するための長い模擬結果です。右スティックを上下に動かすと、表示領域の続きを読むことができます。";

    public const string LayoutHtml =
        "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
        "<style>html,body{margin:0;width:100%;height:100%;font-family:'Microsoft YaHei UI',sans-serif;" +
        "background:#edf0f2;color:#202529}body{display:grid;grid-template-rows:22% 1fr 18%;}" +
        "header{display:flex;align-items:center;padding:0 6%;background:#31373b;color:#fff;font-size:5vw;font-weight:700}" +
        "main{padding:5% 6%;display:grid;grid-template-columns:1fr 1fr;gap:4%;}" +
        ".item{border:2px solid #657078;padding:6%;font-size:3.2vw}.item strong{display:block;font-size:4vw;margin-bottom:4%}" +
        "footer{display:flex;align-items:center;justify-content:center;background:#d8dde0;font-size:2.6vw}</style></head>" +
        "<body><header>访客须知</header><main><section class=\"item\"><strong>入口</strong>请准备好通行凭证</section>" +
        "<section class=\"item\"><strong>服务台</strong>已移至下一楼层</section></main>" +
        "<footer>模拟提供商 HTML 排版翻译</footer></body></html>";

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

    public async Task<string?> TranslateLayoutAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(180, cancellationToken);
        return LayoutHtml;
    }

    public async Task<string?> TranslateTextAsync(
        string sourceText,
        string targetLanguage,
        string systemPrompt,
        string taskPrompt,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(120, cancellationToken);
        var result = $"[{targetLanguage}] {sourceText.Trim()}";
        onPartialResult?.Invoke(result);
        return result;
    }

    public Task<string?> ExecuteCustomCommandAsync(
        byte[] imageBytes,
        string command,
        IReadOnlyList<AssistantConversationTurn> history,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken) =>
        new MockCustomCommandBackend().ExecuteAsync(
            imageBytes,
            command,
            onPartialResult,
            cancellationToken);
}
