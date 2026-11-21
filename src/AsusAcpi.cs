using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DHelper;

/// <summary>
/// Talks to the ASUS ATKACPI driver (installed with ASUS System Control Interface / Armoury Crate drivers).
///
/// Protocol recovered with REA from G-Helper 0.291 (AsusACPI type, see docs/PROTOCOL.md):
///   * open  \\.\ATKACPI  (GENERIC_READ|GENERIC_WRITE, share R/W, OPEN_EXISTING)
///   * DeviceIoControl code 0x0022240C
///   * input  = [u32 methodId][u32 argLength][args...]   output = 16 bytes
///   * DEVS (set): args = [u32 deviceId][i32 value]       -> out[0..3] == 1 on success
///   * DSTS (get): args = [u32 deviceId][u32 0]           -> out[0..3] - 0x10000 (negative = unsupported)
/// </summary>
public sealed class AsusAcpi : IDisposable
{
    private const string DevicePath = @"\\.\ATKACPI";
    private const uint ControlCode = 0x0022240C;

    private const uint DSTS = 0x53545344; // "DSTS"
    private const uint DEVS = 0x53564544; // "DEVS"

    // Device IDs (constants observed in G-Helper's AsusACPI)
    public const uint PerformanceMode = 0x00120075;
    public const uint GpuEco = 0x00090020;
    public const uint GpuMux = 0x00090016;
    public const uint BatteryLimit = 0x00120057;
    public const uint KeyboardBrightness = 0x00050021;
    public const uint TempCpu = 0x00120094;
    public const uint TempGpu = 0x00120097;
    public const uint FanCpu = 0x00110013;
    public const uint FanGpu = 0x00110014;
    public const uint FanMid = 0x00110031;

    // Performance mode values
    public const int ModeBalanced = 0;
    public const int ModeTurbo = 1;
    public const int ModeSilent = 2;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ_WRITE = 0x3;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private readonly SafeFileHandle _handle;
    private readonly object _lock = new();

    public bool IsConnected => !_handle.IsInvalid && !_handle.IsClosed;

    public AsusAcpi()
    {
        _handle = CreateFile(DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
    }

    private byte[] CallMethod(uint methodId, byte[] args)
    {
        var input = new byte[8 + args.Length];
        BitConverter.GetBytes(methodId).CopyTo(input, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(input, 4);
        Array.Copy(args, 0, input, 8, args.Length);

        var output = new byte[16];
        if (!IsConnected) return output;

        lock (_lock)
        {
            DeviceIoControl(_handle, ControlCode, input, (uint)input.Length,
                output, (uint)output.Length, out _, IntPtr.Zero);
        }
        return output;
    }

    /// <summary>Writes a value. Returns 1 when the firmware accepted it.</summary>
    public int DeviceSet(uint deviceId, int value)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        BitConverter.GetBytes(value).CopyTo(args, 4);
        var result = BitConverter.ToInt32(CallMethod(DEVS, args), 0);
        Log.Write($"SET 0x{deviceId:X8} = {value} -> {(result == 1 ? "OK" : result.ToString())}");
        return result;
    }

    /// <summary>Writes a byte buffer (e.g. a 16-byte fan curve). Returns 1 when accepted.</summary>
    public int DeviceSet(uint deviceId, byte[] data)
    {
        var args = new byte[4 + data.Length];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        data.CopyTo(args, 4);
        var result = BitConverter.ToInt32(CallMethod(DEVS, args), 0);
        Log.Write($"SET 0x{deviceId:X8} = {BitConverter.ToString(data)} -> {(result == 1 ? "OK" : result.ToString())}");
        return result;
    }

    public const uint CpuFanCurve = 0x00110024;
    public const uint GpuFanCurve = 0x00110025;

    /// <summary>
    /// Fan curve: 8 temperatures (°C, ascending) then 8 duty values (%, ascending), as in G-Helper's fan_profile_* settings.
    /// Re-selecting a performance mode restores the firmware's default curve.
    /// </summary>
    public int SetFanCurves(byte[] curve) =>
        Math.Min(DeviceSet(CpuFanCurve, curve), DeviceSet(GpuFanCurve, curve));

    /// <summary>Reads a value. Negative means the device ID is not present on this model.</summary>
    public int DeviceGet(uint deviceId)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        return BitConverter.ToInt32(CallMethod(DSTS, args), 0) - 0x10000;
    }

    public bool IsSupported(uint deviceId) => DeviceGet(deviceId) >= 0;

    /// <summary>Fan speed in RPM, or -1 when unavailable. Firmware reports hundreds of RPM.</summary>
    public int GetFanRpm(uint fanId)
    {
        int raw = DeviceGet(fanId);
        int value = raw & 0xFFFF;
        if (value > 120 || (value == 0 && raw < 0)) return -1;
        return value * 100;
    }

    /// <summary>Temperature in °C, or -1 when unavailable.</summary>
    public int GetTemp(uint sensorId)
    {
        int value = DeviceGet(sensorId);
        return value is > 0 and < 150 ? value : -1;
    }

    public int SetPerformanceMode(int mode) => DeviceSet(PerformanceMode, mode);

    /// <summary>Mirrors G-Helper: only writes when the requested state differs from the current one.</summary>
    public int SetGpuEco(bool eco)
    {
        int current = DeviceGet(GpuEco);
        if (current < 0) return -1;
        if (current == 1 && !eco) return DeviceSet(GpuEco, 0);
        if (current == 0 && eco) return DeviceSet(GpuEco, 1);
        return 1; // already in the requested state
    }

    /// <summary>1 = Eco (dGPU off), 0 = Standard, negative = unsupported.</summary>
    public int GetGpuEco() => DeviceGet(GpuEco);

    /// <summary>0 = Ultimate (MUX to dGPU, Eco impossible), 1 = Optimus/Standard, negative = no MUX.</summary>
    public int GetGpuMux() => DeviceGet(GpuMux);

    public int SetBatteryLimit(int percent) => DeviceSet(BatteryLimit, Math.Clamp(percent, 40, 100));

    /// <summary>Level 0..3, encoded as 0x80 | level (as G-Helper does).</summary>
    public int SetKeyboardBrightness(int level) => DeviceSet(KeyboardBrightness, 0x80 | (Math.Clamp(level, 0, 3) & 0x7F));

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
