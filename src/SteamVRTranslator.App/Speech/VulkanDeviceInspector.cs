using System.Runtime.InteropServices;
using System.Text;

namespace SteamVRTranslator.App.Speech;

internal static class VulkanDeviceInspector
{
    private const int VkSuccess = 0;
    private const int PhysicalDevicePropertiesBufferSize = 4096;
    private const int DeviceNameOffset = 20;
    private const int DeviceNameLength = 256;

    public static IReadOnlyList<VulkanGpuDevice> ListDevices()
    {
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out var library))
        {
            return [];
        }

        IntPtr instance = IntPtr.Zero;
        IntPtr applicationName = IntPtr.Zero;
        IntPtr applicationInfoPointer = IntPtr.Zero;
        IntPtr devicesPointer = IntPtr.Zero;
        try
        {
            var createInstance = GetDelegate<VkCreateInstanceDelegate>(library, "vkCreateInstance");
            var enumeratePhysicalDevices = GetDelegate<VkEnumeratePhysicalDevicesDelegate>(
                library,
                "vkEnumeratePhysicalDevices");
            var getPhysicalDeviceProperties = GetDelegate<VkGetPhysicalDevicePropertiesDelegate>(
                library,
                "vkGetPhysicalDeviceProperties");
            var destroyInstance = GetDelegate<VkDestroyInstanceDelegate>(library, "vkDestroyInstance");

            applicationName = Marshal.StringToCoTaskMemUTF8("SteamVR Translator");
            var applicationInfo = new VkApplicationInfo
            {
                StructureType = 0,
                ApplicationName = applicationName,
                ApplicationVersion = 1,
                ApiVersion = 1u << 22
            };
            applicationInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(applicationInfo, applicationInfoPointer, false);

            var createInfo = new VkInstanceCreateInfo
            {
                StructureType = 1,
                ApplicationInfo = applicationInfoPointer
            };
            if (createInstance(ref createInfo, IntPtr.Zero, out instance) != VkSuccess ||
                instance == IntPtr.Zero)
            {
                return [];
            }

            uint deviceCount = 0;
            if (enumeratePhysicalDevices(instance, ref deviceCount, IntPtr.Zero) != VkSuccess ||
                deviceCount == 0)
            {
                return [];
            }

            devicesPointer = Marshal.AllocHGlobal(checked((int)deviceCount * IntPtr.Size));
            if (enumeratePhysicalDevices(instance, ref deviceCount, devicesPointer) != VkSuccess)
            {
                return [];
            }

            var devices = new List<VulkanGpuDevice>(checked((int)deviceCount));
            for (var index = 0; index < deviceCount; index++)
            {
                var physicalDevice = Marshal.ReadIntPtr(
                    devicesPointer,
                    checked((int)index * IntPtr.Size));
                var propertiesPointer = Marshal.AllocHGlobal(PhysicalDevicePropertiesBufferSize);
                try
                {
                    for (var offset = 0; offset < PhysicalDevicePropertiesBufferSize; offset += sizeof(long))
                    {
                        Marshal.WriteInt64(propertiesPointer, offset, 0);
                    }

                    getPhysicalDeviceProperties(physicalDevice, propertiesPointer);
                    var nameBytes = new byte[DeviceNameLength];
                    Marshal.Copy(propertiesPointer + DeviceNameOffset, nameBytes, 0, nameBytes.Length);
                    var terminator = Array.IndexOf(nameBytes, (byte)0);
                    var name = Encoding.UTF8.GetString(
                        nameBytes,
                        0,
                        terminator >= 0 ? terminator : nameBytes.Length).Trim();
                    devices.Add(new VulkanGpuDevice(
                        checked((int)index),
                        string.IsNullOrWhiteSpace(name) ? $"Vulkan GPU {index}" : name,
                        DeviceTypeName(Marshal.ReadInt32(propertiesPointer, 16))));
                }
                finally
                {
                    Marshal.FreeHGlobal(propertiesPointer);
                }
            }

            return devices;
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or
                MarshalDirectiveException or
                BadImageFormatException)
        {
            return [];
        }
        finally
        {
            if (instance != IntPtr.Zero)
            {
                try
                {
                    GetDelegate<VkDestroyInstanceDelegate>(library, "vkDestroyInstance")(
                        instance,
                        IntPtr.Zero);
                }
                catch (EntryPointNotFoundException)
                {
                }
            }
            if (devicesPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(devicesPointer);
            }
            if (applicationInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(applicationInfoPointer);
            }
            if (applicationName != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(applicationName);
            }
            NativeLibrary.Free(library);
        }
    }

    private static T GetDelegate<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static string DeviceTypeName(int type) => type switch
    {
        1 => "集成 GPU",
        2 => "独立 GPU",
        3 => "虚拟 GPU",
        4 => "CPU Vulkan",
        _ => "GPU"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public int StructureType;
        public IntPtr Next;
        public IntPtr ApplicationName;
        public uint ApplicationVersion;
        public IntPtr EngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public int StructureType;
        public IntPtr Next;
        public uint Flags;
        public IntPtr ApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr EnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkCreateInstanceDelegate(
        ref VkInstanceCreateInfo createInfo,
        IntPtr allocator,
        out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkEnumeratePhysicalDevicesDelegate(
        IntPtr instance,
        ref uint deviceCount,
        IntPtr physicalDevices);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkGetPhysicalDevicePropertiesDelegate(
        IntPtr physicalDevice,
        IntPtr properties);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkDestroyInstanceDelegate(IntPtr instance, IntPtr allocator);
}

internal sealed record VulkanGpuDevice(int Index, string Name, string DeviceType);
