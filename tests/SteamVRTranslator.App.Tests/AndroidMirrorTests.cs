using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamVRTranslator.App.AndroidMirror;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.Core.Selection;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class AndroidMirrorTests
{
    [Theory]
    [InlineData(480, 720)]
    [InlineData(720, 720)]
    [InlineData(768, 720)]
    [InlineData(900, 900)]
    [InlineData(1024, 1080)]
    [InlineData(1920, 1080)]
    public void MirrorResolutionIsRestrictedTo720pThrough1080p(int input, int expected)
    {
        Assert.Equal(expected, AndroidMirrorConfiguration.NormalizeMaximumSize(input));
    }

    [Theory]
    [InlineData(null, AndroidVideoDecoders.Auto)]
    [InlineData("invalid", AndroidVideoDecoders.Auto)]
    [InlineData("software", AndroidVideoDecoders.Software)]
    [InlineData("D3D11VA", AndroidVideoDecoders.D3D11)]
    public void MirrorDecoderConfigurationUsesStableValues(string? input, string expected)
    {
        Assert.Equal(expected, AndroidVideoDecoders.Normalize(input));
    }

    [Fact]
    public void FfmpegChecksumManifestSelectsTheRequestedPackage()
    {
        const string package = "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip";
        const string expected = "4d3cfc6f1dbf616eb46bf6b8ff8cb2126304675f04bf9478d1a458ee3d0d5f80";
        var manifest = $"deadbeef  unrelated.zip\n{expected} *{package}\n";

        Assert.Equal(expected, AndroidMirrorRuntimeService.ParsePackageHash(manifest, package));
    }

    [Theory]
    [InlineData(double.NaN, 0.132)]
    [InlineData(0.25, 0.132)]
    [InlineData(0.08, 0.132)]
    [InlineData(0.113, 0.132)]
    [InlineData(0.12, 0.12)]
    public void MirrorPhysicalWidthMigratesToPhoneScale(double input, double expected)
    {
        Assert.Equal(expected, AndroidMirrorConfiguration.NormalizeWindowWidthMeters(input), 3);
    }

    [Theory]
    [InlineData(0.74, 0.75, 0.099)]
    [InlineData(1.02, 1.0, 0.132)]
    [InlineData(1.28, 1.25, 0.165)]
    [InlineData(1.49, 1.5, 0.198)]
    public void MirrorScaleUsesTwentyEightCentimetersAsOne(
        double input,
        double expectedScale,
        double expectedPortraitWidth)
    {
        Assert.Equal(expectedScale, AndroidMirrorConfiguration.NormalizeWindowScale(input), 2);
        Assert.Equal(
            expectedPortraitWidth,
            AndroidMirrorConfiguration.WindowWidthFromScale(input),
            3);
    }

    [Fact]
    public void SmallTriggerJitterRemainsATapUntilTheDragThresholdIsCrossed()
    {
        var stabilizer = new AndroidTouchStabilizer();
        var down = new AndroidTouchEvent(AndroidTouchAction.Move, 300, 500, 1080, 2400);

        Assert.Equal(AndroidTouchAction.Down, stabilizer.Begin(down).Action);
        Assert.False(stabilizer.TryMove(down with { X = 310, Y = 508 }, out _));
        var tapUp = stabilizer.Complete(down with { X = 312, Y = 510 });
        Assert.Equal(AndroidTouchAction.Up, tapUp.Action);
        Assert.Equal(300, tapUp.X);
        Assert.Equal(500, tapUp.Y);

        stabilizer.Begin(down);
        Assert.True(stabilizer.TryMove(down with { X = 350 }, out var move));
        Assert.Equal(AndroidTouchAction.Move, move.Action);
        Assert.Equal(350, move.X);
        var dragUp = stabilizer.Complete(down with { X = 360 });
        Assert.Equal(360, dragUp.X);
    }

    [Theory]
    [InlineData(0.10f, 0.20f, 0, 0)]
    [InlineData(0.50f, 0.55f, 500, 1000)]
    [InlineData(0.90f, 0.90f, 999, 1999)]
    public void PhoneTouchUsesTheDirectVideoViewport(
        float pointX,
        float pointY,
        int expectedX,
        int expectedY)
    {
        var region = new DirectOverlayPixelRegion(0.10f, 0.20f, 0.80f, 0.70f);

        Assert.True(AndroidTouchCoordinateMapper.TryMap(
            new NormalizedPoint(pointX, pointY),
            region,
            1000,
            2000,
            out var touch));
        Assert.Equal(expectedX, touch.X);
        Assert.Equal(expectedY, touch.Y);
    }

    [Theory]
    [InlineData(0.09f, 0.50f)]
    [InlineData(0.91f, 0.50f)]
    [InlineData(0.50f, 0.19f)]
    [InlineData(0.50f, 0.91f)]
    public void PhoneTouchRejectsWindowChromeOutsideTheVideoViewport(float x, float y)
    {
        Assert.False(AndroidTouchCoordinateMapper.TryMap(
            new NormalizedPoint(x, y),
            new DirectOverlayPixelRegion(0.10f, 0.20f, 0.80f, 0.70f),
            1000,
            2000,
            out _));
    }

    [Theory]
    [InlineData(4000, 1000, 0.10f, 0.30f, 0.80f, 0.40f)]
    [InlineData(1000, 4000, 0.4625f, 0.20f, 0.075f, 0.60f)]
    [InlineData(8000, 3000, 0.10f, 0.20f, 0.80f, 0.60f)]
    public void VideoViewportPreservesArbitrarySourceAspectInsideTheScreenArea(
        int sourceWidth,
        int sourceHeight,
        float expectedX,
        float expectedY,
        float expectedWidth,
        float expectedHeight)
    {
        var fitted = AndroidTouchCoordinateMapper.FitVideoRegion(
            new DirectOverlayPixelRegion(0.10f, 0.20f, 0.80f, 0.60f),
            2d,
            sourceWidth,
            sourceHeight);

        Assert.Equal(expectedX, fitted.X, 5);
        Assert.Equal(expectedY, fitted.Y, 5);
        Assert.Equal(expectedWidth, fitted.Width, 5);
        Assert.Equal(expectedHeight, fitted.Height, 5);
    }

    [Fact]
    public void ExtraWideVideoMapsTouchOnlyAcrossItsVisibleLetterboxedArea()
    {
        var fitted = AndroidTouchCoordinateMapper.FitVideoRegion(
            new DirectOverlayPixelRegion(0.10f, 0.20f, 0.80f, 0.60f),
            2d,
            4000,
            1000);

        Assert.False(AndroidTouchCoordinateMapper.TryMap(
            new NormalizedPoint(0.50f, 0.25f),
            fitted,
            4000,
            1000,
            out _));
        Assert.True(AndroidTouchCoordinateMapper.TryMap(
            new NormalizedPoint(0.50f, 0.30f),
            fitted,
            4000,
            1000,
            out var top));
        Assert.Equal(0, top.Y);
        Assert.True(AndroidTouchCoordinateMapper.TryMap(
            new NormalizedPoint(0.50f, 0.70f),
            fitted,
            4000,
            1000,
            out var bottom));
        Assert.Equal(999, bottom.Y);
    }

    [Theory]
    [InlineData((byte)AndroidTouchAction.Down, ushort.MaxValue)]
    [InlineData((byte)AndroidTouchAction.Move, ushort.MaxValue)]
    [InlineData((byte)AndroidTouchAction.Up, 0)]
    [InlineData((byte)AndroidTouchAction.Cancel, 0)]
    public void TouchMessageMatchesScrcpyControlProtocol(
        byte actionValue,
        ushort expectedPressure)
    {
        var action = (AndroidTouchAction)actionValue;
        var message = ScrcpyAndroidSession.BuildTouchMessage(new AndroidTouchEvent(
            action,
            321,
            654,
            1080,
            2340));

        Assert.Equal(32, message.Length);
        Assert.Equal(2, message[0]);
        Assert.Equal((byte)action, message[1]);
        Assert.Equal(unchecked((ulong)-3L), BinaryPrimitives.ReadUInt64BigEndian(message.AsSpan(2, 8)));
        Assert.Equal(321, BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(10, 4)));
        Assert.Equal(654, BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(14, 4)));
        Assert.Equal(1080, BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(18, 2)));
        Assert.Equal(2340, BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(20, 2)));
        Assert.Equal(expectedPressure, BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(22, 2)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    [InlineData(187)]
    public void NavigationKeyMessageMatchesScrcpyControlProtocol(
        int expectedKeyCode)
    {
        var key = (AndroidNavigationKey)expectedKeyCode;
        foreach (var action in new[] { AndroidKeyAction.Down, AndroidKeyAction.Up })
        {
            var message = ScrcpyAndroidSession.BuildKeyCodeMessage(action, key);

            Assert.Equal(14, message.Length);
            Assert.Equal(0, message[0]);
            Assert.Equal((byte)action, message[1]);
            Assert.Equal(expectedKeyCode, BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(2, 4)));
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(6, 4)));
            Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(10, 4)));
        }
    }

    [WpfRenderingFact]
    public void MirrorWindowUsesSourceAspectAndFitsPhonePhysicalEnvelope()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var runtime = new AndroidMirrorRuntimeService();
                var log = new AppLog(Path.Combine(
                    Path.GetTempPath(),
                    "SteamVRTranslator-AndroidMirrorLayoutTests"));
                var session = new ScrcpyAndroidSession(runtime, log);
                var window = new AndroidMirrorWindow(session)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000
                };
                window.Show();

                window.ConfigureForSource(404, 900);
                var image = Assert.IsType<Image>(window.FindName("PhoneImage"));
                Assert.Equal(404d / 900d, image.Width / image.Height, 3);
                Assert.InRange(window.RecommendedWidthMeters, 0.11, 0.13);
                Assert.Equal(0.28, window.RecommendedHeightMeters, 2);
                SaveMirrorVisual(window, "portrait");

                window.ConfigureForSource(900, 404);
                Assert.Equal(900d / 404d, image.Width / image.Height, 3);
                Assert.Equal(0.28, window.RecommendedWidthMeters, 2);
                Assert.InRange(window.RecommendedHeightMeters, 0.14, 0.15);
                var landscapeRegion = window.DirectPixelRegion;
                Assert.Equal(
                    900d / 404d,
                    (window.Width / window.Height) *
                    landscapeRegion.Width /
                    landscapeRegion.Height,
                    3);
                SaveMirrorVisual(window, "landscape");

                window.ConfigureForSource(3600, 900);
                var wideRegion = window.DirectPixelRegion;
                Assert.Equal(
                    4d,
                    (window.Width / window.Height) * wideRegion.Width / wideRegion.Height,
                    3);
                Assert.True(AndroidTouchCoordinateMapper.TryMap(
                    new NormalizedPoint(
                        wideRegion.X + (wideRegion.Width * 0.75f),
                        wideRegion.Y + (wideRegion.Height * 0.25f)),
                    wideRegion,
                    3600,
                    900,
                    out var wideTouch));
                Assert.Equal(2699, wideTouch.X);
                Assert.Equal(225, wideTouch.Y);

                Assert.NotNull(window.FindName("BackButton"));
                Assert.NotNull(window.FindName("HomeButton"));
                Assert.NotNull(window.FindName("RecentAppsButton"));
                using (var interactionSource = new WpfWindowOverlaySource(
                           window,
                           new WpfSpatialOverlayOptions()))
                {
                    var controls = interactionSource.RenderPixels(
                        Math.Max(1, (int)Math.Round(window.Width)),
                        Math.Max(1, (int)Math.Round(window.Height)));
                    var controlPixels = BitmapPixels(controls);
                    Assert.Equal(0, PixelAlpha(
                        controlPixels,
                        controls.PixelWidth,
                        controls.PixelWidth / 2,
                        controls.PixelHeight / 3));
                    Assert.True(PixelAlpha(
                        controlPixels,
                        controls.PixelWidth,
                        controls.PixelWidth / 2,
                        controls.PixelHeight - 10) > 0);
                    var backCenter = Assert.IsType<NormalizedPoint>(
                        interactionSource.FindNamedElementCenter("BackButton"));
                    Assert.Equal(
                        "BackButton",
                        InvokeFromWorker(() => interactionSource.PointerDown(backCenter)));
                    _ = InvokeFromWorker(() => interactionSource.PointerUp(backCenter));

                    var videoRegion = window.DirectPixelRegion;
                    var videoCenter = new NormalizedPoint(
                        videoRegion.X + (videoRegion.Width / 2f),
                        videoRegion.Y + (videoRegion.Height / 2f));
                    Assert.Equal(
                        "AndroidScreen",
                        InvokeFromWorker(() => interactionSource.PointerDown(videoCenter)));
                    Assert.True(InvokeFromWorker(() => interactionSource.PointerUp(videoCenter)));
                }
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Android mirror layout test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void SaveMirrorVisual(AndroidMirrorWindow window, string orientation)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        window.UpdateLayout();
        using var source = new WpfWindowOverlaySource(
            window,
            new WpfSpatialOverlayOptions());
        var width = Math.Max(1, (int)Math.Round(window.Width));
        var height = Math.Max(1, (int)Math.Round(window.Height));
        var bitmap = source.Render(width, height);
        Directory.CreateDirectory(outputDirectory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(
            outputDirectory,
            $"android-mirror-{orientation}.png"));
        encoder.Save(stream);
    }

    private static T InvokeFromWorker<T>(Func<T> action)
    {
        var task = Task.Run(action);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        Assert.True(task.IsCompleted, "Worker operation did not complete on the WPF dispatcher.");
        return task.GetAwaiter().GetResult();
    }

    private static byte[] BitmapPixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static byte PixelAlpha(byte[] pixels, int width, int x, int y) =>
        pixels[((y * width) + x) * 4 + 3];

    [Fact]
    [Trait("Category", "AndroidIntegration")]
    public async Task StreamsAndDecodesAFrameFromConfiguredAndroidDevice()
    {
        var serial = Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_ANDROID_DEVICE");
        if (string.IsNullOrWhiteSpace(serial))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_ANDROID_REQUIRED"),
                    "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Android integration was required, but no device serial reached the test process.");
            }
            return;
        }

        using var runtime = new AndroidMirrorRuntimeService();
        if (!runtime.IsInstalled)
        {
            await runtime.DownloadAsync();
        }

        var adb = new AdbClient(runtime.AdbPath);
        var devices = await adb.GetDevicesAsync();
        var device = Assert.Single(devices, candidate =>
            candidate.IsOnline &&
            string.Equals(candidate.Serial, serial, StringComparison.OrdinalIgnoreCase));
        var log = new AppLog(Path.Combine(Path.GetTempPath(), "SteamVRTranslator-AndroidMirrorTests"));
        foreach (var maximumSize in new[] { 720, 1080 })
        {
            var firstFrame = new TaskCompletionSource<AndroidVideoFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = new ScrcpyAndroidSession(runtime, log);
            session.FrameReceived += (_, frame) => firstFrame.TrySetResult(frame);

            await session.StartAsync(
                device,
                new AndroidMirrorConfiguration
                {
                    DeviceSerial = device.Serial,
                    MaximumSize = maximumSize,
                    MaximumFramesPerSecond = 60,
                    VideoBitRateMbps = maximumSize == 720 ? 8 : 12
                });
            var frame = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(frame.Width > 0);
            Assert.True(frame.Height > 0);
            Assert.Equal(maximumSize, Math.Max(frame.Width, frame.Height));
            Assert.Equal(frame.Width * frame.Height * 4, frame.BgraPixels.Length);
            Assert.Contains(frame.BgraPixels, value => value != 0);
            Assert.True(session.FramesReceived > 0);
            Assert.True(session.QueueTouch(new AndroidTouchEvent(
                AndroidTouchAction.Down,
                frame.Width / 2,
                frame.Height / 2,
                frame.Width,
                frame.Height)));
            await Task.Delay(50);
            Assert.True(session.QueueTouch(new AndroidTouchEvent(
                AndroidTouchAction.Up,
                frame.Width / 2,
                frame.Height / 2,
                frame.Width,
                frame.Height)));
            await Task.Delay(100);
        }
    }

    [Fact]
    [Trait("Category", "AndroidIntegration")]
    public void WideAvdResolutionChangesKeepVideoAndTouchAligned()
    {
        var serial = Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_ANDROID_DEVICE");
        if (string.IsNullOrWhiteSpace(serial))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_ANDROID_REQUIRED"),
                    "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Android integration was required, but no device serial reached the test process.");
            }
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunWideAvdResolutionChangeTest(serial);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(
            thread.Join(TimeSpan.FromMinutes(2)),
            "Android wide-screen resolution change test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RunWideAvdResolutionChangeTest(string serial)
    {
        using var runtime = new AndroidMirrorRuntimeService();
        if (!runtime.IsInstalled)
        {
            runtime.DownloadAsync().GetAwaiter().GetResult();
        }

        var adb = new AdbClient(runtime.AdbPath);
        var devices = adb.GetDevicesAsync().GetAwaiter().GetResult();
        var device = Assert.Single(devices, candidate =>
            candidate.IsOnline &&
            string.Equals(candidate.Serial, serial, StringComparison.OrdinalIgnoreCase));
        var log = new AppLog(Path.Combine(
            Path.GetTempPath(),
            "SteamVRTranslator-AndroidMirrorResolutionTests"));

        try
        {
            SetAndroidDisplaySizeAsync(adb, serial, 2400, 600).GetAwaiter().GetResult();
            var firstFrame = new TaskCompletionSource<AndroidVideoFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new ScrcpyAndroidSession(runtime, log);
            session.FrameReceived += (_, frame) => firstFrame.TrySetResult(frame);
            var window = new AndroidMirrorWindow(session);
            try
            {
                session.StartAsync(
                    device,
                    new AndroidMirrorConfiguration
                    {
                        DeviceSerial = device.Serial,
                        MaximumSize = 1080,
                        MaximumFramesPerSecond = 60,
                        VideoBitRateMbps = 12
                    }).GetAwaiter().GetResult();
                var wideFrame = firstFrame.Task
                    .WaitAsync(TimeSpan.FromSeconds(30))
                    .GetAwaiter()
                    .GetResult();
                Assert.Equal((1080, 270), (wideFrame.Width, wideFrame.Height));
                window.ConfigureForSource(wideFrame.Width, wideFrame.Height);
                AssertVideoAndTouchAlignment(window, session, wideFrame);

                var landscapeFrameTask = WaitForFrameAsync(
                    session,
                    frame => frame.Width == 1080 && frame.Height == 540,
                    TimeSpan.FromSeconds(20));
                SetAndroidDisplaySizeAsync(adb, serial, 1800, 900).GetAwaiter().GetResult();
                var landscapeFrame = landscapeFrameTask.GetAwaiter().GetResult();
                AssertVideoAndTouchAlignment(window, session, landscapeFrame);

                var portraitFrameTask = WaitForFrameAsync(
                    session,
                    frame => frame.Width == 270 && frame.Height == 1080,
                    TimeSpan.FromSeconds(20));
                SetAndroidDisplaySizeAsync(adb, serial, 600, 2400).GetAwaiter().GetResult();
                var portraitFrame = portraitFrameTask.GetAwaiter().GetResult();
                AssertVideoAndTouchAlignment(window, session, portraitFrame);
            }
            finally
            {
                session.StopAsync().GetAwaiter().GetResult();
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                window.Close();
            }
        }
        finally
        {
            var reset = adb.RunForDeviceAsync(
                    serial,
                    ["shell", "wm", "size", "reset"])
                .GetAwaiter()
                .GetResult();
            reset.EnsureSuccess("恢复 Android 显示尺寸");
        }
    }

    private static async Task SetAndroidDisplaySizeAsync(
        AdbClient adb,
        string serial,
        int width,
        int height)
    {
        var result = await adb.RunForDeviceAsync(
            serial,
            ["shell", "wm", "size", $"{width}x{height}"]);
        result.EnsureSuccess($"设置 Android 显示尺寸 {width}x{height}");
        await Task.Delay(400);
    }

    private static async Task<AndroidVideoFrame> WaitForFrameAsync(
        ScrcpyAndroidSession session,
        Func<AndroidVideoFrame, bool> predicate,
        TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<AndroidVideoFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<AndroidVideoFrame>? handler = null;
        handler = (_, frame) =>
        {
            if (predicate(frame))
            {
                completion.TrySetResult(frame);
            }
        };
        session.FrameReceived += handler;
        try
        {
            return await completion.Task.WaitAsync(timeout);
        }
        finally
        {
            session.FrameReceived -= handler;
        }
    }

    private static void AssertVideoAndTouchAlignment(
        AndroidMirrorWindow window,
        ScrcpyAndroidSession session,
        AndroidVideoFrame frame)
    {
        var region = window.DirectPixelRegion;
        var physicalAspect = (window.Width / window.Height) *
                             region.Width /
                             region.Height;
        Assert.Equal((double)frame.Width / frame.Height, physicalAspect, 3);

        var point = new NormalizedPoint(
            region.X + (region.Width * 0.75f),
            region.Y + (region.Height * 0.25f));
        Assert.True(AndroidTouchCoordinateMapper.TryMap(
            point,
            region,
            frame.Width,
            frame.Height,
            out var touch));
        Assert.Equal((int)Math.Round(0.75d * (frame.Width - 1)), touch.X);
        Assert.Equal((int)Math.Round(0.25d * (frame.Height - 1)), touch.Y);

        var outside = region.X > 0.001f
            ? new NormalizedPoint(region.X / 2f, region.Y + (region.Height / 2f))
            : new NormalizedPoint(region.X + (region.Width / 2f), region.Y / 2f);
        Assert.False(AndroidTouchCoordinateMapper.TryMap(
            outside,
            region,
            frame.Width,
            frame.Height,
            out _));

        var queuedBefore = session.TouchEventsQueued;
        Assert.Equal("AndroidScreen", window.PointerDown(point));
        Assert.True(window.PointerUp(point));
        Assert.Equal(queuedBefore + 2, session.TouchEventsQueued);
        Assert.Null(window.PointerDown(outside));
        Assert.Equal(queuedBefore + 2, session.TouchEventsQueued);
    }
}
