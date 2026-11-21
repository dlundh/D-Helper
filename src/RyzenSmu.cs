using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DHelper;

/// <summary>
/// Ryzen SMU access through the PawnIO driver, using the signed RyzenSMU PawnIO module that ships inside G-Helper.
///
/// Recovered with REA from G-Helper 0.291 (PawnIO.PawnIOWrapper, PawnIO.RyzenSmuService):
///   * device \\?\GLOBALROOT\Device\PawnIO, IOCTL_LOAD 0xA1B2C084-ish (see constants), IOCTL_EXECUTE
///   * execute input = 32-byte ASCII function name + u64 args; output = u64 values
///   * SMU mailbox: wait rsp != 0, rsp = 0, args[0..5], cmd; wait rsp != 0; 1 = OK
///   * Cezanne (code name 13, family "Renoir" = 3):
///       MP1  cmd 0x3B10528 rsp 0x3B10564 args 0x3B10998
///       PSMU cmd 0x3B10A20 rsp 0x3B10A80 args 0x3B10A88
///       CO all-core: MP1 0x55, arg 0x100000 + offset
///       Tctl limit: MP1 0x19 (°C)
///       STAPM: MP1 0x14 + PSMU 0x31, Fast: MP1 0x15 + PSMU 0x32, Slow: MP1 0x16 + PSMU 0x33 + 0x34 (mW)
/// Requires administrator rights. Settings written to the SMU reset at every reboot.
/// </summary>
public sealed class RyzenSmu : IDisposable
{
    private const uint IoctlLoad = unchecked((uint)-1582161788);
    private const uint IoctlExecute = unchecked((uint)-1582161660);
    private const int CodeNameCezanne = 13;

    private const uint Mp1Cmd = 0x3B10528, Mp1Rsp = 0x3B10564, Mp1Args = 0x3B10998;
    private const uint PsmuCmd = 0x3B10A20, PsmuRsp = 0x3B10A80, PsmuArgs = 0x3B10A88;

    private SafeFileHandle? _handle;
    private readonly object _lock = new();

    public bool IsReady { get; private set; }
    public int CodeName { get; private set; } = -1;
    public uint PmTableVersion { get; private set; }
    public string Status { get; private set; } = "not started";

    /// <summary>Connects to PawnIO and loads the RyzenSMU module. Returns false (with Status) on failure.</summary>
    public bool Initialize()
    {
        try
        {
            string modulePath = Path.Combine(AppContext.BaseDirectory, "RyzenSMU.bin");
            if (!File.Exists(modulePath)) { Status = "RyzenSMU.bin missing next to DHelper.exe"; return false; }

            var raw = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (raw.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                Status = err == 5 ? "access denied (D-Helper must run as administrator)" : $"PawnIO driver not available (error {err})";
                return false;
            }

            byte[] module = File.ReadAllBytes(modulePath);
            if (!DeviceIoControl(raw, IoctlLoad, module, module.Length, null, 0, out _, IntPtr.Zero))
            {
                Status = $"PawnIO refused the SMU module (error {Marshal.GetLastWin32Error()})";
                raw.Dispose();
                return false;
            }
            _handle = raw;

            var code = Execute("ioctl_get_code_name", null, 1);
            CodeName = code == null ? -1 : (int)code[0];
            if (CodeName != CodeNameCezanne)
            {
                Status = $"unsupported CPU code name {CodeName} (D-Helper only knows Cezanne = 13)";
                return false;
            }

            var pm = Execute("ioctl_resolve_pm_table", null, 2);
            PmTableVersion = pm == null ? 0 : (uint)pm[0];
            IsReady = true;
            Status = $"ready (Cezanne, PM table 0x{PmTableVersion:X6})";
            return true;
        }
        catch (Exception ex)
        {
            Status = "error: " + ex.Message;
            return false;
        }
        finally
        {
            Log.Write("SMU: " + Status);
        }
    }

    // ---------- Telemetry ----------

    public sealed record Telemetry(
        float StapmLimit, float StapmValue, float FastLimit, float FastValue,
        float SlowLimit, float SlowValue, float TctlLimit, float TctlValue)
    {
        public bool PowerLimited => SlowValue >= SlowLimit * 0.97f || StapmValue >= StapmLimit * 0.97f || FastValue >= FastLimit * 0.97f;
        public bool ThermalLimited => TctlValue >= TctlLimit - 1.0f;
    }

    /// <summary>Reads the SMU power/thermal table. Indices are the Cezanne layout (PM table 0x40000x).</summary>
    public Telemetry? ReadTelemetry()
    {
        var t = ReadPmTable();
        if (t == null || t.Length < 18 || t[0] == 0) return null;
        return new Telemetry(t[0], t[1], t[2], t[3], t[4], t[5], t[16], t[17]);
    }

