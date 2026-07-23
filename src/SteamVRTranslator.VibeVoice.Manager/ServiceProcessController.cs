using System.Diagnostics;
using System.IO;

namespace SteamVRTranslator.VibeVoice.Manager;

public sealed class ServiceProcessController
{
    private readonly ManagerStartupOptions _options;

    public ServiceProcessController(ManagerStartupOptions options)
    {
        _options = options;
    }

    public bool CanStartLocalService =>
        _options.StartLocalService &&
        string.Equals(_options.ServiceUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        (_options.ServiceUri.IsLoopback ||
         string.Equals(_options.ServiceUri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    public Process Start()
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "SteamVRTranslator.VibeVoice.Server.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                ManagerLocalization.Text("Error.ServiceMissing"),
                executable);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--VibeVoiceService:ListenUrl");
        startInfo.ArgumentList.Add(_options.ServiceUri.ToString());
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            startInfo.Environment["VIBEVOICE_API_KEY"] = _options.ApiKey;
        }

        return Process.Start(startInfo) ??
               throw new InvalidOperationException("Unable to start the VibeVoice service.");
    }
}
