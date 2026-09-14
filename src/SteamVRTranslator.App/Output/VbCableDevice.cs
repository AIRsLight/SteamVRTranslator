using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;

namespace SteamVRTranslator.App.Output;

internal sealed record VbCableStatus(bool Installed, string? PlaybackDeviceId, string? RecordingDeviceId, string? Error = null)
{
    public bool Ready => Installed && PlaybackDeviceId is not null && RecordingDeviceId is not null && Error is null;
    public IReadOnlyCollection<string> DeviceIds { get; init; } = Array.Empty<string>();
    public string DiagnosticDetails { get; init; } = string.Empty;
}

internal sealed record VbCableEndpoint(string Id, string AdapterInstanceId, DataFlow Flow, bool Active, int FormFactor);
internal sealed record VbCableIdentitySource(string Name, Func<string?> Read, bool FollowParents = false, bool IsController = false);

/// <summary>Finds the base VB-CABLE by hardware identity, including renamed endpoints.</summary>
internal static class VbCableDevice
{
    internal const string HardwareId = "VBAudioVACWDM";

    public static VbCableStatus Probe()
    {
        var installed = false;
        var diagnostics = new List<string>();
        try
        {
            var adapters = FindAdapters();
            installed = adapters.Count > 0;
            diagnostics.Add($"Hardware {HardwareId}: {string.Join(", ", adapters.Order(StringComparer.OrdinalIgnoreCase))}");
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = new List<VbCableEndpoint>();
            // Removed historical endpoints can have invalid property stores. Only
            // currently installed endpoints matter, including disabled/unplugged ones.
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All,
                         DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged))
            {
                using (device)
                {
                    try
                    {
                        diagnostics.Add($"Endpoint: {device.FriendlyName}; {device.DataFlow}; {device.State}; {device.ID}");
                        if (!installed || device.State != DeviceState.Active) continue;
                        // Some drivers cannot activate IDeviceTopology, or omit the
                        // controller's InstanceId there. Read endpoint identity first.
                        var instanceId = ResolveAdapter(adapters,
                        [
                            new("ControllerDeviceId", () => ReadString(device, PropertyKeys.PKEY_Device_ControllerDeviceId), IsController: true),
                            new("EndpointInstanceId", () => ReadString(device, PropertyKeys.PKEY_Device_InstanceId), true),
                            new("TopologyInstanceId", () =>
                            {
                                var topology = device.DeviceTopology;
                                if (topology.ConnectorCount == 0) return null;
                                using var adapter = enumerator.GetDevice(topology.GetConnector(0).ConnectedToDeviceId);
                                return ReadString(adapter, PropertyKeys.PKEY_Device_InstanceId);
                            }, true)
                        ], GetParentInstanceId, diagnostics.Add);
                        if (instanceId is null) continue;
                        var formKey = PropertyKeys.PKEY_AudioEndpoint_FormFactor;
                        var form = device.Properties.Contains(formKey) ? Convert.ToInt32(device.Properties[formKey].Value) : -1;
                        diagnostics.Add($"Matched: {instanceId}; formFactor={form}");
                        endpoints.Add(new(device.ID, instanceId, device.DataFlow, device.State == DeviceState.Active, form));
                    }
                    catch (Exception exception) when (IsEndpointReadFailure(exception))
                    {
                        diagnostics.Add($"Endpoint read failed: 0x{exception.HResult:X8} {exception.Message}");
                    }
                }
            }
            var status = SelectEndpoints(adapters, endpoints);
            diagnostics.Add($"Selection: installed={status.Installed}; ready={status.Ready}; playback={status.PlaybackDeviceId ?? "none"}; recording={status.RecordingDeviceId ?? "none"}; matched={endpoints.Count}; active={endpoints.Count(e => e.Active)}");
            return status with { DiagnosticDetails = string.Join(Environment.NewLine, diagnostics) };
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Detection failed: 0x{exception.HResult:X8} {exception}");
            return new(installed, null, null, exception.Message) { DiagnosticDetails = string.Join(Environment.NewLine, diagnostics) };
        }
    }

    private static string? ReadString(MMDevice device, PropertyKey key) =>
        device.Properties.Contains(key) ? device.Properties[key].Value as string : null;

    // NAudio 2.2.1 can wrap an unavailable IDeviceTopology as a null interface,
    // then throw NullReferenceException on ConnectorCount instead of COMException.
    private static bool IsEndpointReadFailure(Exception exception) =>
        exception is COMException or InvalidCastException or ArgumentException or NullReferenceException;

    internal static string? ResolveAdapter(IReadOnlyCollection<string> adapters,
        IEnumerable<VbCableIdentitySource> sources, Func<string, string?> getParent, Action<string>? diagnostic = null)
    {
        foreach (var source in sources)
        {
            try
            {
                var identity = source.Read();
                diagnostic?.Invoke($"{source.Name}: {identity ?? "missing"}");
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var depth = 0; depth < 16 && !string.IsNullOrWhiteSpace(identity) && visited.Add(identity); depth++)
                {
                    // ControllerDeviceId commonly contains "{1}." before the PnP ID.
                    // Compare only complete known identities, never substrings or names.
                    var match = adapters.FirstOrDefault(adapter =>
                        string.Equals(identity, adapter, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(identity, "{1}." + adapter, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) return match;
                    if (source.IsController && identity.StartsWith("{1}.", StringComparison.OrdinalIgnoreCase))
                    {
                        // Its controller is already known and is not a VB-CABLE
                        // adapter. Do not activate another driver's optional topology.
                        diagnostic?.Invoke("Controller belongs to another audio device; skipped.");
                        return null;
                    }
                    if (!source.FollowParents) break;
                    identity = getParent(identity);
                    if (identity is not null) diagnostic?.Invoke($"Parent: {identity}");
                }
            }
            catch (Exception exception) when (IsEndpointReadFailure(exception))
            {
                // Failure of one optional identity source must not hide the endpoint.
                diagnostic?.Invoke($"{source.Name} failed: 0x{exception.HResult:X8} {exception.Message}");
            }
        }
        return null;
    }

    private static string? GetParentInstanceId(string instanceId)
    {
        if (Native.CM_Locate_DevNode(out var node, instanceId, 0) != 0 || Native.CM_Get_Parent(out var parent, node, 0) != 0)
            return null;
        var id = new StringBuilder(1024);
        return Native.CM_Get_Device_ID(parent, id, id.Capacity, 0) == 0 ? id.ToString() : null;
    }

    internal static VbCableStatus SelectEndpoints(IReadOnlyCollection<string> adapters, IEnumerable<VbCableEndpoint> endpoints)
    {
        var candidates = new List<VbCableStatus>();
        foreach (var adapter in adapters)
        {
            var devices = endpoints.Where(e => e.Active && string.Equals(e.AdapterInstanceId, adapter, StringComparison.OrdinalIgnoreCase)).ToArray();
            var render = devices.Where(e => e.Flow == DataFlow.Render).ToArray();
            // Pack45 also exposes "CABLE In 16 Ch". Prefer the normal speaker pin,
            // regardless of the display name assigned by the user.
            var speakers = render.Where(e => e.FormFactor == 1).ToArray(); // EndpointFormFactor.Speakers
            if (speakers.Length == 1) render = speakers;
            var capture = devices.Where(e => e.Flow == DataFlow.Capture).ToArray();
            if (render.Length == 1 && capture.Length == 1)
                candidates.Add(new VbCableStatus(true, render[0].Id, capture[0].Id) { DeviceIds = devices.Select(e => e.Id).ToArray() });
        }
        return candidates.Count == 1 ? candidates[0] : new(adapters.Count > 0, null, null);
    }

    private static HashSet<string> FindAdapters()
    {
        var media = new Guid("4D36E96C-E325-11CE-BFC1-08002BE10318");
        using var devices = Native.SetupDiGetClassDevs(ref media, null, IntPtr.Zero, 0);
        if (devices.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var device = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
            if (!Native.SetupDiEnumDeviceInfo(devices, index, ref device))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) return result;
                throw new Win32Exception(error);
            }
            var ids = new byte[8192];
            if (!Native.SetupDiGetDeviceRegistryProperty(devices, ref device, 1, out var type, ids, ids.Length, out _) || type != 7 ||
                !Encoding.Unicode.GetString(ids).Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(id => string.Equals(id, HardwareId, StringComparison.OrdinalIgnoreCase))) continue;
            var instance = new StringBuilder(1024);
            if (Native.SetupDiGetDeviceInstanceId(devices, ref device, instance, instance.Capacity, out _)) result.Add(instance.ToString());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    private sealed class SafeDeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeDeviceInfoSet() : base(true) { }
        protected override bool ReleaseHandle() => Native.SetupDiDestroyDeviceInfoList(handle);
    }
    private static class Native
    {
        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint CM_Locate_DevNode(out uint node, string instanceId, uint flags);
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        internal static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int size, uint flags);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeDeviceInfoSet SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInfo(SafeDeviceInfoSet set, uint index, ref DeviceInfo device);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceRegistryProperty(SafeDeviceInfoSet set, ref DeviceInfo device, uint property, out uint type, byte[] data, int size, out int required);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstanceId(SafeDeviceInfoSet set, ref DeviceInfo device, StringBuilder id, int size, out int required);
        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
