using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class ResultContentFormatterTests
{
    [Fact]
    public void MarkdownVisibleTextExcludesFormattingAndLinkTarget()
    {
        const string markdown =
            "# 标题\n\n这是 **重点** 和 [可见链接](https://example.invalid/path)。\n\n" +
            "- 第一项\n- 第二项\n\n`READY`";
        var renderer = new OverlayRenderer();

        var visible = renderer.ExtractVisibleText(ResultContentFormat.Markdown, markdown);

        Assert.Contains("标题", visible, StringComparison.Ordinal);
        Assert.Contains("重点", visible, StringComparison.Ordinal);
        Assert.Contains("可见链接", visible, StringComparison.Ordinal);
        Assert.Contains("READY", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("**", visible, StringComparison.Ordinal);
        Assert.DoesNotContain('`', visible);
    }

    [Fact]
    public void HtmlPreparationRemovesExecutableAndExternalContent()
    {
        const string html =
            "```html\n<html><head><style>@import url('https://bad.invalid/a.css');" +
            ".card{background:url(https://bad.invalid/a.png)}</style></head>" +
            "<body onclick=\"alert(1)\"><h1>访客须知</h1><p>请前往 <b>二楼</b></p>" +
            "<img src=\"https://bad.invalid/a.png\"><script>alert(1)</script></body></html>\n```";

        var prepared = ResultContentFormatter.PrepareHtml(html);

        Assert.Contains("访客须知", prepared.VisibleText, StringComparison.Ordinal);
        Assert.Contains("请前往 二楼", prepared.VisibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", prepared.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", prepared.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", prepared.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://bad.invalid", prepared.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Security-Policy", prepared.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextNormalizationRemovesControlCharacters()
    {
        var normalized = ResultContentFormatter.NormalizeVisibleText(
            "第一行\0\u0001\r\n\r\n\r\n第二\t行");

        Assert.Equal("第一行\n\n第二 行", normalized);
    }

    [Theory]
    [InlineData(1280, 720, 2048, 1152)]
    [InlineData(4096, 2048, 2048, 1024)]
    [InlineData(10, 20, 1024, 2048)]
    public void HtmlViewportAlwaysUsesA2048PixelLongEdge(
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var viewport = HtmlResultRenderer.CalculateViewport(sourceWidth, sourceHeight);

        Assert.Equal(expectedWidth, viewport.Width);
        Assert.Equal(expectedHeight, viewport.Height);
    }
}