    public float[]? ReadPmTable()
    {
        if (!IsReady) return null;
        if (Execute("ioctl_update_pm_table", null, 0) == null) return null;
        var raw = Execute("ioctl_read_pm_table", null, 64);
        if (raw == null) return null;
        var bytes = new byte[raw.Length * 8];
        Buffer.BlockCopy(raw, 0, bytes, 0, bytes.Length);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    // ---------- Writes ----------

    /// <summary>Curve Optimizer all-core offset (negative = undervolt). Returns 1 on success.</summary>
    public uint SetCurveOptimizerAll(int offset)
    {
        offset = Math.Clamp(offset, -50, 0);
        uint r = SendMp1(0x55, (uint)(0x100000 + offset));
        Log.Write($"SMU CO all-core {offset} -> {r}");
        return r;
    }

    public uint SetTctlLimit(int celsius)
    {
        celsius = Math.Clamp(celsius, 60, 100); // Ryzen 9 5980HX maximum is 105 °C
        uint r = SendMp1(0x19, (uint)celsius);
        Log.Write($"SMU Tctl limit {celsius}°C -> {r}");
        return r;
    }

    /// <summary>Sets STAPM, slow and fast power limits in watts.</summary>
    public void SetPowerLimits(int stapmW, int slowW, int fastW)
    {
        uint a = SendMp1(0x14, (uint)(stapmW * 1000)); SendPsmu(0x31, (uint)(stapmW * 1000));
        uint b = SendMp1(0x15, (uint)(fastW * 1000)); SendPsmu(0x32, (uint)(fastW * 1000));
        uint c = SendMp1(0x16, (uint)(slowW * 1000)); SendPsmu(0x33, (uint)(slowW * 1000)); SendPsmu(0x34, (uint)(slowW * 1000));
        Log.Write($"SMU limits STAPM {stapmW}W slow {slowW}W fast {fastW}W -> {a}/{c}/{b}");
    }

    private uint SendMp1(uint cmd, uint arg) => Mailbox(Mp1Cmd, Mp1Rsp, Mp1Args, cmd, arg);
    private uint SendPsmu(uint cmd, uint arg) => Mailbox(PsmuCmd, PsmuRsp, PsmuArgs, cmd, arg);

    private uint Mailbox(uint cmdAddr, uint rspAddr, uint argAddr, uint cmd, uint arg)
    {
        if (!IsReady) return 0xFF;
        lock (_lock)
        {
            if (!WaitForResponse(rspAddr, out _)) return 0xFC;
            if (!WriteReg(rspAddr, 0)) return 0xFF;
            for (uint i = 0; i < 6; i++)
                if (!WriteReg(argAddr + i * 4, i == 0 ? arg : 0)) return 0xFF;
            if (!WriteReg(cmdAddr, cmd)) return 0xFF;
            return WaitForResponse(rspAddr, out uint rsp) ? rsp : 0xFF;
        }
    }

    private bool WaitForResponse(uint rspAddr, out uint value)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        value = 0;
        while (sw.ElapsedMilliseconds < 200)
        {
            if (!ReadReg(rspAddr, out value)) return false;
            if (value != 0) return true;
            Thread.Sleep(1);
        }
        return false;
    }

    private bool ReadReg(uint addr, out uint value)
    {
        value = 0;
        var r = Execute("ioctl_read_smu_register", new ulong[] { addr }, 1);
        if (r == null) return false;
        value = (uint)r[0];
        return true;
    }

    private bool WriteReg(uint addr, uint value) =>
        Execute("ioctl_write_smu_register", new ulong[] { addr, value }, 0) != null;

    /// <summary>Runs a function in the loaded PawnIO module. Returns null on failure.</summary>
    private ulong[]? Execute(string function, ulong[]? input, int outCount)
    {
        if (_handle == null || _handle.IsInvalid) return null;
        int inCount = input?.Length ?? 0;
        var inBuf = new byte[32 + inCount * 8];
        var name = Encoding.ASCII.GetBytes(function);
        Buffer.BlockCopy(name, 0, inBuf, 0, Math.Min(name.Length, 31));
        if (input != null) Buffer.BlockCopy(input, 0, inBuf, 32, inCount * 8);

        var outBuf = outCount > 0 ? new byte[outCount * 8] : null;
        lock (_lock)
        {
            if (!DeviceIoControl(_handle, IoctlExecute, inBuf, inBuf.Length, outBuf, outBuf?.Length ?? 0, out int returned, IntPtr.Zero))
                return null;
        }
        var result = new ulong[outCount];
        if (outBuf != null) Buffer.BlockCopy(outBuf, 0, result, 0, outBuf.Length);
        return result;
    }

    public void Dispose() => _handle?.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? inBuf, int inSize,
        byte[]? outBuf, int outSize, out int returned, IntPtr overlapped);
}
