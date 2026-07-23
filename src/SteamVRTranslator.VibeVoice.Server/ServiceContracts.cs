namespace SteamVRTranslator.VibeVoice.Server;

public sealed record InstallRequest(
    string? Backend,
    int? DeviceIndex,
    int? ThreadCount,
    string? DownloadSource);

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
