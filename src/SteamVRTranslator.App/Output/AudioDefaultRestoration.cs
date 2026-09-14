using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SteamVRTranslator.App.Output;

internal sealed record AudioDefaultEntry(DataFlow Flow, Role Role, string DeviceId);
internal sealed record AudioDefaultSnapshot(DateTimeOffset CreatedAt, AudioDefaultEntry[] Entries);

/// <summary>Restores defaults changed to VB-CABLE by installation, including after a restart.</summary>
internal static class AudioDefaultRestoration
{
    private static string SnapshotPath(string directory) => Path.Combine(directory, "audio-defaults-before-install.json");

    public static void Save(string directory)
    {
        using var enumerator = new MMDeviceEnumerator();
        var entries = new List<AudioDefaultEntry>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            try
            {
                using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
                entries.Add(new(flow, role, device.ID));
            }
            catch (COMException) { } // No default is a valid initial state.
        }
        Directory.CreateDirectory(directory);
        File.WriteAllText(SnapshotPath(directory), JsonSerializer.Serialize(new AudioDefaultSnapshot(DateTimeOffset.UtcNow, entries.ToArray())));
    }

    public static void Clear(string directory) => File.Delete(SnapshotPath(directory));

    public static void RestoreIfPending(string directory, IReadOnlyCollection<string> cableDeviceIds)
    {
        var path = SnapshotPath(directory);
        if (!File.Exists(path) || cableDeviceIds.Count == 0) return;
        var snapshot = JsonSerializer.Deserialize<AudioDefaultSnapshot>(File.ReadAllText(path));
        if (snapshot is null || snapshot.Entries is null || DateTimeOffset.UtcNow - snapshot.CreatedAt > TimeSpan.FromDays(1))
        {
            Clear(directory);
            return;
        }
        using var enumerator = new MMDeviceEnumerator();
        var failures = new List<Exception>();
        foreach (var entry in snapshot.Entries)
        {
            string currentId;
            bool originalActive;
            try
            {
                using var current = enumerator.GetDefaultAudioEndpoint(entry.Flow, entry.Role);
                using var original = enumerator.GetDevice(entry.DeviceId);
                currentId = current.ID;
                originalActive = original.State == DeviceState.Active;
            }
            catch (COMException) { continue; } // The old device may have been unplugged.
            if (!ShouldRestore(entry.DeviceId, currentId, originalActive, cableDeviceIds)) continue;
            try { SetDefault(entry.DeviceId, entry.Role); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0) throw new AggregateException("Could not restore previous audio defaults.", failures);
        Clear(directory);
    }

    internal static bool ShouldRestore(string original, string current, bool originalActive, IReadOnlyCollection<string> cableIds) =>
        originalActive && !string.Equals(original, current, StringComparison.OrdinalIgnoreCase) &&
        !cableIds.Contains(original, StringComparer.OrdinalIgnoreCase) && cableIds.Contains(current, StringComparer.OrdinalIgnoreCase);

    private static void SetDefault(string deviceId, Role role)
    {
        // Windows has no public setter. Use the established PolicyConfig interface
        // only to undo an install-time change; callers report compatibility failures.
        var policy = (IAudioPolicyDefaults)new AudioPolicyClient();
        try { Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role)); }
        finally { Marshal.ReleaseComObject(policy); }
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class AudioPolicyClient { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyDefaults
    {
        // Unused vtable slots, in order: formats (4), periods (2), share mode (2), properties (2).
        void Slot0(); void Slot1(); void Slot2(); void Slot3(); void Slot4();
        void Slot5(); void Slot6(); void Slot7(); void Slot8(); void Slot9();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
    }
}
