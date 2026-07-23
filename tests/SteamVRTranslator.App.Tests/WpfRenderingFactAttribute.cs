using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class WpfRenderingFactAttribute : FactAttribute
{
    public WpfRenderingFactAttribute()
    {
        if (!WpfRenderingCapability.IsAvailable)
        {
            Skip = "RenderTargetBitmap is unavailable in the current Windows desktop session.";
        }
    }
}

internal static class WpfRenderingCapability
{
    private static readonly Lazy<bool> Availability = new(Probe);

    public static bool IsAvailable => Availability.Value;

    private static bool Probe()
    {
        var available = false;
        var thread = new Thread(() =>
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, 2, 2));
            }

            var bitmap = new RenderTargetBitmap(2, 2, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[16];
            bitmap.CopyPixels(pixels, 8, 0);
            available = pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return available;
    }
}
