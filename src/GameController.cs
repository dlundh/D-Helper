using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;

namespace DHelper;

/// <summary>
/// Game mode: detects a listed game, switches to Turbo, holds the CPU just under a temperature target,
/// and applies the game undervolt ONLY while the CPU is under sustained load.
///
/// Why load-gated: a Curve Optimizer offset such as -40 can be stable under load but crash at idle
/// (low-voltage idle states have less margin). So the deep offset is removed as soon as load drops
/// (menus, loading screens, alt-tab) and whenever the game exits.
///
/// Crash guard: a marker file exists while the deep offset is active. If D-Helper starts and finds it,
/// Windows went down with the offset applied, so the game offset is made 5 less aggressive.
/// WHEA hardware-error events seen while the offset is active do the same immediately.
/// </summary>
public sealed class GameController : IDisposable
{
    public enum Phase { Off, Waiting, GameIdle, GameUndervolted, Unavailable }

    private readonly AsusAcpi _acpi;
    private readonly RyzenSmu _smu;
    private readonly AppConfig _config;
    private readonly System.Threading.Timer _timer;
    private readonly object _tickLock = new();

    private Phase _phase = Phase.Off;
    private int _highLoadSeconds, _lowLoadSeconds, _hotSeconds, _coolSeconds;
    private bool _coolingBoost;
    private int _modeBeforeGame = -1;
    private int _originalTctlLimit = 93;
    private int _appliedTctl = -1;   // limit currently in the SMU during a game
    private int _appliedUv;          // offset currently in the SMU while undervolted
    private DateTime _uvAppliedAt;
    private long _prevIdle, _prevTotal;
    private StreamWriter? _csv;

    public Phase CurrentPhase => _phase;
    public string StatusText { get; private set; } = "Off";
    public RyzenSmu.Telemetry? LastTelemetry { get; private set; }
    public int LastCpuLoad { get; private set; }
    public string? ActiveGame { get; private set; }
    public event Action? Changed;

    private static string MarkerPath => Path.Combine(AppConfig.Folder, "uv_active.marker");

    // Load gate: enter the deep offset after 5 s at >= 40 % CPU; leave after 2 s below 25 %.
    private const int EnterLoadPct = 40, EnterSeconds = 5, ExitLoadPct = 25, ExitSeconds = 2;

    // Aggressive cooling curve (temps °C then duty %), used when the CPU nears the target.
    private static readonly byte[] BoostCurve = { 30, 45, 55, 62, 68, 74, 80, 86, 25, 40, 55, 70, 85, 100, 100, 100 };

