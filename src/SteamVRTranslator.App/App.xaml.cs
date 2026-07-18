using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App;

public partial class App : Application
{
    private readonly AppLog _fatalLog = new(AppContext.BaseDirectory);

    protected override void OnStartup(StartupEventArgs e)
    {
        _fatalLog.Info("应用进程启动。命令行：" + Environment.CommandLine);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _fatalLog.Error(
                "非界面线程发生未处理异常。",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _fatalLog.Error("后台任务发生未观察异常。", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            _fatalLog.Error("界面线程发生未处理异常。", args.Exception);
            MessageBox.Show(
                args.Exception.Message,
                "SteamVR Translator 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
