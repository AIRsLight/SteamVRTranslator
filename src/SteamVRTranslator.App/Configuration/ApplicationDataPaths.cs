namespace SteamVRTranslator.App.Configuration;

internal static class ApplicationDataPaths
{
    public static string RootDirectory { get; } = Path.GetFullPath(AppContext.BaseDirectory);

    public static string LegacyRootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamVRTranslator");

    public static string CaptureDirectory { get; } = Path.Combine(RootDirectory, "captures");

    public static string RuntimeDataDirectory { get; } = Path.Combine(RootDirectory, "runtime-data");

    public static string TemporaryDirectory { get; } = Path.Combine(RuntimeDataDirectory, "temp");

    public static string WebView2Directory { get; } = Path.Combine(RuntimeDataDirectory, "WebView2");

    public static string SteamVrDirectory { get; } = Path.Combine(RuntimeDataDirectory, "SteamVR");

    public static string ResolveCaptureDirectory(string? configuredPath)
    {
        _ = configuredPath;
        return CaptureDirectory;
    }

    public static string CreateTemporaryFilePath(string prefix, string extension)
    {
        Directory.CreateDirectory(TemporaryDirectory);
        return Path.Combine(
            TemporaryDirectory,
            $"{prefix}-{Guid.NewGuid():N}{extension}");
    }

    public static void MigrateLegacyData()
    {
        if (!Directory.Exists(LegacyRootDirectory) ||
            PathsEqual(LegacyRootDirectory, RootDirectory))
        {
            return;
        }

        MigrateFile(
            Path.Combine(LegacyRootDirectory, "appsettings.json"),
            Path.Combine(RootDirectory, "appsettings.json"));
        MigrateDirectory(
            Path.Combine(LegacyRootDirectory, "captures"),
            CaptureDirectory);
        MigrateDirectory(
            Path.Combine(LegacyRootDirectory, "runtimes"),
            Path.Combine(RootDirectory, "runtimes"));
        MigrateDirectory(
            Path.Combine(LegacyRootDirectory, "WebView2"),
            WebView2Directory);
        MigrateDirectory(
            Path.Combine(LegacyRootDirectory, "SteamVR"),
            SteamVrDirectory);
        MigrateDirectory(
            Path.Combine(LegacyRootDirectory, "VibeVoiceService"),
            Path.Combine(RootDirectory, "vibevoice-service", "data"));
        TryDeleteEmptyDirectory(LegacyRootDirectory);
    }

    internal static void MigrateDirectory(string source, string destination)
    {
        if (!Directory.Exists(source) || PathsEqual(source, destination))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            MigrateFile(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            MigrateDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
        }

        TryDeleteEmptyDirectory(source);
    }

    private static void MigrateFile(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);
}
