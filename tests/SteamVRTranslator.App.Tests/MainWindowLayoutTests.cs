using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SteamVRTranslator.App.Configuration;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class MainWindowLayoutTests
{
    [Fact]
    public void ProviderEditorWidthDoesNotDependOnFieldContent()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    Width = 1120,
                    Height = 760,
                    Left = 8,
                    Top = 8,
                    ShowInTaskbar = false
                };
                window.Show();
                foreach (var pageName in new[]
                         {
                             "CapturePage", "PromptsPage", "VoicePage", "SubtitlesPage", "AndroidMirrorPage", "DiagnosticsPage"
                         })
                {
                    Assert.IsAssignableFrom<FrameworkElement>(window.FindName(pageName)).Visibility =
                        Visibility.Collapsed;
                }
                Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ProvidersPage")).Visibility =
                    Visibility.Visible;

                var editor = Assert.IsType<StackPanel>(window.FindName("ProviderEditorPanel"));
                Assert.IsType<Border>(window.FindName("MockProviderPanel")).Visibility = Visibility.Collapsed;
                Assert.IsType<StackPanel>(window.FindName("CompatibleProviderPanel")).Visibility = Visibility.Visible;
                var baseUrl = Assert.IsType<TextBox>(window.FindName("BaseUrlTextBox"));
                baseUrl.Text = string.Empty;
                ArrangeWindow(window);
                var shortContentWidth = editor.ActualWidth;

                baseUrl.Text = "https://" + new string('w', 200) + ".example.com/v1";
                ArrangeWindow(window);
                var longContentWidth = editor.ActualWidth;

                Assert.InRange(shortContentWidth, 350, 610);
                Assert.Equal(shortContentWidth, longContentWidth, 3);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void DiagnosticsLogContentStartsAtTheTop()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var log = Assert.IsType<TextBox>(window.FindName("LogTextBox"));

                Assert.Equal(VerticalAlignment.Top, log.VerticalContentAlignment);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void VoiceChunkIntervalAndDefaultButtonHintUseSeparateRows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var interval = Assert.IsType<TextBox>(window.FindName("OscChunkIntervalTextBox"));
                var hint = Assert.IsType<TextBlock>(window.FindName("DefaultVrButtonValueText"));
                foreach (var pageName in new[]
                         {
                             "CapturePage", "ProvidersPage", "PromptsPage", "SubtitlesPage", "AndroidMirrorPage", "DiagnosticsPage"
                         })
                {
                    Assert.IsAssignableFrom<FrameworkElement>(window.FindName(pageName)).Visibility =
                        Visibility.Collapsed;
                }
                Assert.IsAssignableFrom<FrameworkElement>(window.FindName("VoicePage")).Visibility =
                    Visibility.Visible;
                ArrangeWindow(window);

                var intervalPanel = Assert.IsType<StackPanel>(interval.Parent);
                var intervalRow = Assert.IsType<Grid>(intervalPanel.Parent);
                var hintRow = Assert.IsType<Grid>(hint.Parent);
                var section = Assert.IsType<StackPanel>(intervalRow.Parent);
                var intervalOffset = VisualTreeHelper.GetOffset(intervalRow);
                var hintOffset = VisualTreeHelper.GetOffset(hintRow);

                Assert.Same(section, hintRow.Parent);
                Assert.True(section.Children.IndexOf(intervalRow) < section.Children.IndexOf(hintRow));
                Assert.True(
                    intervalOffset.Y + intervalRow.ActualHeight <= hintOffset.Y,
                    $"Interval bottom {intervalOffset.Y + intervalRow.ActualHeight:F1} overlaps hint top {hintOffset.Y:F1}.");
                Assert.Equal(TextWrapping.Wrap, hint.TextWrapping);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void DesktopVoiceHotKeyUsesAKeyCaptureButton()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var button = Assert.IsType<Button>(window.FindName("DesktopVoiceHotKeyButton"));

                Assert.Null(window.FindName("DesktopVoiceHotKeyComboBox"));
                Assert.False(string.IsNullOrWhiteSpace(button.Content?.ToString()));
                Assert.Equal(170, button.Width);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void ProviderActivationUsesSwitchAndListBadge()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var toggle = Assert.IsType<CheckBox>(window.FindName("ProviderActiveCheckBox"));
                var list = Assert.IsType<ListBox>(window.FindName("ProviderListBox"));

                Assert.Null(window.FindName("ActivateProviderButton"));
                Assert.NotNull(list.ItemTemplate);
                Assert.False(string.IsNullOrWhiteSpace(list.Tag?.ToString()));
                Assert.Equal(
                    Visibility.Visible,
                    new ProviderActiveVisibilityConverter().Convert(
                        ["provider", "PROVIDER"],
                        typeof(Visibility),
                        null!,
                        System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(
                    Visibility.Collapsed,
                    new ProviderActiveVisibilityConverter().Convert(
                        ["provider-a", "provider-b"],
                        typeof(Visibility),
                        null!,
                        System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(
                    list.SelectedItem is TranslationProviderConfiguration selected &&
                    string.Equals(selected.Id, list.Tag?.ToString(), StringComparison.OrdinalIgnoreCase),
                    toggle.IsChecked == true);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void NonExperimentalNavigationIconsUseOneColor()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow
                {
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 8,
                    Top = 8
                };
                window.Show();
                ArrangeWindow(window);
                var standardButtons = new[]
                {
                    window.CaptureNavButton,
                    window.ProviderNavButton,
                    window.PromptsNavButton,
                    window.VoiceNavButton,
                    window.DiagnosticsNavButton
                };
                var experimentalButtons = new[]
                {
                    window.SubtitlesNavButton,
                    window.AndroidMirrorNavButton
                };
                var standardColors = standardButtons
                    .Select(button => FindVisualDescendant<System.Windows.Shapes.Path>(button).Fill.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var experimentalColors = experimentalButtons
                    .Select(button => FindVisualDescendant<System.Windows.Shapes.Path>(button).Fill.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Assert.Single(standardColors);
                Assert.Single(experimentalColors);
                Assert.NotEqual(standardColors[0], experimentalColors[0]);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    private static T FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            try
            {
                return FindVisualDescendant<T>(child);
            }
            catch (InvalidOperationException)
            {
                // Search the next branch.
            }
        }

        throw new InvalidOperationException($"No visual descendant of type {typeof(T).Name} was found.");
    }

    private static void ArrangeWindow(Window window)
    {
        if (window.IsLoaded)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            return;
        }

        window.InvalidateMeasure();
        window.Measure(new Size(1120, 760));
        window.Arrange(new Rect(0, 0, 1120, 760));
        window.UpdateLayout();
    }
}
