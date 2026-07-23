using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Subtitles;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class SubtitleHistoryTests
{
    [Fact]
    public void SpeakerRegistry_ReusesCloseEmbeddingAndCreatesDifferentSpeaker()
    {
        var registry = new SpeakerIdentityRegistry(0.8);

        var first = registry.Resolve([1f, 0f, 0f]);
        var same = registry.Resolve([0.99f, 0.05f, 0f]);
        var different = registry.Resolve([0f, 1f, 0f]);

        Assert.Equal(first, same);
        Assert.NotEqual(first, different);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void SpeakerColorRegistry_ReturnsStableDistinctBrushes()
    {
        var colors = new SpeakerColorRegistry();

        var first = colors.GetBrush(0);

        Assert.Same(first, colors.GetBrush(0));
        Assert.NotEqual(first.ToString(), colors.GetBrush(1).ToString());
    }

    [Fact]
    public void SubtitleVrWindowUsesItsOwnCloseButtonAndSixtyFpsBudget()
    {
        var options = MainWindow.CreateSubtitleOverlayOptions(
            new SubtitleConfiguration
            {
                WindowWidthMeters = 0.42,
                WindowDistanceMeters = 0.75
            },
            WpfSpatialOverlayPlacement.LeftHand);

        Assert.False(options.ShowToolbarWhenGrabbed);
        Assert.Equal(60, options.MaximumFramesPerSecond);
        Assert.Equal(0.42f, options.WidthMeters);
        Assert.Equal(WpfSpatialOverlayPlacement.LeftHand, options.Placement);
    }

    [Fact]
    public void History_TrimsOldestEntriesAtConfiguredLimit()
    {
        var configuration = new SubtitleConfiguration
        {
            MaximumHistoryEntries = 10,
            MaximumHistoryCharacters = 500000
        };
        var history = new SubtitleHistoryViewModel(configuration);

        for (var index = 0; index < 12; index++)
        {
            history.Add(
                index % 2,
                TimeSpan.FromSeconds(index),
                TimeSpan.FromSeconds(index + 1),
                $"entry-{index}");
        }

        Assert.Equal(10, history.Entries.Count);
        Assert.Equal("entry-2", history.Entries[0].SourceText);
        Assert.Equal("entry-11", history.Entries[^1].SourceText);
    }

    [Fact]
    public void HistoryWindowRendersLongEntriesInEverySupportedLanguage()
    {
        Exception? failure = null;
        var originalLanguage = AppLocalization.Instance.Language;
        var outputDirectory = Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_VISUAL_QA_DIR");
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var language in ApplicationLanguages.Supported)
                {
                    AppLocalization.SetLanguage(language);
                    var history = new SubtitleHistoryViewModel(new SubtitleConfiguration());
                    var entry = history.Add(
                        0,
                        TimeSpan.FromSeconds(62),
                        TimeSpan.FromSeconds(69),
                        SourceSample(language));
                    entry.TranslatedText = TranslationSample(language);
                    entry.IsTranslating = false;

                    var window = new SubtitleHistoryWindow(history)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 8,
                        Top = 8,
                        ShowInTaskbar = false
                    };
                    window.Show();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    using var source = new WpfWindowOverlaySource(
                        window,
                        new WpfSpatialOverlayOptions());
                    var bitmap = source.Render(720, 480);

                    var historyList = Assert.IsType<System.Windows.Controls.ListBox>(
                        window.FindName("HistoryListBox"));
                    Assert.True(historyList.ActualWidth > 600);
                    Assert.True(historyList.ActualHeight > 300);
                    Assert.Equal(
                        ScrollBarVisibility.Hidden,
                        ScrollViewer.GetVerticalScrollBarVisibility(historyList));
                    Assert.Equal(720, bitmap.PixelWidth);
                    Assert.Equal(480, bitmap.PixelHeight);
                    if (!string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        Directory.CreateDirectory(outputDirectory);
                        CaptureBitmap(bitmap, Path.Combine(outputDirectory, $"{language}-subtitle-history.png"));
                        source.SetInteractionHighlighted(true);
                        CaptureBitmap(
                            source.Render(720, 480),
                            Path.Combine(outputDirectory, $"{language}-subtitle-history-contact.png"));
                        source.SetInteractionHighlighted(false);
                    }

                    window.ClosePermanently();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                AppLocalization.SetLanguage(originalLanguage);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void HistoryWindowInvalidatesForEntriesAndStreamingTranslationUpdates()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var history = new SubtitleHistoryViewModel(new SubtitleConfiguration());
                var window = new SubtitleHistoryWindow(history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false
                };
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions { MaximumFramesPerSecond = 60 });
                source.RenderPixels(1024, 675);
                Assert.False(source.NeedsRender);

                var entry = history.Add(0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "source");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(source.NeedsRender);
                source.RenderPixels(1024, 675);
                Assert.False(source.NeedsRender);

                entry.TranslatedText = "streamed translation";
                Assert.True(source.NeedsRender);
                source.RenderPixels(1024, 675);
                Assert.False(source.NeedsRender);
                window.ClosePermanently();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Subtitle invalidation test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void HistoryWindowCloseButtonIsReachableThroughOverlayCoordinates()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var history = new SubtitleHistoryViewModel(new SubtitleConfiguration());
                var window = new SubtitleHistoryWindow(history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false
                };
                var closeRequested = false;
                window.CloseRequested += (_, _) => closeRequested = true;
                window.Show();
                window.UpdateLayout();

                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());
                var closeCenter = source.FindNamedElementCenter("CloseButton");

                Assert.NotNull(closeCenter);
                Assert.Equal("CloseButton", source.PointerDown(closeCenter.Value));
                Assert.True(source.PointerUp(closeCenter.Value));
                Assert.True(closeRequested);
                window.ClosePermanently();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Subtitle close button test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void HistoryWindowListeningButtonIsReachableAndReflectsState()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var history = new SubtitleHistoryViewModel(new SubtitleConfiguration());
                var window = new SubtitleHistoryWindow(history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false
                };
                var requested = false;
                window.StartStopListeningRequested += (_, _) => requested = true;
                window.Show();
                window.UpdateLayout();
                using var source = new WpfWindowOverlaySource(
                    window,
                    new WpfSpatialOverlayOptions());
                var center = source.FindNamedElementCenter("ListenButton");

                Assert.NotNull(center);
                Assert.Equal("ListenButton", source.PointerDown(center.Value));
                Assert.True(source.PointerUp(center.Value));
                Assert.True(requested);
                window.ApplyListeningState(SubtitleListeningState.Listening, "listening");
                Assert.Equal(
                    AppLocalization.Text("Subtitle.Window.StopListening"),
                    Assert.IsType<TextBlock>(window.FindName("ListenButtonText")).Text);
                window.ClosePermanently();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Subtitle listening button test timed out.");
        Assert.Null(failure);
    }

    private static string SourceSample(string language) => language switch
    {
        ApplicationLanguages.Chinese => "欢迎来到实验性实时字幕。这里是一段较长的原文，用于确认说话人、时间戳以及自动换行在窗口中不会重叠。",
        ApplicationLanguages.Japanese => "実験的なリアルタイム字幕へようこそ。話者、タイムスタンプ、自動折り返しが重ならないことを確認するための長い原文です。",
        _ => "Welcome to experimental live subtitles. This longer source sentence verifies speaker labels, timestamps, and wrapping without overlap."
    };

    private static string TranslationSample(string language) => language switch
    {
        ApplicationLanguages.Chinese => "这是对应的译文。会议记录会保留原文与译文，并为匿名说话人使用稳定颜色。",
        ApplicationLanguages.Japanese => "これは対応する翻訳です。履歴には原文と訳文が残り、匿名話者には一貫した色が使われます。",
        _ => "This is the translated text. The history keeps source and translation with stable colors for anonymous speakers."
    };

    private static void CaptureBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
