using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamVRTranslator.App.Capture;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class CaptureEncodingTests
{
    [Fact]
    public void CaptureEncoderProducesDecodableJpeg()
    {
        const int width = 32;
        const int height = 24;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * stride) + (x * 4);
                pixels[offset] = (byte)(x * 7);
                pixels[offset + 1] = (byte)(y * 10);
                pixels[offset + 2] = 180;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();

        var encoded = SteamVrCompositorCaptureService.EncodeJpeg(bitmap);

        Assert.Equal(0xFF, encoded[0]);
        Assert.Equal(0xD8, encoded[1]);
        Assert.Equal(0xFF, encoded[^2]);
        Assert.Equal(0xD9, encoded[^1]);
        using var stream = new MemoryStream(encoded);
        var decoder = new JpegBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        Assert.Equal(width, decoder.Frames[0].PixelWidth);
        Assert.Equal(height, decoder.Frames[0].PixelHeight);
    }
}
