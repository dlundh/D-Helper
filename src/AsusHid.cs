using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DHelper;

/// <summary>
/// Keyboard backlight over the ROG laptop's USB HID "Aura" interface.
///
/// Recovered with REA from G-Helper 0.291 (GHelper.USB.AsusHid / GHelper.USB.Aura):
///   * vendor 0x0B05, product IDs from AsusHid.MAIN_AURA_PIDS (static array blob)
///   * Aura.DirectBrightness -> AsusHid.WriteInput: feature report 0x5A = [5A BA C5 C4 level]
///     padded to the collection's feature report length, sent with SetFeature.
/// Uses the Windows HID API directly, so no third-party library is needed.
/// </summary>
public static class AsusHid
{
    private const ushort AsusVendor = 0x0B05;
    private const byte InputReportId = 0x5A;

    // AsusHid.MAIN_AURA_PIDS (25 entries) + REAR_LIGHT_PIDS (0x18C6), read from GHelper.dll metadata.
    private static readonly HashSet<ushort> AuraPids = new()
    {
        0x1A30, 0x1854, 0x1869, 0x1866, 0x19B6, 0x1822, 0x1837, 0x184A, 0x183D, 0x8502,
        0x1807, 0x17E0, 0x1ABE, 0x1B4C, 0x1B6E, 0x1B2C, 0x8854, 0x1CE7, 0x1CE6, 0x1BF2,
        0x1BF3, 0x1CD7, 0x1CD8, 0x4B42, 0x18C6,
    };

    /// <summary>Sets backlight level 0 (off) .. 3 (high). Returns the number of HID collections that accepted it.</summary>
    public static int SetKeyboardBrightness(int level)
    {
        byte[] packet = { InputReportId, 0xBA, 0xC5, 0xC4, (byte)Math.Clamp(level, 0, 3) };
        int accepted = 0;

        foreach (var path in EnumerateAsusHidPaths())
        {
            using var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;

            int featureLength = GetFeatureReportLength(handle);
            if (featureLength < packet.Length) continue;

            var buffer = new byte[featureLength];
            Array.Copy(packet, buffer, packet.Length);
            if (HidD_SetFeature(handle, buffer, buffer.Length)) accepted++;
        }

        Log.Write($"HID keyboard brightness {level} -> accepted by {accepted} collection(s)");
        return accepted;
    }

    public static bool IsPresent() => EnumerateAsusHidPaths().Any();

    // ---------- Aura lighting (keyboard + light bar effect/colour, light bar power) ----------
    // Recovered from Aura.AuraMessage / ApplyAura / AuraPowerMessage / ApplyPower and AsusHid.Write:
    // messages go out as HID *output* reports with report ID 0x5D, sent in order: effect, SET, APPLY.

    public enum AuraMode : byte { Static = 0, Breathe = 1, ColorCycle = 2, Rainbow = 3, Strobe = 10 }
    public enum AuraSpeed { Slow = 0, Normal = 1, Fast = 2 }

    private const byte AuraReportId = 0x5D;
    private static readonly byte[] MessageSet = { 0x5D, 0xB5, 0x00, 0x00, 0x00 };
    private static readonly byte[] MessageApply = { 0x5D, 0xB4 };

    /// <summary>Sets the lighting effect. On single-zone Strix models this drives the keyboard and the light bar together.</summary>
    public static int SetAura(AuraMode mode, Color color, AuraSpeed speed)
    {
        byte speedByte = speed switch { AuraSpeed.Slow => 0xE1, AuraSpeed.Fast => 0xF5, _ => 0xEB };
        bool black = color.R == 0 && color.G == 0 && color.B == 0;
        var effect = new byte[17];
        effect[0] = AuraReportId;
        effect[1] = 0xB3;
        effect[2] = 0x00;                 // zone
        effect[3] = (byte)mode;
        effect[4] = color.R;
        effect[5] = color.G;
        effect[6] = color.B;
        effect[7] = speedByte;
        effect[8] = 0x00;                 // direction
        effect[9] = black ? (byte)0xFF : (byte)(mode == AuraMode.Breathe ? 1 : 0);
        // bytes 10..12: second colour (breathe), left black

        int n = WriteOutput(new[] { effect, MessageSet, MessageApply });
        Log.Write($"Aura mode={mode} color=#{color.R:X2}{color.G:X2}{color.B:X2} speed={speed} -> {n} collection(s)");
        return n;
    }

