namespace SteamVRTranslator.VibeVoice.Server;

public sealed record InstallRequest(
    string? Backend,
    int? DeviceIndex,
    int? ThreadCount,
    string? DownloadSource,
    string? Target = null);

public sealed record ConfigureRequest(
    string? Backend,
    int? DeviceIndex,
    int? ThreadCount,
    string? DownloadSource);

public sealed record ServiceStatus(
    string Status,
    bool RuntimeInstalled,
    bool ModelInstalled,
    bool RuntimeRunning,
    string Backend,
    int DeviceIndex,
    int ThreadCount,
    string DownloadSource,
    string? CurrentOperation,
    long DownloadedBytes,
    long? TotalBytes,
    string? Error,
    string DataDirectory,
    string ModelPath,
    string? RuntimePath);

public static class InstallationTargets
{
    public const string All = "all";
    public const string Runtime = "runtime";
    public const string Model = "model";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Runtime => Runtime,
        Model => Model,
        _ => All
    };
}
