using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.Speech;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class SenseVoiceSetupDialogTests
{
    [Fact]
    public void DialogFitsEverySupportedInterfaceLanguage()
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
                    var dialog = new SenseVoiceSetupDialog(
                        "Q8_0 · 242.4 MiB",
                        [
                            AppLocalization.Text("Asr.Asset.CpuRuntime"),
                            AppLocalization.Text("Asr.Asset.VulkanRuntime"),
                            AppLocalization.Text("Asr.Asset.Vad")
                        ],
                        useMirror: false)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 8,
                        Top = 8,
                        ShowInTaskbar = false
                    };
                    dialog.Show();
                    dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    dialog.UpdateLayout();

                    Assert.InRange(dialog.ActualHeight, 360, dialog.MaxHeight);
                    Assert.True(dialog.InstallButton.IsVisible);
                    Assert.True(dialog.OfficialSourceRadioButton.IsVisible);
                    Assert.True(dialog.MirrorSourceRadioButton.IsVisible);
                    Assert.True(dialog.MissingAssetsText.IsVisible);

                    if (!string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        Directory.CreateDirectory(outputDirectory);
                        CaptureVisual(
                            Assert.IsAssignableFrom<FrameworkElement>(dialog.Content),
                            Path.Combine(outputDirectory, $"{language}-sensevoice-setup.png"));
                    }

                    dialog.Close();
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
    public void DialogDisplaysMissingAssetsAndAllowsChoosingEitherSource()
    {
        Exception? failure = null;
        var originalLanguage = AppLocalization.Instance.Language;
        var thread = new Thread(() =>
        {
            try
            {
                AppLocalization.SetLanguage(ApplicationLanguages.Chinese);
                var dialog = new SenseVoiceSetupDialog(
                    "Q5_0 · 159.4 MiB",
                    ["SenseVoice CPU 运行时", "FSMN VAD"],
                    useMirror: true);
                Assert.True(dialog.UseMirror);
                Assert.Equal("Q5_0 · 159.4 MiB", dialog.ModelValueText.Text);
                Assert.Contains("SenseVoice CPU 运行时", dialog.MissingAssetsText.Text);
                Assert.Contains("FSMN VAD", dialog.MissingAssetsText.Text);

                dialog.OfficialSourceRadioButton.IsChecked = true;
                Assert.False(dialog.UseMirror);
                dialog.Close();
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

    private static void CaptureVisual(FrameworkElement visual, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
