using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Localization;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class MainWindowVisualLayoutTests
{
    private static readonly WindowView[] Views =
    [
        new("capture", "CaptureNavButton"),
        new(
            "providers",
            "ProviderNavButton",
            ProviderType: TranslationProviderConfiguration.MockType),
        new(
            "providers-google",
            "ProviderNavButton",
            ProviderType: TranslationProviderConfiguration.GoogleAiStudioType),
        new("prompts-direct", "PromptsNavButton", 0),
        new("prompts-layout", "PromptsNavButton", 1),
        new("prompts-custom", "PromptsNavButton", 2),
        new("prompts-voice", "PromptsNavButton", 3),
        new("prompts-subtitle", "PromptsNavButton", 4),
        new(
            "prompts-direct-advanced",
            "PromptsNavButton",
            0,
            "DirectPromptAdvancedExpander"),
        new(
            "prompts-layout-advanced",
            "PromptsNavButton",
            1,
            "LayoutPromptAdvancedExpander"),
        new(
            "prompts-custom-advanced",
            "PromptsNavButton",
            2,
            "CustomPromptAdvancedExpander"),
        new(
            "prompts-voice-advanced",
            "PromptsNavButton",
            3,
            "VoicePromptAdvancedExpander"),
        new(
            "prompts-subtitle-advanced",
            "PromptsNavButton",
            4,
            "SubtitlePromptAdvancedExpander"),
        new("voice", "VoiceNavButton"),
        new("subtitles", "SubtitlesNavButton"),
        new(
            "subtitles-vibevoice",
            "SubtitlesNavButton",
            SubtitleBackend: SubtitleAsrBackends.VibeVoiceApi),
        new("android-mirror", "AndroidMirrorNavButton"),
        new("diagnostics", "DiagnosticsNavButton")
    ];

    private static readonly WindowSize[] Sizes =
    [
        new("default", 1120, 760),
        new("minimum", 920, 640)
    ];

    [Fact]
    public void EveryLocalizedPageHasNoTextClippingOrOverlap()
    {
        Exception? failure = null;
        var originalLanguage = AppLocalization.Instance.Language;
        var outputDirectory = Environment.GetEnvironmentVariable("STEAMVR_TRANSLATOR_VISUAL_QA_DIR");
        var thread = new Thread(() =>
        {
            try
            {
                var allIssues = new List<string>();
                foreach (var language in ApplicationLanguages.Supported)
                {
                    using var scope = CreateLocalizedWindow(language);
                    foreach (var size in Sizes)
                    {
                        SetWindowSize(scope, size);
                        foreach (var view in Views)
                        {
                            ActivateView(scope.Window, view);
                            var issues = FindLayoutIssues(scope.Window);
                            allIssues.AddRange(issues.Select(
                                issue => $"{language}/{view.Name}/{size.Name}: {issue}"));

                            if (!string.IsNullOrWhiteSpace(outputDirectory))
                            {
                                Directory.CreateDirectory(outputDirectory);
                                var path = Path.Combine(
                                    outputDirectory,
                                    $"{language}-{view.Name}-{size.Name}.png");
                                CaptureVisual(scope.Root, path);
                            }
                        }
                    }
                }

                Assert.True(
                    allIssues.Count == 0,
                    $"Localized layout issues:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, allIssues));
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
    public void CapturePageOmitsLegacySourceAndDesktopHotKeyControls()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = CreateLocalizedWindow(ApplicationLanguages.English);
                Assert.Null(scope.Window.FindName("CaptureSourceTextBox"));
                Assert.Null(scope.Window.FindName("HotKeyComboBox"));
                Assert.Equal(Visibility.Collapsed, scope.Window.StatusText.Visibility);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    private static WindowScope CreateLocalizedWindow(string language)
    {
        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            MinWidth = 0,
            MinHeight = 0,
            Width = 620,
            Height = 420,
            Left = 8,
            Top = 8,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true
        };
        var configuration = Assert.IsType<AppConfiguration>(typeof(MainWindow)
            .GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window));
        configuration.UiLanguage = language;
        configuration.ApplyPromptLanguage();
        AppLocalization.SetLanguage(language);
        window.UiLanguageComboBox.SelectedItem = window.UiLanguageComboBox.Items
            .OfType<ComboBoxItem>()
            .Single(item => string.Equals(item.Tag as string, language, StringComparison.Ordinal));
        typeof(MainWindow)
            .GetMethod("PopulatePromptControls", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        typeof(MainWindow)
            .GetMethod("RefreshLocalizedDynamicText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        window.Content = null;
        var viewBox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = root
        };
        window.Content = viewBox;
        window.Show();
        PumpLayout(window);
        return new WindowScope(window, root);
    }

    private static void SetWindowSize(WindowScope scope, WindowSize size)
    {
        scope.Root.Width = size.Width;
        scope.Root.Height = size.Height;
        PumpLayout(scope.Window);
    }

    private static void ActivateView(MainWindow window, WindowView view)
    {
        var button = Assert.IsType<Button>(window.FindName(view.NavigationButton));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (view.PromptTab is int tab)
        {
            window.PromptTabControl.SelectedIndex = tab;
        }
        Expander? advancedExpander = null;
        foreach (var expanderName in new[]
                 {
                     "DirectPromptAdvancedExpander",
                     "LayoutPromptAdvancedExpander",
                     "CustomPromptAdvancedExpander",
                     "VoicePromptAdvancedExpander",
                     "SubtitlePromptAdvancedExpander"
                 })
        {
            var expander = Assert.IsType<Expander>(window.FindName(expanderName));
            expander.IsExpanded =
                string.Equals(expanderName, view.PromptAdvancedExpander, StringComparison.Ordinal);
            if (expander.IsExpanded)
            {
                advancedExpander = expander;
            }
        }
        if (!string.IsNullOrWhiteSpace(view.SubtitleBackend))
        {
            SelectSubtitleBackendForVisualTest(window, view.SubtitleBackend);
        }
        if (!string.IsNullOrWhiteSpace(view.ProviderType))
        {
            SelectProviderTypeForVisualTest(window, view.ProviderType);
        }

        PumpLayout(window);
        if (advancedExpander is not null)
        {
            advancedExpander.BringIntoView();
            PumpLayout(window);
        }
    }

    private static void SelectSubtitleBackendForVisualTest(MainWindow window, string backend)
    {
        var selectionChangedMethod = typeof(MainWindow).GetMethod(
            "SubtitleAsrBackendComboBox_SelectionChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var selectionChangedHandler = (SelectionChangedEventHandler)selectionChangedMethod.CreateDelegate(
            typeof(SelectionChangedEventHandler),
            window);
        window.SubtitleAsrBackendComboBox.SelectionChanged -= selectionChangedHandler;
        try
        {
            window.SubtitleAsrBackendComboBox.SelectedItem = window.SubtitleAsrBackendComboBox.Items
                .OfType<ComboBoxItem>()
                .Single(item => string.Equals(
                    item.Tag as string,
                    backend,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            window.SubtitleAsrBackendComboBox.SelectionChanged += selectionChangedHandler;
        }

        typeof(MainWindow)
            .GetMethod("UpdateSubtitleAsrPanel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
    }

    private static void SelectProviderTypeForVisualTest(MainWindow window, string providerType)
    {
        var provider = window.ProviderListBox.Items
            .OfType<TranslationProviderConfiguration>()
            .FirstOrDefault(item => string.Equals(
                item.Type,
                providerType,
                StringComparison.OrdinalIgnoreCase));
        if (provider is null &&
            string.Equals(
                providerType,
                TranslationProviderConfiguration.GoogleAiStudioType,
                StringComparison.OrdinalIgnoreCase))
        {
            window.AddProviderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.ProviderTypeComboBox.SelectedItem = window.ProviderTypeComboBox.Items
                .OfType<ComboBoxItem>()
                .Single(item => string.Equals(
                    item.Tag as string,
                    providerType,
                    StringComparison.OrdinalIgnoreCase));
            return;
        }

        window.ProviderListBox.SelectedItem = Assert.IsType<TranslationProviderConfiguration>(
            provider);
    }

    private static void PumpLayout(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static IReadOnlyList<string> FindLayoutIssues(MainWindow window)
    {
        var issues = new List<string>();
        var textElements = Descendants(window)
            .OfType<TextBlock>()
            .Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text))
            .Select(text => new ElementBounds(text, BoundsInWindow(text, window)))
            .Where(value => value.Bounds is not null)
            .ToArray();

        foreach (var value in textElements)
        {
            var text = (TextBlock)value.Element;
            var bounds = value.Bounds!.Value;
            if (bounds.Left < -1 || bounds.Right > window.ActualWidth + 1)
            {
                issues.Add($"Horizontal overflow: {Describe(text)} at {bounds}.");
            }

            if (text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.None)
            {
                var dpi = VisualTreeHelper.GetDpi(text);
                var formatted = new FormattedText(
                    text.Text,
                    CultureInfo.CurrentUICulture,
                    text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                    text.FontSize,
                    text.Foreground ?? Brushes.Black,
                    dpi.PixelsPerDip);
                if (formatted.WidthIncludingTrailingWhitespace > text.ActualWidth + 1.5)
                {
                    issues.Add(
                        $"Clipped text: {Describe(text)} needs {formatted.WidthIncludingTrailingWhitespace:F1}px " +
                        $"but has {text.ActualWidth:F1}px.");
                }
            }
        }

        for (var firstIndex = 0; firstIndex < textElements.Length; firstIndex++)
        {
            var first = textElements[firstIndex];
            for (var secondIndex = firstIndex + 1; secondIndex < textElements.Length; secondIndex++)
            {
                var second = textElements[secondIndex];
                if (IsAncestor(first.Element, second.Element) || IsAncestor(second.Element, first.Element))
                {
                    continue;
                }

                var intersection = Rect.Intersect(first.Bounds!.Value, second.Bounds!.Value);
                if (!intersection.IsEmpty && intersection.Width > 1 && intersection.Height > 1)
                {
                    issues.Add(
                        $"Text overlap: {Describe((TextBlock)first.Element)} and " +
                        $"{Describe((TextBlock)second.Element)} at {intersection}.");
                }
            }
        }

        return issues;
    }

    private static Rect? BoundsInWindow(FrameworkElement element, Window window)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return null;
        }

        try
        {
            var bounds = element.TransformToAncestor(window).TransformBounds(
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            for (var current = VisualTreeHelper.GetParent(element);
                 current is not null && !ReferenceEquals(current, window);
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is not FrameworkElement viewport ||
                    current is not (ScrollViewer or ScrollContentPresenter) ||
                    viewport.ActualWidth <= 0 ||
                    viewport.ActualHeight <= 0)
                {
                    continue;
                }

                var viewportBounds = viewport.TransformToAncestor(window).TransformBounds(
                    new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
                bounds = Rect.Intersect(bounds, viewportBounds);
                if (bounds.IsEmpty)
                {
                    return null;
                }
            }

            return bounds;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsAncestor(DependencyObject candidate, DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(TextBlock text)
    {
        var value = text.Text.Replace(Environment.NewLine, " ");
        return value.Length <= 48 ? $"'{value}'" : $"'{value[..45]}...'";
    }

    private static void CaptureVisual(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        File.Delete(path);
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class WindowScope(MainWindow window, FrameworkElement root) : IDisposable
    {
        public MainWindow Window { get; } = window;

        public FrameworkElement Root { get; } = root;

        public void Dispose() => Window.Close();
    }

    private sealed record WindowView(
        string Name,
        string NavigationButton,
        int? PromptTab = null,
        string? PromptAdvancedExpander = null,
        string? SubtitleBackend = null,
        string? ProviderType = null);

    private sealed record WindowSize(string Name, double Width, double Height);

    private sealed record ElementBounds(FrameworkElement Element, Rect? Bounds);

}
