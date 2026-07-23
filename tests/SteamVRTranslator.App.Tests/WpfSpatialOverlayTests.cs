using System.Runtime.ExceptionServices;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.Core.Selection;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
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
        Assert.Equal(120, options.MaximumFramesPerSecond);
        Assert.Equal(60, new WpfSpatialOverlayOptions().MaximumFramesPerSecond);
    }

    [Fact]
    public void WindowSourceReusesItsUploadBuffer()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window { Width = 400, Height = 200 };
                using var source = new WpfWindowOverlaySource(window, new WpfSpatialOverlayOptions());

                var first = source.GetReusablePixelBuffer(1024);
                var second = source.GetReusablePixelBuffer(1024);
                var resized = source.GetReusablePixelBuffer(2048);

                Assert.Same(first, second);
                Assert.NotSame(first, resized);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF pixel buffer test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
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

    [Fact]
    public void WindowTextureUsesVersionedInvalidationInsteadOfPointerPolling()
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
                    Content = new Button { Content = "Test" }
                };
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions { MaximumFramesPerSecond = 60 });

                Assert.True(source.NeedsRender);
                source.RenderPixels(1024, 512);
                Assert.False(source.NeedsRender);
                Assert.InRange(source.FrameInterval.TotalMilliseconds, 16, 17);

                source.PointerMove(new NormalizedPoint(0.25f, 0.25f));
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                Assert.False(source.NeedsRender);

                source.Invalidate();
                Assert.True(source.NeedsRender);
                source.RenderPixels(1024, 512);
                Assert.False(source.NeedsRender);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF invalidation test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void DirectPixelFramesAreVersionedSeparatelyFromWpfChrome()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new DirectPixelTestWindow { Width = 400, Height = 200 };
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions { MaximumFramesPerSecond = 120 });
                source.RenderPixels(400, 200);

                Assert.False(source.NeedsRender);
                Assert.False(source.NeedsDirectPixelRender);
                window.Publish(sequence: 7);
                Assert.True(source.NeedsDirectPixelRender);
                Assert.True(source.TryGetLatestDirectPixelFrame(out var frame));
                Assert.Equal(7, frame.Sequence);
                Assert.Equal(DirectOverlayPixelFormat.Bgra, frame.Format);
                source.MarkDirectPixelFrameRendered(frame.Sequence);
                Assert.False(source.NeedsDirectPixelRender);
                Assert.False(source.NeedsRender);
                Assert.Equal(120, source.Options.MaximumFramesPerSecond);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Direct pixel source test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void ControlPanelContentChangesInvalidateItsTexture()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = CreateControlPanelWindow();
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions { MaximumFramesPerSecond = 60 });
                source.RenderPixels(1024, 676);
                Assert.False(source.NeedsRender);

                ClickDirect(window, source, "VoiceMenuButton");

                Assert.True(source.NeedsRender);
                source.RenderPixels(1024, 676);
                Assert.False(source.NeedsRender);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Control panel invalidation test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [WpfRenderingFact]
    public void FullWindowRasterizationBenchmarkReportsSixtyFpsBudget()
    {
        Exception? failure = null;
        var averageMilliseconds = double.MaxValue;
        var thread = new Thread(() =>
        {
            try
            {
                var window = CreateControlPanelWindow();
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions { MaximumFramesPerSecond = 60 });
                source.RenderPixels(1024, 676);

                const int samples = 12;
                var stopwatch = Stopwatch.StartNew();
                for (var index = 0; index < samples; index++)
                {
                    source.Invalidate();
                    source.RenderPixels(1024, 676);
                }
                stopwatch.Stop();
                averageMilliseconds = stopwatch.Elapsed.TotalMilliseconds / samples;
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF render benchmark timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.True(
            averageMilliseconds <= 1000d / 60d,
            $"1024x676 WPF rasterization averaged {averageMilliseconds:F2} ms; 60 FPS requires <= 16.67 ms.");
    }

    [Fact]
    public void VisibleOffscreenControlPanelAcceptsOverlayPointerClicks()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new VrControlPanelWindow(new VrControlPanelState(
                    VoiceEnabled: true,
                    TranslationEnabled: false,
                    SendImmediately: true,
                    DisplayMode: VoiceTranslationDisplayModes.TranslationOnly,
                    TargetLanguage: "en-US",
                    ChunkIntervalMilliseconds: 1000,
                    CaptureEye: "left-eye"))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000
                };
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var button = Assert.IsType<Button>(window.FindName("VoiceMenuButton"));
                var center = button.TranslatePoint(
                    new Point(button.ActualWidth / 2d, button.ActualHeight / 2d),
                    root);
                var pointer = new NormalizedPoint(
                    (float)(center.X / root.ActualWidth),
                    (float)(center.Y / root.ActualHeight));

                source.PointerMove(pointer);
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                source.PointerDown(pointer);
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                source.PointerUp(pointer);
                PumpDispatcher(TimeSpan.FromMilliseconds(80));

                var mainPage = Assert.IsType<Grid>(window.FindName("MainPage"));
                var voicePage = Assert.IsType<Grid>(window.FindName("VoicePage"));
                Assert.Equal(Visibility.Collapsed, mainPage.Visibility);
                Assert.Equal(Visibility.Visible, voicePage.Visibility);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "VR control panel pointer test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void ControlPanelDirectPointerSupportsSequentialNavigationAndSubtitleAction()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new VrControlPanelWindow(new VrControlPanelState(
                    VoiceEnabled: true,
                    TranslationEnabled: false,
                    SendImmediately: true,
                    DisplayMode: VoiceTranslationDisplayModes.TranslationOnly,
                    TargetLanguage: "en-US",
                    ChunkIntervalMilliseconds: 1000,
                    CaptureEye: "left-eye"))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000
                };
                var subtitlesRequested = 0;
                window.SubtitlesRequested += (_, _) => subtitlesRequested++;
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());

                ClickDirect(window, source, "SettingsMenuButton");
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("SettingsPage")).Visibility);
                ClickDirect(window, source, "HomeButton");
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("MainPage")).Visibility);
                ClickDirect(window, source, "VoiceMenuButton");
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("VoicePage")).Visibility);

                var voiceToggle = Assert.IsType<ToggleButton>(window.FindName("VoiceEnabledToggle"));
                Assert.True(voiceToggle.IsChecked);
                var togglePointer = PointerFor(window, voiceToggle);
                Assert.Equal("VoiceEnabledToggle", source.PointerDown(togglePointer));
                Assert.True(source.PointerUp(togglePointer));
                Assert.False(source.PointerUp(togglePointer));
                Assert.False(voiceToggle.IsChecked);

                ClickDirect(window, source, "HomeButton");
                ClickDirect(window, source, "SubtitleMenuButton");
                Assert.Equal(1, subtitlesRequested);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "VR control panel sequential pointer test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void ControlPanelPhoneMirrorSupportsDeviceQualityAndConnectionActions()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = CreateControlPanelWindow();
                window.ApplyAndroidMirrorState(new VrAndroidMirrorControlState(
                    RuntimeInstalled: true,
                    Devices:
                    [
                        new VrAndroidMirrorDeviceOption("emulator-5554", "Test phone A", true),
                        new VrAndroidMirrorDeviceOption("192.168.1.10:5555", "Test phone B", true)
                    ],
                    DeviceSerial: "emulator-5554",
                    MaximumSize: 720,
                    MaximumFramesPerSecond: 30,
                    VideoBitRateMbps: 4,
                    WindowScale: 1.0,
                    IsRunning: false,
                    IsBusy: false,
                    Status: "Ready"));
                var requests = new List<VrAndroidMirrorControlRequestEventArgs>();
                window.AndroidMirrorRequested += (_, args) => requests.Add(args);
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());

                ClickDirect(window, source, "MirrorMenuButton");
                Assert.Equal(
                    Visibility.Visible,
                    Assert.IsType<Grid>(window.FindName("MirrorPage")).Visibility);
                ClickDirect(window, source, "MirrorNextDeviceButton");
                ClickDirect(window, source, "Mirror1080Button");
                ClickDirect(window, source, "Mirror120FpsButton");
                ClickDirect(window, source, "Mirror12MbpsButton");
                ClickDirect(window, source, "Mirror150ScaleButton");
                ClickDirect(window, source, "MirrorStartStopButton");

                Assert.Equal(6, requests.Count);
                Assert.Equal(
                    "192.168.1.10:5555",
                    requests[^1].State.DeviceSerial);
                Assert.Equal(1080, requests[^1].State.MaximumSize);
                Assert.Equal(120, requests[^1].State.MaximumFramesPerSecond);
                Assert.Equal(12, requests[^1].State.VideoBitRateMbps);
                Assert.Equal(1.5, requests[^1].State.WindowScale, 2);
                Assert.Equal(
                    VrAndroidMirrorControlRequestKind.ToggleConnection,
                    requests[^1].Kind);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "VR mirror panel interaction test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void ControlPanelPointerDiagnosticsAreDispatcherSafe()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new VrControlPanelWindow(new VrControlPanelState(
                    VoiceEnabled: true,
                    TranslationEnabled: false,
                    SendImmediately: true,
                    DisplayMode: VoiceTranslationDisplayModes.TranslationOnly,
                    TargetLanguage: "en-US",
                    ChunkIntervalMilliseconds: 1000,
                    CaptureEye: "left-eye"))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000
                };
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());
                var button = Assert.IsType<Button>(window.FindName("VoiceMenuButton"));
                var pointer = PointerFor(window, button);

                var controlName = InvokeFromWorker(() => source.PointerDown(pointer));
                var executed = InvokeFromWorker(() => source.PointerUp(pointer));

                Assert.Equal("VoiceMenuButton", controlName);
                Assert.True(executed);
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("VoicePage")).Visibility);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "VR control panel worker pointer test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void PointerSwipeMovesScrollableContentWithThePointerAndSuppressesClick()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var clickCount = 0;
                var button = new Button
                {
                    Name = "SwipeButton",
                    Content = "Swipe",
                    Height = 48
                };
                button.Click += (_, _) => clickCount++;
                var content = new StackPanel();
                content.Children.Add(button);
                content.Children.Add(new Border
                {
                    Height = 1200,
                    Background = Brushes.DimGray
                });
                var scrollViewer = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = content
                };
                ScrollViewer.SetCanContentScroll(scrollViewer, false);
                var window = new Window
                {
                    Width = 400,
                    Height = 300,
                    Content = scrollViewer,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000
                };
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());
                var pointer = PointerFor(window, button);
                var beforeY = button.TranslatePoint(new Point(0, 0), scrollViewer).Y;

                Assert.Equal("SwipeButton", source.PointerDown(pointer));
                Assert.True(source.BeginPointerSwipe(pointer));
                Assert.True(source.PointerSwipe(0.2));
                window.UpdateLayout();
                var afterY = button.TranslatePoint(new Point(0, 0), scrollViewer).Y;
                Assert.True(source.PointerSwipe(-0.2));
                window.UpdateLayout();
                source.EndPointerSwipe();

                Assert.True(afterY < beforeY);
                Assert.Equal(0, scrollViewer.VerticalOffset, 3);
                Assert.False(source.PointerUp(pointer));
                Assert.Equal(0, clickCount);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF swipe test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [WpfRenderingFact]
    public void WpfOverlayUsesItsOwnBorderOnlyWhileTouched()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new VrControlPanelWindow(new VrControlPanelState(
                    VoiceEnabled: true,
                    TranslationEnabled: false,
                    SendImmediately: true,
                    DisplayMode: VoiceTranslationDisplayModes.TranslationOnly,
                    TargetLanguage: "en-US",
                    ChunkIntervalMilliseconds: 1000,
                    CaptureEye: "left-eye"))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000
                };
                window.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());
                var frame = Assert.IsType<Border>(window.FindName("WindowFrame"));
                var captureButton = Assert.IsAssignableFrom<ButtonBase>(
                    window.FindName("CaptureMenuButton"));

                Assert.Equal(Colors.Transparent, ((SolidColorBrush)frame.BorderBrush).Color);
                var captureCenter = Assert.IsType<NormalizedPoint>(
                    source.FindNamedElementCenter("CaptureMenuButton"));
                source.PointerMove(captureCenter);
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                Assert.True(VrPointerHover.GetIsHovered(captureButton));
                var hovered = source.Render(840, 560);
                source.PointerLeave();
                Assert.False(VrPointerHover.GetIsHovered(captureButton));
                var normal = source.Render(840, 560);
                Assert.False(BitmapPixels(normal).SequenceEqual(BitmapPixels(hovered)));
                source.SetInteractionHighlighted(true);
                Assert.Equal(Color.FromRgb(46, 229, 140), ((SolidColorBrush)frame.BorderBrush).Color);
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                var highlighted = source.Render(840, 560);
                Assert.False(BitmapPixels(normal).SequenceEqual(BitmapPixels(highlighted)));
                source.SetInteractionHighlighted(false);
                Assert.Equal(Colors.Transparent, ((SolidColorBrush)frame.BorderBrush).Color);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF highlight test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Theory]
    [InlineData(ApplicationLanguages.English)]
    [InlineData(ApplicationLanguages.Chinese)]
    [InlineData(ApplicationLanguages.Japanese)]
    public void VrControlPanelRendersInEverySupportedLanguage(string language)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppLocalization.SetLanguage(language);
                var window = new VrControlPanelWindow(new VrControlPanelState(
                    VoiceEnabled: true,
                    TranslationEnabled: true,
                    SendImmediately: true,
                    DisplayMode: VoiceTranslationDisplayModes.OriginalThenTranslation,
                    TargetLanguage: "zh-CN",
                    ChunkIntervalMilliseconds: 1100,
                    CaptureEye: "right-eye"))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000
                };
                window.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());

                var bitmap = source.Render(840, 560);

                Assert.Equal(840, bitmap.PixelWidth);
                Assert.Equal(560, bitmap.PixelHeight);
                SaveVisualQa(bitmap, language, "main");
                source.SetInteractionHighlighted(true);
                SaveVisualQa(source.Render(840, 560), language, "main-contact");
                source.SetInteractionHighlighted(false);
                SaveVisualQa(
                    AssertPanelPageRenders(window, source, "VoiceMenuButton", "VoicePage"),
                    language,
                    "voice");
                AssertPanelPageRenders(window, source, "HomeButton", "MainPage");
                window.ApplySubtitleState(new VrSubtitleControlState(
                    SubtitleAsrBackends.SenseVoice,
                    ShowOriginalText: true,
                    TranslateText: true,
                    TargetLanguage: "zh-CN",
                    UseSpeakerColors: true,
                    DiarizationEnabled: true,
                    DiarizationCpuThreadCount: 2,
                    IsWindowOpen: false,
                    IsListening: false,
                    Status: AppLocalization.Text("Subtitle.Listening.Stopped")));
                SaveVisualQa(
                    AssertPanelPageRenders(window, source, "SubtitleMenuButton", "SubtitlePage"),
                    language,
                    "subtitles");
                AssertPanelPageRenders(window, source, "HomeButton", "MainPage");
                SaveVisualQa(
                    AssertPanelPageRenders(window, source, "SettingsMenuButton", "SettingsPage"),
                    language,
                    "settings");
                window.ApplyAndroidMirrorState(new VrAndroidMirrorControlState(
                    RuntimeInstalled: true,
                    Devices: [new VrAndroidMirrorDeviceOption("emulator-5554", "Pixel test device", true)],
                    DeviceSerial: "emulator-5554",
                    MaximumSize: 1080,
                    MaximumFramesPerSecond: 60,
                    VideoBitRateMbps: 12,
                    WindowScale: 1.0,
                    IsRunning: false,
                    IsBusy: false,
                    Status: AppLocalization.Text("AndroidMirror.Status.Ready")));
                AssertPanelPageRenders(window, source, "HomeButton", "MainPage");
                SaveVisualQa(
                    AssertPanelPageRenders(window, source, "MirrorMenuButton", "MirrorPage"),
                    language,
                    "mirror");
                Assert.Equal(window.MinWidth, window.MaxWidth);
                Assert.Equal(window.MinHeight, window.MaxHeight);
                Assert.Equal(SizeToContent.Manual, window.SizeToContent);
                Assert.InRange(source.AspectRatio, 1.49, 1.51);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "VR control panel render timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static BitmapSource AssertPanelPageRenders(
        VrControlPanelWindow window,
        WpfWindowOverlaySource source,
        string buttonName,
        string expectedPageName)
    {
        var button = Assert.IsAssignableFrom<ButtonBase>(window.FindName(buttonName));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.UpdateLayout();

        var page = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(expectedPageName));
        Assert.Equal(Visibility.Visible, page.Visibility);
        var bitmap = source.Render(840, 560);
        Assert.Equal(840, bitmap.PixelWidth);
        Assert.Equal(560, bitmap.PixelHeight);
        return bitmap;
    }

    [Fact]
    public void ControlPanelOnlyShowsExperimentalFeaturesEnabledByManager()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new VrControlPanelWindow(new VrControlPanelState(
                    VoiceEnabled: true,
                    TranslationEnabled: false,
                    SendImmediately: true,
                    DisplayMode: VoiceTranslationDisplayModes.TranslationOnly,
                    TargetLanguage: "en-US",
                    ChunkIntervalMilliseconds: 1000,
                    CaptureEye: "left-eye",
                    SubtitlesAvailable: false,
                    AndroidMirrorAvailable: false));

                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Button>(window.FindName("SubtitleMenuButton")).Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Button>(window.FindName("MirrorMenuButton")).Visibility);

                window.ApplyState(new VrControlPanelState(
                    VoiceEnabled: true,
                    TranslationEnabled: false,
                    SendImmediately: true,
                    DisplayMode: VoiceTranslationDisplayModes.TranslationOnly,
                    TargetLanguage: "en-US",
                    ChunkIntervalMilliseconds: 1000,
                    CaptureEye: "left-eye",
                    SubtitlesAvailable: true,
                    AndroidMirrorAvailable: true));

                Assert.Equal(
                    Visibility.Visible,
                    Assert.IsType<Button>(window.FindName("SubtitleMenuButton")).Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    Assert.IsType<Button>(window.FindName("MirrorMenuButton")).Visibility);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "VR feature visibility test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void ControlPanelVrHoverStartsClearAndClearsAgainOnPointerLeave()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = CreateControlPanelWindow();
                window.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());
                var button = Assert.IsType<Button>(window.FindName("CaptureMenuButton"));
                var voiceButton = Assert.IsType<Button>(window.FindName("VoiceMenuButton"));
                var center = Assert.IsType<NormalizedPoint>(
                    source.FindNamedElementCenter("CaptureMenuButton"));
                var voiceCenter = Assert.IsType<NormalizedPoint>(
                    source.FindNamedElementCenter("VoiceMenuButton"));

                Assert.False(VrPointerHover.GetIsHovered(button));
                Assert.False(source.SynchronizeBuffersRequested);
                source.PointerMove(center);
                Assert.True(VrPointerHover.GetIsHovered(button));
                Assert.True(source.SynchronizeBuffersRequested);
                Assert.True(source.ConsumeBufferSynchronizationRequest());
                source.PointerMove(voiceCenter);
                Assert.False(VrPointerHover.GetIsHovered(button));
                Assert.True(VrPointerHover.GetIsHovered(voiceButton));
                Assert.True(source.SynchronizeBuffersRequested);
                Assert.True(source.ConsumeBufferSynchronizationRequest());
                source.PointerLeave();
                Assert.False(VrPointerHover.GetIsHovered(button));
                Assert.False(VrPointerHover.GetIsHovered(voiceButton));
                Assert.True(source.SynchronizeBuffersRequested);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "VR hover reset test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static VrControlPanelWindow CreateControlPanelWindow() => new(
        new VrControlPanelState(
            VoiceEnabled: true,
            TranslationEnabled: false,
            SendImmediately: true,
            DisplayMode: VoiceTranslationDisplayModes.TranslationOnly,
            TargetLanguage: "en-US",
            ChunkIntervalMilliseconds: 1000,
            CaptureEye: "left-eye"))
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -32000,
        Top = -32000
    };

    private static void ClickDirect(
        VrControlPanelWindow window,
        WpfWindowOverlaySource source,
        string buttonName)
    {
        _ = Assert.IsAssignableFrom<ButtonBase>(window.FindName(buttonName));
        var located = source.FindNamedElementCenter(buttonName);
        Assert.True(located.HasValue, $"Control '{buttonName}' did not expose an overlay pointer center.");
        var pointer = located.Value;
        Assert.Equal(buttonName, source.PointerDown(pointer));
        Assert.True(source.PointerUp(pointer));
        window.UpdateLayout();
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
    }

    private static NormalizedPoint PointerFor(Window window, FrameworkElement element)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var center = element.TranslatePoint(
            new Point(element.ActualWidth / 2d, element.ActualHeight / 2d),
            root);
        return new NormalizedPoint(
            (float)(center.X / root.ActualWidth),
            (float)(center.Y / root.ActualHeight));
    }

    private static T InvokeFromWorker<T>(Func<T> action)
    {
        var task = Task.Run(action);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(5));
        }

        Assert.True(task.IsCompleted, "Worker operation did not complete while pumping the WPF dispatcher.");
        return task.GetAwaiter().GetResult();
    }

    private static void SaveVisualQa(BitmapSource bitmap, string language, string page)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(outputDirectory, $"{language}-control-{page}.png"));
        encoder.Save(stream);
    }

    private static byte[] BitmapPixels(BitmapSource bitmap)
    {
        var stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private sealed class DirectPixelTestWindow : Window, IWpfOverlayDirectPixelSource
    {
        private DirectOverlayPixelFrame? _frame;

        public long LatestDirectPixelSequence => _frame?.Sequence ?? long.MinValue;

        public DirectOverlayPixelRegion DirectPixelRegion { get; } =
            new(0.1f, 0.1f, 0.8f, 0.8f);

        public bool TryGetLatestDirectPixelFrame(out DirectOverlayPixelFrame frame)
        {
            if (_frame is { } current)
            {
                frame = current;
                return true;
            }

            frame = default;
            return false;
        }

        public void Publish(long sequence) => _frame = new DirectOverlayPixelFrame(
            new byte[4 * 4 * 4],
            4,
            4,
            sequence,
            DirectOverlayPixelFormat.Bgra);
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
