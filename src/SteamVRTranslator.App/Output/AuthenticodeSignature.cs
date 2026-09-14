using System.Runtime.InteropServices;

namespace SteamVRTranslator.App.Output;

internal static class AuthenticodeSignature
{
    public static bool IsTrusted(string path)
    {
        var file = new TrustFile { Size = Marshal.SizeOf<TrustFile>(), FilePath = path };
        var pointer = Marshal.AllocHGlobal(file.Size);
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            File = pointer, StateAction = 1, ProviderFlags = 0x1000 };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try { return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0; }
        finally
        {
            data.StateAction = 2;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public int Size;
        public IntPtr PolicyCallback;
        public IntPtr SipClient;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr Url;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
