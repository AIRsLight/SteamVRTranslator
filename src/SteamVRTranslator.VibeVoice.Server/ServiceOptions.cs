using System.Runtime.InteropServices;

namespace SteamVRTranslator.VibeVoice.Server;

public sealed class ServiceOptions
{
    public const string SectionName = "VibeVoiceService";

    public string ListenUrl { get; set; } = "http://127.0.0.1:5090";

    public string ApiKey { get; set; } = string.Empty;

    public string DataDirectory { get; set; } = string.Empty;

    public string DownloadSource { get; set; } = "official";

    public string Backend { get; set; } = "cpu";

    public int DeviceIndex { get; set; }

    public int ThreadCount { get; set; } = 4;

    public bool AutoStartRuntime { get; set; } = true;

    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataDirectory));
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    public void Normalize()
    {
        ListenUrl = string.IsNullOrWhiteSpace(ListenUrl)
            ? "http://127.0.0.1:5090"
            : ListenUrl.Trim();
        ApiKey = ApiKey?.Trim() ?? string.Empty;
        DownloadSource = string.Equals(DownloadSource, "hf-mirror", StringComparison.OrdinalIgnoreCase)
            ? "hf-mirror"
            : "official";
        Backend = VibeVoiceRuntimeSettings.NormalizeBackend(Backend);
        DeviceIndex = Math.Max(0, DeviceIndex);
        ThreadCount = Math.Clamp(ThreadCount, 1, Math.Max(1, Environment.ProcessorCount));
    }

    public void ValidateRemoteBinding()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return;
        }

        foreach (var value in ListenUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"Invalid listen URL: {value}");
            }

            if (!IsLoopbackHost(uri.Host))
            {
                throw new InvalidOperationException(
                    "An API key is required when VibeVoice Server listens outside localhost.");
            }
        }
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
         string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase));
}

public sealed class VibeVoiceRuntimeSettings
{
    public string Backend { get; set; } = "cpu";

    public int DeviceIndex { get; set; }

    public int ThreadCount { get; set; } = 4;

    public string DownloadSource { get; set; } = "official";

    public static string NormalizeBackend(string? backend) => backend?.Trim().ToLowerInvariant() switch
    {
        "cuda" => "cuda",
        "vulkan" => "vulkan",
        _ => "cpu"
    };

    public void Normalize()
    {
        Backend = NormalizeBackend(Backend);
        DeviceIndex = Math.Max(0, DeviceIndex);
        ThreadCount = Math.Clamp(ThreadCount, 1, Math.Max(1, Environment.ProcessorCount));
        DownloadSource = string.Equals(DownloadSource, "hf-mirror", StringComparison.OrdinalIgnoreCase)
            ? "hf-mirror"
            : "official";
    }
}
