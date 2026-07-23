using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.Speech;

internal static class SenseVoiceInstallation
{
    public static bool IsConfiguredInstallationAvailable(
        SpeechConfiguration configuration,
        string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var runtimePath = string.Equals(
            configuration.SenseVoiceBackend,
            "vulkan",
            StringComparison.OrdinalIgnoreCase)
            ? configuration.SenseVoiceVulkanExecutablePath
            : configuration.SenseVoiceExecutablePath;
        return FileExists(runtimePath, rootDirectory) &&
               FileExists(configuration.SenseVoiceModelPath, rootDirectory) &&
               (string.IsNullOrWhiteSpace(configuration.SenseVoiceVadModelPath) ||
                FileExists(configuration.SenseVoiceVadModelPath, rootDirectory));
    }

    internal static bool FileExists(string? configuredPath, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        try
        {
            var path = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(rootDirectory, configuredPath);
            return File.Exists(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
