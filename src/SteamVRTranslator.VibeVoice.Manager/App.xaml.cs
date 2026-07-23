using System.Windows;

namespace SteamVRTranslator.VibeVoice.Manager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = ManagerStartupOptions.Parse(e.Args);
        MainWindow = new MainWindow(options);
        MainWindow.Show();
    }
}
