using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamVRTranslator.App.AndroidMirror;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Subtitles;
using SteamVRTranslator.App.Translation;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class OverlayAppearanceTests
{
    [Fact]
    public void OldAndInvalidSettingsFallBackWithoutLosingCustomColors()
    {
        Assert.Equal("classic", JsonSerializer.Deserialize<AppConfiguration>("{}")!.OverlayAppearance.Preset);
        var configuration = new AppConfiguration
        {
            OverlayAppearance = new() { Preset = "unknown", Background = "invalid", Accent = " #ab1234 " }
        };
        ConfigurationStore.NormalizePrompts(configuration);
        Assert.Equal("classic", configuration.OverlayAppearance.Preset);
        Assert.Equal("#18201D", configuration.OverlayAppearance.Background);
        Assert.Equal("#AB1234", configuration.OverlayAppearance.Accent);
        var copy = JsonSerializer.Deserialize<AppConfiguration>(JsonSerializer.Serialize(configuration))!;
        Assert.Equal(configuration.OverlayAppearance, copy.OverlayAppearance);
    }

    [WpfRenderingFact]
    public void CachedResultsEchoAndToolbarChangeColorAndRestoreExactly() => RunSta(() =>
    {
        var renderer = new OverlayRenderer();
        var owner = new object();
        var plane = new SpatialSelectionPlane(default, new Vector3f(1, 0, 0), new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1), .4f, .3f, .5f, default, new NormalizedPoint(1, 1), 0);
        var result = new ResultOverlaySnapshot(1, "# Hello\n你好、こんにちは。", "Hello", ResultContentFormat.Markdown,
            null, ResultStatus.Completed, 0, true, DateTimeOffset.Now);
        var echo = new VoiceInputEchoSnapshot(1, "Voice.Echo.Sent", "你好，Hello、こんにちは。");
        var toolbar = new WpfOverlayToolbar(InteractiveOverlayKind.Result);
        OverlayTheme.Instance.Apply(new());
        var classicResult = renderer.RenderResults(result, plane, cacheOwner: owner);
        var classicEcho = renderer.RenderVoiceEcho(echo);
        var classicToolbar = Pixels(toolbar.Render(140, 32, null, false));
        var classicRay = renderer.RenderPointerRay();
        var photo = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 220, 255, 0, 180, 0, 255, 230, 0, 0, 255, 40, 70, 190, 255 }, 8);
        var imageEncoder = new PngBitmapEncoder();
        imageEncoder.Frames.Add(BitmapFrame.Create(photo));
        using var imageStream = new MemoryStream();
        imageEncoder.Save(imageStream);
        var photoBytes = imageStream.ToArray();
        var classicCapture = renderer.RenderCapture(photoBytes, plane);
        OverlayTheme.Instance.Apply(new() { Preset = "light" });
        var lightResult = renderer.RenderResults(result, plane, cacheOwner: owner);
        var lightEcho = renderer.RenderVoiceEcho(echo);
        Assert.False(classicResult.Pixels.SequenceEqual(lightResult.Pixels));
        Assert.False(classicEcho.Pixels.SequenceEqual(lightEcho.Pixels));
        Assert.False(classicToolbar.SequenceEqual(Pixels(toolbar.Render(140, 32, null, false))));
        Assert.False(classicRay.Pixels.SequenceEqual(renderer.RenderPointerRay().Pixels));
        Assert.Equal(classicCapture.Pixels, renderer.RenderCapture(photoBytes, plane).Pixels);
        Assert.Equal(classicEcho.PixelWidth, lightEcho.PixelWidth);
        Assert.Equal(classicEcho.PixelHeight, lightEcho.PixelHeight);
        Assert.Equal(classicResult.MaximumScrollOffset, lightResult.MaximumScrollOffset);
        ExportFrame("classic-echo", classicEcho);
        ExportFrame("light-echo", lightEcho);
        ExportFrame("light-result", new(lightResult.Pixels, lightResult.PixelWidth, lightResult.PixelHeight));
        OverlayTheme.Instance.Apply(new() { Preset = "monochrome" });
        foreach (var role in Enum.GetValues<OverlayColorRole>())
        {
            var color = OverlayTheme.Resolve(role, Color.FromArgb(137, 60, 120, 180));
            Assert.Equal(color.R, color.G);
            Assert.Equal(color.G, color.B);
            Assert.Equal(137, color.A);
        }
        var monochromeEcho = renderer.RenderVoiceEcho(echo);
        Assert.False(monochromeEcho.Pixels.SequenceEqual(lightEcho.Pixels));
        ExportFrame("monochrome-echo", monochromeEcho);
        OverlayTheme.Instance.Apply(new());
        Assert.Equal(classicResult.Pixels, renderer.RenderResults(result, plane, cacheOwner: owner).Pixels);
        Assert.Equal(classicEcho.Pixels, renderer.RenderVoiceEcho(echo).Pixels);
        Assert.Equal(classicToolbar, Pixels(toolbar.Render(140, 32, null, false)));
    });

    [WpfRenderingFact]
    public void OpenWindowsAndHighlightBindingsFollowPaletteWithoutRecreation() => RunSta(() =>
    {
        var history = new SubtitleHistoryViewModel(new() { TranslateText = true });
        var entry = history.Add(1, TimeSpan.Zero, TimeSpan.FromSeconds(2), "Hello, can you hear me?");
        entry.TranslatedText = "你好，能听到我说话吗？";
        var control = new VrControlPanelWindow(new(true, true, true, "translation-only", "zh-CN", 1500, "left-eye"));
        var subtitles = new SubtitleHistoryWindow(history);
        var session = new ScrcpyAndroidSession(new AndroidMirrorRuntimeService(), new AppLog(AppContext.BaseDirectory));
        var mirror = new AndroidMirrorWindow(session);
        var windows = new (string Name, Window Window)[] { ("control", control), ("subtitles", subtitles), ("mirror", mirror) };
        try
        {
            foreach (var (name, window) in windows)
            {
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -30000;
                window.Top = -30000;
                using var source = new WpfWindowOverlaySource(window, new());
                OverlayTheme.Instance.Apply(new());
                Pump(window);
                var classic = source.RenderPixels((int)window.Width, (int)window.Height);
                Assert.False(source.NeedsRender);
                OverlayTheme.Instance.Apply(new() { Preset = "light" });
                Pump(window);
                Assert.True(source.NeedsRender);
                var light = source.RenderPixels((int)window.Width, (int)window.Height);
                Assert.False(source.NeedsRender);
                Assert.False(Pixels(classic).SequenceEqual(Pixels(light)));
                ExportBitmap($"classic-{name}", classic);
                ExportBitmap($"light-{name}", light);
                ((IWpfOverlayInteractionHighlightAware)window).SetInteractionHighlighted(true);
                OverlayTheme.Instance.Apply(new() { Preset = "custom", Accent = "#C1519A" });
                Pump(window);
                var border = Assert.IsType<Border>(window.FindName("WindowFrame"));
                Assert.Equal(OverlayTheme.Parse("#C1519A"), Assert.IsType<SolidColorBrush>(border.BorderBrush).Color);
            }
        }
        finally
        {
            control.Close();
            subtitles.ClosePermanently();
            mirror.Close();
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    });

    [WpfRenderingFact]
    public void SettingsApplyImmediatelyPersistAndKeepCustomColorsAcrossPresets() => RunSta(() =>
    {
        var store = new ConfigurationStore();
        var previousFile = File.Exists(store.FilePath) ? File.ReadAllText(store.FilePath) : null;
        MainWindow? window = null;
        try
        {
            store.Save(new AppConfiguration());
            window = new MainWindow { ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
            window.Show();
            var presets = Assert.IsType<ComboBox>(window.FindName("OverlayPresetComboBox"));
            void Select(string name) => presets.SelectedItem = presets.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == name);
            Select("custom");
            var background = Assert.IsType<TextBox>(window.FindName("OverlayBackgroundTextBox"));
            background.Text = "#202044";
            window.GetType().GetMethod("OverlayColorsApply_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.Equal("#202044", store.Load().OverlayAppearance.Background);
            Select("light");
            Assert.Equal("light", store.Load().OverlayAppearance.Preset);
            Pump(window);
            ExportBitmap("light-manager", RenderWindow(window));
            Select("monochrome");
            Assert.Equal("monochrome", store.Load().OverlayAppearance.Preset);
            Pump(window);
            ExportBitmap("monochrome-manager", RenderWindow(window));
            Select("custom");
            Assert.Equal("#202044", background.Text);
            Assert.Equal(OverlayTheme.Parse("#202044"), OverlayTheme.Resolve(OverlayColorRole.Background, Colors.Black));
            Pump(window);
            ExportBitmap("custom-manager", RenderWindow(window));
            background.Text = "invalid";
            window.GetType().GetMethod("OverlayColorsApply_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, new object[] { window, new RoutedEventArgs() });
            Assert.Equal("#202044", store.Load().OverlayAppearance.Background);
            Assert.NotEmpty(Assert.IsType<TextBlock>(window.FindName("OverlayColorError")).Text);
            Select("classic");
            Assert.Equal("classic", store.Load().OverlayAppearance.Preset);
        }
        finally
        {
            window?.Close();
            if (previousFile is null) File.Delete(store.FilePath); else File.WriteAllText(store.FilePath, previousFile);
        }
    });

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
            finally { OverlayTheme.Instance.Apply(new()); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Theme rendering timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }

    private static BitmapSource RenderWindow(Window window)
    {
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)window.Content);
        return bitmap;
    }

    private static void ExportFrame(string name, OverlayRenderFrame frame)
    {
        var bgra = (byte[])frame.Pixels.Clone();
        for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        ExportBitmap(name, BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Bgra32, null, bgra, frame.PixelWidth * 4));
    }

    private static void ExportBitmap(string name, BitmapSource bitmap)
    {
        var directory = Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_THEME_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
}
