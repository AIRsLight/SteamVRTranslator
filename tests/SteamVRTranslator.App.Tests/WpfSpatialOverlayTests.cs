using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SteamVRTranslator.App.SteamVR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class WpfSpatialOverlayTests
{
    [Fact]
    public void OptionsClampUnsafeDimensionsAndRefreshRate()
    {
        var options = new WpfSpatialOverlayOptions
        {
            Name = " ",
            WidthMeters = 20,
            DistanceMeters = 0.01f,
            MaximumFramesPerSecond = 500
        }.Validated();

        Assert.Equal("WPF Window", options.Name);
        Assert.Equal(2.5f, options.WidthMeters);
        Assert.Equal(0.2f, options.DistanceMeters);
        Assert.Equal(60, options.MaximumFramesPerSecond);
    }

    [Fact]
    public void WindowVisualCanRenderToOverlayBitmap()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window
                {
                    Width = 400,
                    Height = 200,
                    Content = new Border { Background = Brushes.Crimson }
                };
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());

                var bitmap = source.Render(128, 64);

                Assert.Equal(128, bitmap.PixelWidth);
                Assert.Equal(64, bitmap.PixelHeight);
                Assert.InRange(source.AspectRatio, 1.9, 2.1);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF render thread timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
