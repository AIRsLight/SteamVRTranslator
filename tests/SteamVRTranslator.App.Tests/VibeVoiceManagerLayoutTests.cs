using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SteamVRTranslator.VibeVoice.Manager;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class VibeVoiceManagerLayoutTests
{
    [WpfRenderingFact]
    public void DefaultAndMinimumSizesKeepManagerActionsInsideTheViewport()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var cultureName in new[] { "en-US", "zh-CN", "ja-JP" })
                {
                    Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                    foreach (var size in new[] { new Size(980, 700), new Size(880, 620) })
                    {
                        var window = new SteamVRTranslator.VibeVoice.Manager.MainWindow(
                            new ManagerStartupOptions(
                                new Uri("http://127.0.0.1:5090"),
                                string.Empty,
                                StartLocalService: false));
                        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                        root.Measure(size);
                        root.Arrange(new Rect(new Point(0, 0), size));
                        root.UpdateLayout();

                        var startButton = Find<Button>(window, "StartButton");
                        var stopButton = Find<Button>(window, "StopButton");
                        var runtimeButton = Find<Button>(window, "InstallRuntimeButton");
                        var modelButton = Find<Button>(window, "InstallModelButton");
                        var installAllButton = Find<Button>(window, "InstallAllButton");
                        AssertInside(root, startButton);
                        AssertInside(root, stopButton);
                        AssertInside(root, runtimeButton);
                        AssertInside(root, modelButton);
                        AssertInside(root, installAllButton);
                        Assert.True(runtimeButton.ActualWidth >= 100);
                        Assert.True(modelButton.ActualWidth >= 100);
                        window.Close();
                    }
                }
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

    private static void AssertInside(FrameworkElement root, FrameworkElement control)
    {
        var origin = control.TranslatePoint(new Point(0, 0), root);
        Assert.InRange(origin.X, 0, root.ActualWidth - control.ActualWidth + 0.5);
        Assert.InRange(origin.Y, 0, root.ActualHeight - control.ActualHeight + 0.5);
    }

    private static T Find<T>(FrameworkElement root, string name)
        where T : FrameworkElement =>
        Assert.IsType<T>(root.FindName(name));
}
