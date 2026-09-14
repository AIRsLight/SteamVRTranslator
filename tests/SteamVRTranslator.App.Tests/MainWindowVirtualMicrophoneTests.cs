using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SteamVRTranslator.App.Output;
using SteamVRTranslator.App.Configuration;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class MainWindowVirtualMicrophoneTests
{
    [Fact]
    public void EnablingAndTestingCuesRequiresUsableInstalledMicrophone()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var enabled = Assert.IsType<CheckBox>(window.FindName("VoiceCuesEnabledCheckBox"));
                var test = Assert.IsType<Button>(window.FindName("TestVoiceCuesButton"));
                var preview = Assert.IsType<Button>(window.FindName("PreviewVoiceCuesButton"));
                var echo = Assert.IsType<CheckBox>(window.FindName("VoiceCueEchoCheckBox"));
                Assert.False(echo.IsChecked);
                echo.IsChecked = true;
                var fileFields = new[] { window.VoiceCueStartFileText, window.VoiceCueOngoingFileText, window.VoiceCueEndFileText };
                foreach (var field in fileFields) Assert.Null(field.Tag);
                fileFields[0].Tag = "sounds/start.wav";
                fileFields[1].Tag = "sounds/loop.wav";
                fileFields[2].Tag = "sounds/end.wav";
                var cueSettings = Assert.IsType<VoiceInputCueConfiguration>(typeof(MainWindow)
                    .GetMethod("ReadVoiceCueControls", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [true, false]));
                Assert.True(cueSettings.EchoEnabled);
                Assert.Equal("sounds/start.wav", cueSettings.StartFilePath);
                Assert.Equal("sounds/loop.wav", cueSettings.OngoingFilePath);
                Assert.Equal("sounds/end.wav", cueSettings.EndFilePath);
                foreach (var stage in new[] { "Start", "Ongoing", "End" })
                {
                    typeof(MainWindow).GetMethod("ResetVoiceCueFileButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, [new Button { Tag = stage }, new RoutedEventArgs()]);
                }
                foreach (var field in fileFields) Assert.Null(field.Tag);
                var install = Assert.IsType<Button>(window.FindName("InstallVirtualMicrophoneButton"));
                var download = Assert.IsType<Button>(window.FindName("DownloadVbCableButton"));
                var uninstall = Assert.IsType<Button>(window.FindName("UninstallVirtualMicrophoneButton"));
                Set(window, "_virtualMicrophonePackageState", VbCablePackageState.Ready);
                foreach (var state in new[]
                         {
                             new VbCableStatus(false, null, null),
                             new VbCableStatus(true, "render", null),
                             new VbCableStatus(true, "render", "capture", "disconnected")
                         })
                {
                    Set(window, "_virtualMicrophoneStatus", state);
                    Update(window);
                    Assert.False(enabled.IsEnabled);
                    Assert.False(enabled.IsChecked);
                    Assert.False(test.IsEnabled);
                    Assert.True(preview.IsEnabled);
                    Assert.Equal(!state.Installed, install.IsEnabled);
                    Assert.Equal(state.Installed, uninstall.IsEnabled);
                    Assert.True(download.IsEnabled);
                }
                Set(window, "_virtualMicrophoneStatus", new VbCableStatus(true, "render", "capture"));
                Update(window);
                Assert.True(enabled.IsEnabled);
                Assert.True(test.IsEnabled);
                Assert.False(install.IsEnabled);
                Set(window, "_startingRuntime", true);
                Update(window);
                Assert.False(enabled.IsEnabled);
                Assert.False(test.IsEnabled);
                Assert.False(preview.IsEnabled);
                Assert.False(window.VoiceCueFilesGrid.IsEnabled);
                Assert.False(install.IsEnabled);
                Set(window, "_startingRuntime", false);
                using var localPreview = new CancellationTokenSource();
                Set(window, "_voiceCuePreviewCancellation", localPreview);
                Set(window, "_voiceCuePreviewIsLocal", true);
                Set(window, "_virtualMicrophoneStatus", new VbCableStatus(false, null, null));
                Update(window);
                Assert.True(preview.IsEnabled);
                Assert.False(test.IsEnabled);
                Assert.False(localPreview.IsCancellationRequested);
                Assert.False(window.VoiceCueFilesGrid.IsEnabled);
                Set(window, "_voiceCuePreviewCancellation", null!);
                Set(window, "_voiceCuePreviewIsLocal", false);
                Set(window, "_virtualMicrophoneStatus", new VbCableStatus(false, null, null));
                Set(window, "_virtualMicrophonePackageState", VbCablePackageState.Invalid);
                Update(window);
                Assert.False(install.IsEnabled);
                Assert.True(download.IsEnabled);
                Set(window, "_virtualMicrophonePackageState", VbCablePackageState.Missing);
                Update(window);
                Assert.True(download.IsEnabled);
                Assert.False(install.IsEnabled);
                using var downloading = new CancellationTokenSource();
                Set(window, "_vbCableDownloadCancellation", downloading);
                Set(window, "_virtualMicrophonePackageState", VbCablePackageState.Ready);
                Update(window);
                Assert.False(download.IsEnabled);
                Assert.False(install.IsEnabled);
                Set(window, "_vbCableDownloadCancellation", null!);
                Set(window, "_virtualMicrophoneBusy", true);
                Update(window);
                Assert.False(download.IsEnabled);
                Assert.False(install.IsEnabled);
                Assert.False(uninstall.IsEnabled);
                Set(window, "_virtualMicrophoneBusy", false);
                Assert.Null(window.FindName("VoiceCueDeviceComboBox"));
            }
            catch (Exception exception) { failure = exception; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        Assert.Null(failure);
    }

    private static void Set(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void Update(MainWindow window) =>
        typeof(MainWindow).GetMethod("UpdateVirtualMicrophoneUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
}
