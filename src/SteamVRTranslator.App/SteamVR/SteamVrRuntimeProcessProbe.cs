using System.Diagnostics;

namespace SteamVRTranslator.App.SteamVR;

internal readonly record struct SteamVrRuntimeProcessSnapshot(
    bool ServerRunning,
    bool CompositorRunning)
{
    public bool IsReady => ServerRunning && CompositorRunning;

    public bool AnyRunning => ServerRunning || CompositorRunning;
}

internal static class SteamVrRuntimeProcessProbe
{
    private const string ServerProcessName = "vrserver";
    private const string CompositorProcessName = "vrcompositor";

    public static SteamVrRuntimeProcessSnapshot Capture() =>
        new(IsProcessRunning(ServerProcessName), IsProcessRunning(CompositorProcessName));

    internal static SteamVrRuntimeProcessSnapshot FromProcessNames(IEnumerable<string> processNames)
    {
        var normalizedNames = processNames
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new SteamVrRuntimeProcessSnapshot(
            normalizedNames.Contains(ServerProcessName),
            normalizedNames.Contains(CompositorProcessName));
    }

    private static bool IsProcessRunning(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