    public GameController(AsusAcpi acpi, RyzenSmu smu, AppConfig config)
    {
        _acpi = acpi;
        _smu = smu;
        _config = config;
        CheckCrashMarker();
        _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    // ---------- crash guard ----------

    private void CheckCrashMarker()
    {
        if (!File.Exists(MarkerPath)) return;
        int old = _config.GameUndervolt;
        _config.GameUndervolt = Math.Min(0, old + 5);
        _config.Save();
        File.Delete(MarkerPath);
        Log.Write($"CRASH GUARD: previous session ended with undervolt {old} active. Game undervolt is now {_config.GameUndervolt}.");
        CrashGuardMessage = $"Windows went down while the {old} undervolt was active. Game undervolt reduced to {_config.GameUndervolt}.";
    }

    public string? CrashGuardMessage { get; private set; }

    private bool WheaErrorsSince(DateTime since)
    {
        try
        {
            string query = $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));
            return reader.ReadEvent() != null;
        }
        catch { return false; }
    }

    // ---------- main loop (1 s) ----------

    private void Tick()
    {
        if (!Monitor.TryEnter(_tickLock)) return;
        try
        {
            LastCpuLoad = SampleCpuLoad();
            if (!_smu.IsReady)
            {
                SetPhase(Phase.Unavailable, "Game mode needs D-Helper running as administrator (" + _smu.Status + ")");
                return;
            }
            LastTelemetry = _smu.ReadTelemetry();

            if (!_config.GameModeEnabled)
            {
                if (_phase is Phase.GameIdle or Phase.GameUndervolted) LeaveGame("Game mode turned off");
                SetPhase(Phase.Off, "Off");
                return;
            }

            string? game = FindRunningGame();
            if (game == null)
            {
                if (_phase is Phase.GameIdle or Phase.GameUndervolted) LeaveGame($"{ActiveGame} closed");
                SetPhase(Phase.Waiting, "Waiting for a game");
                return;
            }

            if (_phase is not (Phase.GameIdle or Phase.GameUndervolted)) EnterGame(game);

            ApplyLiveChanges();
            UpdateUndervoltGate();
            UpdateCooling();
            WriteCsv();

            string uv = _phase == Phase.GameUndervolted ? $"undervolt {_config.GameUndervolt} ON" : $"undervolt off (load {LastCpuLoad}%)";
            string temp = LastTelemetry != null ? $"{LastTelemetry.TctlValue:F0}/{_config.GameTempTarget}°C" : "";
            SetPhase(_phase, $"{ActiveGame}: {uv}, {temp}{(_coolingBoost ? ", cooling boost" : "")}");
        }
        catch (Exception ex)
        {
            Log.Write("Game controller error: " + ex.Message);
            SafeRevert();
        }
        finally
        {
            Monitor.Exit(_tickLock);
        }
    }

    private void EnterGame(string game)
    {
        ActiveGame = game;
        _modeBeforeGame = _config.PerformanceMode;
        if (LastTelemetry != null) _originalTctlLimit = (int)Math.Round(LastTelemetry.TctlLimit);
        _acpi.SetPerformanceMode(AsusAcpi.ModeTurbo);
        _smu.SetTctlLimit(_config.GameTempTarget);
        _appliedTctl = _config.GameTempTarget;
        _highLoadSeconds = _lowLoadSeconds = _hotSeconds = _coolSeconds = 0;
        _coolingBoost = false;
        OpenCsv(game);
        Log.Write($"Game mode: {game} started -> Turbo, Tctl target {_config.GameTempTarget}°C");
        _phase = Phase.GameIdle;
    }

    private void LeaveGame(string reason)
    {
        RemoveUndervolt();
        _smu.SetTctlLimit(_originalTctlLimit);
        // Re-selecting the mode also restores the firmware's default fan curves.
        _acpi.SetPerformanceMode(_modeBeforeGame >= 0 ? _modeBeforeGame : _config.PerformanceMode);
        _coolingBoost = false;
        _csv?.Dispose();
        _csv = null;
        Log.Write($"Game mode: {reason} -> restored mode {_modeBeforeGame}, Tctl {_originalTctlLimit}°C");
        ActiveGame = null;
        _phase = Phase.Waiting;
    }

    private void UpdateUndervoltGate()
    {
        if (LastCpuLoad >= EnterLoadPct) { _highLoadSeconds++; _lowLoadSeconds = 0; }
        else if (LastCpuLoad < ExitLoadPct) { _lowLoadSeconds++; _highLoadSeconds = 0; }

        if (_phase == Phase.GameIdle && _highLoadSeconds >= EnterSeconds && _config.GameUndervolt < 0)
        {
            File.WriteAllText(MarkerPath, $"{_config.GameUndervolt} {DateTime.Now:o}");
            if (_smu.SetCurveOptimizerAll(_config.GameUndervolt) == 1)
            {
                _uvAppliedAt = DateTime.Now;
                _appliedUv = _config.GameUndervolt;
                _phase = Phase.GameUndervolted;
            }
            else File.Delete(MarkerPath);
        }
        else if (_phase == Phase.GameUndervolted)
        {
            if (_lowLoadSeconds >= ExitSeconds)
            {
                RemoveUndervolt();
                _phase = Phase.GameIdle;
            }
            else if (DateTime.Now.Second % 15 == 0 && WheaErrorsSince(_uvAppliedAt))
            {
                RemoveUndervolt();
                _config.GameUndervolt = Math.Min(0, _config.GameUndervolt + 5);
                _config.Save();
                CrashGuardMessage = $"CPU hardware errors were logged under the undervolt. Game undervolt reduced to {_config.GameUndervolt}.";
                Log.Write("CRASH GUARD: WHEA errors -> " + CrashGuardMessage);
                _phase = Phase.GameIdle;
            }
        }
    }

    /// <summary>
    /// Pushes values changed in the UI while a game runs. A new undervolt is only sent while the
    /// undervolt is already on, i.e. while the CPU is under sustained load, so a deeper value never
    /// reaches the CPU at idle; setting it to 0 removes it at once.
    /// </summary>
    private void ApplyLiveChanges()
    {
        int target = _config.GameTempTarget;
        if (target != _appliedTctl && _smu.SetTctlLimit(target) == 1)
        {
            _appliedTctl = target;
            Log.Write($"Live change: CPU limit now {target}°C");
        }

        int uv = _config.GameUndervolt;
        if (_phase == Phase.GameUndervolted && uv != _appliedUv)
        {
            if (uv >= 0)
            {
                RemoveUndervolt();
                _phase = Phase.GameIdle;
                Log.Write("Live change: game undervolt set to 0, removed");
            }
            else if (_smu.SetCurveOptimizerAll(uv) == 1)
            {
                File.WriteAllText(MarkerPath, $"{uv} {DateTime.Now:o}");
                _appliedUv = uv;
                Log.Write($"Live change: undervolt now {uv}");
            }
        }
    }

    private void RemoveUndervolt()
    {
        if (_phase == Phase.GameUndervolted || File.Exists(MarkerPath))
        {
            _smu.SetCurveOptimizerAll(_config.SafeUndervolt);
            try { File.Delete(MarkerPath); } catch { }
        }
    }

    /// <summary>Fans first: switch to the aggressive curve when the CPU is within 3 °C of the target.</summary>
    private void UpdateCooling()
    {
        var t = LastTelemetry;
        if (t == null) return;
        if (t.TctlValue >= _config.GameTempTarget - 3) { _hotSeconds++; _coolSeconds = 0; }
        else if (t.TctlValue < _config.GameTempTarget - 12) { _coolSeconds++; _hotSeconds = 0; }

        if (!_coolingBoost && _hotSeconds >= 3)
        {
            _acpi.SetFanCurves(BoostCurve);
            _coolingBoost = true;
            Log.Write($"Cooling boost ON at {t.TctlValue:F0}°C");
        }
        else if (_coolingBoost && _coolSeconds >= 60)
        {
            _acpi.SetPerformanceMode(AsusAcpi.ModeTurbo); // restores Turbo's default curve
            _coolingBoost = false;
            Log.Write($"Cooling boost OFF at {t.TctlValue:F0}°C");
        }
    }

    /// <summary>Emergency path: back to the safe offset and the original temperature limit.</summary>
    public void SafeRevert()
    {
        try
        {
            if (_smu.IsReady)
            {
                _smu.SetCurveOptimizerAll(_config.SafeUndervolt);
                if (_phase is Phase.GameIdle or Phase.GameUndervolted) _smu.SetTctlLimit(_originalTctlLimit);
            }
            try { File.Delete(MarkerPath); } catch { }
        }
        catch { }
    }

    /// <summary>Called after waking from sleep: the SMU lost its settings, so start the game state over.</summary>
    public void OnResume()
    {
        lock (_tickLock)
        {
            if (_phase is Phase.GameIdle or Phase.GameUndervolted)
            {
                try { File.Delete(MarkerPath); } catch { }
                _phase = Phase.Waiting; // next tick re-enters the game cleanly
            }
        }
    }

    // ---------- helpers ----------

    private string? FindRunningGame()
    {
        foreach (var name in _config.GameExecutables)
        {
            string bare = Path.GetFileNameWithoutExtension(name.Trim());
            if (bare.Length == 0) continue;
            var procs = Process.GetProcessesByName(bare);
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (running) return bare + ".exe";
        }
        return null;
    }

    private int SampleCpuLoad()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        long total = kernel + user; // kernel time includes idle
        long dIdle = idle - _prevIdle, dTotal = total - _prevTotal;
        _prevIdle = idle;
        _prevTotal = total;
        return dTotal <= 0 ? 0 : (int)Math.Clamp(100 - dIdle * 100 / dTotal, 0, 100);
    }

    private void SetPhase(Phase phase, string text)
    {
        bool changed = phase != _phase || text != StatusText;
        _phase = phase;
        StatusText = text;
        if (changed) Changed?.Invoke();
    }

    private void OpenCsv(string game)
    {
        try
        {
            string path = Path.Combine(AppConfig.Folder, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(game)}.csv");
            _csv = new StreamWriter(path) { AutoFlush = true };
            _csv.WriteLine("time,cpu_load,tctl,tctl_limit,stapm_w,slow_w,fast_w,undervolt_on,cooling_boost,fan_cpu_rpm,fan_gpu_rpm");
        }
        catch { _csv = null; }
    }

    private void WriteCsv()
    {
        var t = LastTelemetry;
        if (_csv == null || t == null) return;
        _csv.WriteLine(string.Join(",", DateTime.Now.ToString("HH:mm:ss"), LastCpuLoad, t.TctlValue.ToString("F1"), t.TctlLimit.ToString("F0"),
            t.StapmValue.ToString("F1"), t.SlowValue.ToString("F1"), t.FastValue.ToString("F1"),
            _phase == Phase.GameUndervolted ? 1 : 0, _coolingBoost ? 1 : 0,
            _acpi.GetFanRpm(AsusAcpi.FanCpu), _acpi.GetFanRpm(AsusAcpi.FanGpu)));
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_tickLock)
        {
            if (_phase is Phase.GameIdle or Phase.GameUndervolted) LeaveGame("D-Helper closing");
            SafeRevert();
            _csv?.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
