using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.SteamVR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class VoiceInputEchoRenderingTests
{
    [WpfRenderingFact]
    public void EchoRendersMultilingualTextWithinAdaptiveSizeLimits()
    {
        var originalLanguage = AppLocalization.Instance.Language;
        var output = Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_VOICE_ECHO_QA_DIR");
        try
        {
            var renderer = new OverlayRenderer();
            foreach (var language in ApplicationLanguages.Supported)
            {
                AppLocalization.SetLanguage(language);
                var samples = new[]
                {
                    new VoiceInputEchoSnapshot(2, "Voice.Echo.Sent", "你好，大家能看见我输入的文字吗？ Hello, can you see my message? こんにちは、一緒に遊びましょう。", 1, 2),
                    new VoiceInputEchoSnapshot(3, "Voice.Echo.Updated", string.Concat(Enumerable.Repeat("这是用于确认语音识别和翻译结果是否准确的长句文本。", 6))[..144], 2, 2),
                    new VoiceInputEchoSnapshot(4, "Voice.Echo.Preview", string.Concat(Enumerable.Repeat("😀", 144))),
                    new VoiceInputEchoSnapshot(5, "Voice.Echo.Sent", "你好")
                };
                byte[]? previous = null;
                foreach (var sample in samples)
                {
                    var frame = renderer.RenderVoiceEcho(sample);
                    Assert.InRange(frame.PixelWidth, 512, 2048);
                    Assert.InRange(frame.PixelHeight, 128, 640);
                    Assert.Equal(frame.PixelWidth * frame.PixelHeight * 4, frame.Pixels.Length);
                    Assert.Contains(frame.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
                    if (previous is not null) Assert.False(previous.SequenceEqual(frame.Pixels));
                    previous = frame.Pixels;
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Directory.CreateDirectory(output);
                        var bgra = (byte[])frame.Pixels.Clone();
                        for (var index = 0; index < bgra.Length; index += 4)
                            (bgra[index], bgra[index + 2]) = (bgra[index + 2], bgra[index]);
                        var bitmap = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96,
                            PixelFormats.Bgra32, null, bgra, frame.PixelWidth * 4);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(output, $"{language}-{sample.Revision}.png"));
                        encoder.Save(file);
                    }
                }
            }
        }
        finally { AppLocalization.SetLanguage(originalLanguage); }
    }

    [WpfRenderingFact]
    public void ShortTextShrinksAndLongTextWrapsInsteadOfShrinkingTheFont()
    {
        var renderer = new OverlayRenderer();
        var shortText = renderer.RenderVoiceEcho(new(1, "Voice.Echo.Sent", "你好"));
        var longText = renderer.RenderVoiceEcho(new(2, "Voice.Echo.Sent", new string('好', 144)));
        Assert.True(shortText.PixelWidth < longText.PixelWidth);
        Assert.True(shortText.PixelHeight < longText.PixelHeight);
        Assert.Equal(2048, longText.PixelWidth);
        var shortAgain = renderer.RenderVoiceEcho(new(3, "Voice.Echo.Sent", "你好"));
        Assert.Equal(shortText.PixelWidth, shortAgain.PixelWidth);
        Assert.Equal(shortText.PixelHeight, shortAgain.PixelHeight);
    }

    [WpfRenderingFact]
    public void StatusAndChunkMetadataDoNotAffectTextOnlyRendering()
    {
        var renderer = new OverlayRenderer();
        var compact = renderer.RenderVoiceEcho(new(1, "Voice.Echo.Sent", "你好"));
        var preview = renderer.RenderVoiceEcho(new(2, "Voice.Echo.Preview", "你好", 1, 2));
        Assert.Equal(compact.PixelWidth, preview.PixelWidth);
        Assert.Equal(compact.PixelHeight, preview.PixelHeight);
        Assert.Equal(compact.Pixels, preview.Pixels);
        var compactGlyphWidth = GlyphWidth(compact);
        Assert.InRange(compactGlyphWidth, 80, 120);
        Assert.Equal(compactGlyphWidth, GlyphWidth(preview));
    }

    private static int GlyphWidth(OverlayRenderFrame frame)
    {
        var left = frame.PixelWidth;
        var right = 0;
        for (var y = 32; y < frame.PixelHeight - 32; y++)
        for (var x = 20; x < frame.PixelWidth - 20; x++)
        {
            var pixel = (y * frame.PixelWidth + x) * 4;
            if (frame.Pixels[pixel] < 220 || frame.Pixels[pixel + 1] < 220 || frame.Pixels[pixel + 2] < 220) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
        }
        return right - left + 1;
    }
}
