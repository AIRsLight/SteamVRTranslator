using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class HtmlResultRendererSmokeTests
{
    [Fact]
    public async Task WebView2CapturesSanitizedHtmlAtDirect2048ViewportResolution()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "SteamVRTranslator-HtmlSmoke");
        var log = new AppLog(dataDirectory);
        await using var renderer = new HtmlResultRenderer(log);
        var html = MockTranslationBackend.LayoutHtml
            .Replace(
                "</style>",
                ".viewport-probe{position:fixed;right:0;bottom:0;width:20px;height:20px;background:#00ff00}" +
                "@media(min-width:1000px){.viewport-probe{background:#ff0000}}</style>",
                StringComparison.Ordinal)
            .Replace(
                "</body>",
                "<div class=\"viewport-probe\"></div></body>",
                StringComparison.Ordinal);

        var result = await renderer.RenderAsync(
            html,
            640,
            360,
            CancellationToken.None);

        Assert.True(result.RenderedImage.Length > 1000);
        using var stream = new MemoryStream(result.RenderedImage, writable: false);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        Assert.Equal(2048, decoder.Frames[0].PixelWidth);
        Assert.Equal(1152, decoder.Frames[0].PixelHeight);
        var pixel = new byte[4];
        decoder.Frames[0].CopyPixels(
            new Int32Rect(2047, 1151, 1, 1),
            pixel,
            4,
            0);
        Assert.True(pixel[2] > 200 && pixel[1] < 50, "CSS 视口未直接使用 2048 像素宽度。");
        Assert.Contains("访客须知", result.VisibleText, StringComparison.Ordinal);

        var second = await renderer.RenderAsync(
            "<!doctype html><html><body>第二次渲染</body></html>",
            640,
            360,
            CancellationToken.None);
        Assert.Contains("第二次渲染", second.VisibleText, StringComparison.Ordinal);
        Assert.Equal(
            ["RenderedImage", "VisibleText"],
            typeof(HtmlRenderResult).GetProperties().Select(property => property.Name).Order().ToArray());
    }
}
