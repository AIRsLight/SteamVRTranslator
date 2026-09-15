using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace SteamVRTranslator.App;

internal static class NativeColorPicker
{
    internal static Color? Choose(Window owner, Color initial)
    {
        var customColors = Marshal.AllocHGlobal(16 * sizeof(int));
        try
        {
            Marshal.Copy(new int[16], 0, customColors, 16);
            var options = new ChooseColorOptions
            {
                Size = Marshal.SizeOf<ChooseColorOptions>(),
                Owner = new WindowInteropHelper(owner).Handle,
                Result = initial.R | initial.G << 8 | initial.B << 16,
                CustomColors = customColors,
                Flags = 0x00000001 | 0x00000002 // CC_RGBINIT | CC_FULLOPEN
            };
            return ChooseColorW(ref options)
                ? Color.FromRgb((byte)options.Result, (byte)(options.Result >> 8), (byte)(options.Result >> 16))
                : null;
        }
        finally { Marshal.FreeHGlobal(customColors); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorOptions
    {
        public int Size;
        public nint Owner;
        public nint Instance;
        public int Result;
        public nint CustomColors;
        public int Flags;
        public nint CustomData;
        public nint Hook;
        public nint TemplateName;
    }

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColorW(ref ChooseColorOptions options);
}