    /// <summary>
    /// Which zones are lit in which power state (AuraPowerMessage: 5D BD 01 kb bar lid rear FF ...).
    /// Defaults match G-Helper's: everything on, except the light bar at boot/sleep/shutdown.
    /// </summary>
    public sealed record AuraPower(
        bool AwakeKeyb = true, bool BootKeyb = true, bool SleepKeyb = true, bool ShutdownKeyb = true,
        bool AwakeLogo = true, bool BootLogo = true, bool SleepLogo = true, bool ShutdownLogo = true,
        bool AwakeBar = true, bool BootBar = false, bool SleepBar = false, bool ShutdownBar = false,
        bool AwakeLid = true, bool BootLid = true, bool SleepLid = true, bool ShutdownLid = true);

    public static int SetPower(AuraPower p)
    {
        byte kb = 0, bar = 0, lid = 0, rear = 0;
        if (p.BootLogo) kb |= 0x01; if (p.BootKeyb) kb |= 0x02; if (p.AwakeLogo) kb |= 0x04; if (p.AwakeKeyb) kb |= 0x08;
        if (p.SleepLogo) kb |= 0x10; if (p.SleepKeyb) kb |= 0x20; if (p.ShutdownLogo) kb |= 0x40; if (p.ShutdownKeyb) kb |= 0x80;

        if (p.AwakeBar) bar |= 0x01 | 0x04; if (p.BootBar) bar |= 0x02; if (p.SleepBar) bar |= 0x08; if (p.ShutdownBar) bar |= 0x10;

        // G-Helper drives the lid and rear zones from the same "lid" settings.
        if (p.BootLid) { lid |= 0x01 | 0x10; rear |= 0x01 | 0x10; }
        if (p.AwakeLid) { lid |= 0x02 | 0x20; rear |= 0x02 | 0x20; }
        if (p.SleepLid) { lid |= 0x04 | 0x40; rear |= 0x04 | 0x40; }
        if (p.ShutdownLid) { lid |= 0x08 | 0x80; rear |= 0x08 | 0x80; }

        byte[] message = { AuraReportId, 0xBD, 0x01, kb, bar, lid, rear, 0xFF };
        int n = WriteOutput(new[] { message });
        Log.Write($"Aura power kb=0x{kb:X2} bar=0x{bar:X2} lid=0x{lid:X2} rear=0x{rear:X2} -> {n} collection(s)");
        return n;
    }

    /// <summary>Writes each message as an output report (padded to the collection's output report length).</summary>
    private static int WriteOutput(IEnumerable<byte[]> messages)
    {
        var list = messages.ToList();
        int accepted = 0;
        foreach (var path in EnumerateAsusHidPaths())
        {
            using var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;

            int outLength = GetCaps(handle).OutputReportByteLength;
            if (outLength < 17) continue;

            bool ok = true;
            foreach (var msg in list)
            {
                var buffer = new byte[outLength];
                Array.Copy(msg, buffer, Math.Min(msg.Length, outLength));
                if (!WriteFile(handle, buffer, buffer.Length, out _, IntPtr.Zero)) { ok = false; break; }
            }
            if (ok) accepted++;
        }
        return accepted;
    }

    private static IEnumerable<string> EnumerateAsusHidPaths()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) yield break;

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
            {
                string? path = GetInterfacePath(set, ref iface);
                if (path == null || !TryParseVidPid(path, out ushort vid, out ushort pid)) continue;
                if (vid == AsusVendor && AuraPids.Contains(pid)) yield return path;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? GetInterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out int required, IntPtr.Zero);
        if (required <= 0) return null;

        IntPtr detail = Marshal.AllocHGlobal(required);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8 on x64 (DWORD + WCHAR[1] + padding)
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static bool TryParseVidPid(string path, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        string p = path.ToLowerInvariant();
        int v = p.IndexOf("vid_", StringComparison.Ordinal);
        int d = p.IndexOf("pid_", StringComparison.Ordinal);
        if (v < 0 || d < 0 || v + 8 > p.Length || d + 8 > p.Length) return false;
        return ushort.TryParse(p.AsSpan(v + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid)
            && ushort.TryParse(p.AsSpan(d + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    private static int GetFeatureReportLength(SafeFileHandle handle) => GetCaps(handle).FeatureReportByteLength;

    private static HIDP_CAPS GetCaps(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) return default;
        try
        {
            return HidP_GetCaps(preparsed, out HIDP_CAPS caps) == HIDP_STATUS_SUCCESS ? caps : default;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);

    // ---------- Win32 ----------

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const int DIGCF_PRESENT = 0x2;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
        IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
