using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;

namespace DHelper;

/// <summary>
/// The performance brain. Every second it reads the CPU's power/thermal table, effective clock, load and fans,
/// decides whether the laptop is throttling, and - while a listed game runs - fixes it automatically:
///
///   1. Game starts: Turbo + CPU temperature limit.
///   2. Undervolt (default -40) applied after 3 s of >= 25 % load, or IMMEDIATELY if throttling is detected.
///      It is only removed when the CPU is truly idle (< 12 % for 5 s, e.g. alt-tabbed) because a deep offset
///      that is stable under load can crash at idle. Game exit always restores the safe offset.
///   3. Thermal limit reached: fans go to the aggressive curve at once.
///   4. Still throttling 15 s later with both fixes active: concrete suggestions (with one-click actions).
///
/// Crash guard: a marker file exists while the deep offset is active. If D-Helper starts and finds it,
/// Windows went down with the offset applied, so the game offset is made 5 less aggressive.
/// WHEA hardware-error events seen while the offset is active do the same immediately.
/// </summary>
public sealed class GameController : IDisposable
{
    public enum Phase { Off, Waiting, GameIdle, GameUndervolted, Unavailable }
    public enum Health { Unknown, Idle, Optimal, Watch, Throttling }

    public sealed record Event(DateTime Time, string Kind, string Text);           // Kind: info | action | warn | good
    public sealed record Advice(string Id, string Text, string? ActionLabel);

    public sealed record Snapshot(
        Phase Phase, string StatusText, string? Game, Health Health, string HealthTitle, string HealthDetail,
        int CpuLoad, float? Tctl, float? TctlLimit, float? PowerW, float? PowerLimitW,
        int ClockMhz, int PeakClockMhz, int MaxClockMhz, int CpuFanRpm, int GpuFanRpm,
        bool ThermalLimited, bool PowerLimited, bool ClockDrop,
        bool UndervoltActive, int UndervoltValue, bool CoolingBoost, bool SmuReady, string SmuStatus,
        int? PowerCapW, int LagCount, string LearningStatus,
        double? Fps, double? FpsLow, int TargetFps, string Strategy, int? BudgetW, int? SavingW, string? ModeName, bool GameForeground,
        IReadOnlyList<Event> Events, IReadOnlyList<Advice> Advice, string? CrashGuardMessage);

    /// <summary>Efficient = hold the FPS target with the least power. Performance = the game needs everything.</summary>
    public enum Strategy { None, Efficient, Performance }

    private readonly AsusAcpi _acpi;
    private readonly RyzenSmu _smu;
    private readonly AppConfig _config;
    private readonly System.Threading.Timer _timer;
    private readonly object _tickLock = new();
    private readonly LinkedList<Event> _events = new();
    private List<Advice> _advice = new();

    private Phase _phase = Phase.Off;
    private Health _health = Health.Unknown;
    private string _healthTitle = "Starting…", _healthDetail = "";
    private int _highLoadSeconds, _lowLoadSeconds, _hotSeconds, _coolSeconds, _throttleSeconds;
    private bool _coolingBoost, _clockDrop;
    private int _modeBeforeGame = -1;
    private int _originalTctlLimit = 93;
    private int _appliedTctl = -1;   // limit currently in the SMU during a game
    private int _appliedUv;          // offset currently in the SMU while undervolted
    private DateTime _uvAppliedAt, _lastReassert, _lastReassertEvent;

    // Steady clocks: auto power cap. Original limits are captured when the game starts and restored after.
    private int? _capW;
    private int _origStapmW, _origSlowW, _origFastW;
    private int _wallSeconds, _headroomSeconds;
    private DateTime _lastCapChange, _lastCapReassert;

    // Lag reports (hotkey / button)
    private int _lagCount;
    private bool _lagMark;

    // Learning: best cap per game + continuous speed search
    private readonly CapLearner _learner = new();
    private int? _learnedStartCap;
    private int _loadedSeconds;
    public CapLearner Learner => _learner;

    // FPS + automatic game detection
    private readonly FpsMonitor _fps = new();
    private int _gamePid, _candidatePid, _candidateSeconds;
    private double? _fpsAvg, _fpsLow;
    private bool _gameForeground = true;   // games throttle FPS when tabbed out: those seconds don't count

    // Efficiency controller: one power budget (W) that also picks the performance mode
    private Strategy _strategy = Strategy.None;
    private int _budgetW, _currentMode = -1;
    private int _tightSeconds, _comfySeconds, _noFpsSeconds, _maxedSeconds, _recoverSeconds, _stableBudgetSeconds;
    private DateTime _lastBudgetChange, _lastBudgetEvent;
    private long _prevIdle, _prevTotal;
    private StreamWriter? _csv;

    // Effective clock = base clock x "% Processor Performance"; peak tracked under load.
    private readonly PerformanceCounter? _perfCounter;
    private readonly int _baseMhz;
    private int _clockMhz, _peakClockMhz;
    private const int MaxBoostMhz = 4800; // Ryzen 9 5980HX

    public Phase CurrentPhase => _phase;
    public string StatusText { get; private set; } = "Off";
    public RyzenSmu.Telemetry? LastTelemetry { get; private set; }
    public int LastCpuLoad { get; private set; }
    public string? ActiveGame { get; private set; }
    public string? CrashGuardMessage { get; private set; }
    public event Action? Changed;

    private static string MarkerPath => Path.Combine(AppConfig.Folder, "uv_active.marker");

    // Load gate. Entering is quick; leaving needs real idle, so the undervolt stays on through normal play.
    // Undervolt gate. A deep Curve Optimizer offset is only stable while every core stays busy: on 2026-11-21
    // -40 crashed Windows ~7 s after being applied in WoW at 26-58 % bursty load (cores dropping into low-power
    // states). So it is only applied in max-performance (CPU-bound) games after 10 s at >= 60 % load, and removed
    // the moment load falls under 40 %. Efficient (light) games never get it.
    private const int EnterLoadPct = 60, EnterSeconds = 10, ExitLoadPct = 40, ExitSeconds = 1;
    private const int GamingLoadPct = 25;

    // Aggressive cooling curve (temps °C then duty %), used when the CPU nears the target.
    private static readonly byte[] BoostCurve = { 30, 45, 55, 62, 68, 74, 80, 86, 25, 40, 55, 70, 85, 100, 100, 100 };

    public GameController(AsusAcpi acpi, RyzenSmu smu, AppConfig config)
    {
        _acpi = acpi;
        _smu = smu;
        _config = config;
        _baseMhz = ReadBaseMhz();
        try { _perfCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); _perfCounter.NextValue(); }
        catch { _perfCounter = null; }
        CheckCrashMarker();
        _fps.Start();
        AddEvent("info", "D-Helper started" + (_smu.IsReady ? "" : " (Game mode unavailable: " + _smu.Status + ")")
            + (_fps.IsRunning ? "" : " (FPS monitor " + _fps.Status + ")"));
        _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    // ---------- public API for the UI ----------

    public Snapshot GetSnapshot()
    {
        lock (_events)
        {
            var t = LastTelemetry;
            return new Snapshot(_phase, StatusText, ActiveGame, _health, _healthTitle, _healthDetail,
                LastCpuLoad, t?.TctlValue, t?.TctlLimit, t?.SlowValue, t?.SlowLimit,
                _clockMhz, _peakClockMhz, MaxBoostMhz,
                _acpi.GetFanRpm(AsusAcpi.FanCpu), _acpi.GetFanRpm(AsusAcpi.FanGpu),
                t?.ThermalLimited == true && LastCpuLoad >= GamingLoadPct, t?.PowerLimited == true && LastCpuLoad >= GamingLoadPct, _clockDrop,
                _phase == Phase.GameUndervolted, _phase == Phase.GameUndervolted ? _appliedUv : _config.SafeUndervolt,
                _coolingBoost, _smu.IsReady, _smu.Status,
                _capW, _lagCount, _phase is Phase.GameIdle or Phase.GameUndervolted ? (_strategy == Strategy.Efficient ? EfficientStatus() : _learner.Status) : "",
                _fpsAvg, _fpsLow, _config.TargetFps, _strategy.ToString(),
                _strategy == Strategy.Efficient ? _budgetW : _capW,
                _strategy == Strategy.Efficient && _origSlowW > 0 && t != null ? Math.Max(0, (int)(_origSlowW - t.SlowValue)) : null,
                _currentMode switch { AsusAcpi.ModeSilent => "Silent", AsusAcpi.ModeBalanced => "Balanced", AsusAcpi.ModeTurbo => "Turbo", _ => null },
                _gameForeground,
                _events.ToList(), _advice.ToList(), CrashGuardMessage);
        }
    }

    /// <summary>Runs a suggestion's one-click action.</summary>
    public void RunAdvice(string id)
    {
        lock (_tickLock)
        {
            switch (id)
            {
                case "raise-limit":
                    _config.GameTempTarget = 100;
                    _config.Save();
                    AddEvent("action", "CPU limit raised to 100°C (from suggestion)");
                    break;
                case "enable-game-mode":
                    _config.GameModeEnabled = true;
                    _config.Save();
                    AddEvent("action", "Game mode turned on (from suggestion)");
                    break;
                case "turbo":
                    _acpi.SetPerformanceMode(AsusAcpi.ModeTurbo);
                    _config.PerformanceMode = AsusAcpi.ModeTurbo;
                    _config.Save();
                    AddEvent("action", "Switched to Turbo (from suggestion)");
                    break;
            }
            _advice.RemoveAll(a => a.Id == id);
        }
        Changed?.Invoke();
    }

    public void DismissCrashGuard() => CrashGuardMessage = null;

    // ---------- crash guard ----------

    private void CheckCrashMarker()
    {
        if (!File.Exists(MarkerPath)) return;
        int old = _config.GameUndervolt;
        _config.GameUndervolt = Math.Min(0, old + 5);
        _config.Save();
        File.Delete(MarkerPath);
        CrashGuardMessage = $"Windows went down while the {old} undervolt was active. Game undervolt reduced to {_config.GameUndervolt}.";
        Log.Write("CRASH GUARD: " + CrashGuardMessage);
        AddEvent("warn", CrashGuardMessage);
    }

    private static bool WheaErrorsSince(DateTime since)
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
            SampleClock();
            _fps.EnsureRunning();
            LastTelemetry = _smu.IsReady ? _smu.ReadTelemetry() : null;

            if (!_smu.IsReady)
            {
                SetPhase(Phase.Unavailable, "Game mode needs administrator rights (" + _smu.Status + ")");
            }
            else if (!_config.GameModeEnabled)
            {
                if (_phase is Phase.GameIdle or Phase.GameUndervolted) LeaveGame("Game mode turned off");
                SetPhase(Phase.Off, "Game mode is off");
            }
            else
            {
                string? game = DetectGame();
                if (game == null)
                {
                    if (_phase is Phase.GameIdle or Phase.GameUndervolted) LeaveGame($"{ActiveGame} closed");
                    SetPhase(Phase.Waiting, "Waiting for a game (detected automatically)");
                }
                else
                {
                    if (_phase is not (Phase.GameIdle or Phase.GameUndervolted)) EnterGame(game);
                    SampleFps();
                    ApplyLiveChanges();
                    UpdateUndervoltGate();
                    UpdateCooling();
                    if (_strategy == Strategy.Efficient) UpdateEfficiency();
                    else { UpdatePowerCap(); CheckEfficiencyReturn(); }
                    WriteCsv();
                    string fps = _fpsAvg is double f ? $"{f:F0} fps · " : "";
                    string uv = _phase == Phase.GameUndervolted ? $"undervolt {_appliedUv} on" : "undervolt waiting for load";
                    SetPhase(_phase, $"{ActiveGame} · {fps}{uv}{(_coolingBoost ? " · fan boost" : "")}");
                }
            }

            AssessHealth();
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
        Changed?.Invoke();
    }

    private void EnterGame(string game)
    {
        ActiveGame = game;
        _modeBeforeGame = _config.PerformanceMode;
        if (LastTelemetry != null)
        {
            _originalTctlLimit = (int)Math.Round(LastTelemetry.TctlLimit);
            _origStapmW = (int)Math.Round(LastTelemetry.StapmLimit);
            _origSlowW = (int)Math.Round(LastTelemetry.SlowLimit);
            _origFastW = (int)Math.Round(LastTelemetry.FastLimit);
        }
        _capW = null;
        _wallSeconds = _headroomSeconds = _loadedSeconds = 0;
        if (_origSlowW <= 0) _origStapmW = _origSlowW = _origFastW = 150;
        int? perfStart = _learner.BeginSession(game, _origSlowW - 1, _config.MinPowerCapW); // top probe ≈ uncapped
        _learnedStartCap = _config.SteadyClocks ? perfStart : null;
        _smu.SetTctlLimit(_config.GameTempTarget);
        _appliedTctl = _config.GameTempTarget;
        _highLoadSeconds = _lowLoadSeconds = _hotSeconds = _coolSeconds = _throttleSeconds = 0;
        _tightSeconds = _comfySeconds = _noFpsSeconds = _maxedSeconds = _recoverSeconds = _stableBudgetSeconds = 0;
        _peakClockMhz = 0;
        _coolingBoost = false;
        _currentMode = -1;
        _fpsAvg = _fpsLow = null;
        OpenCsv(game);

        // Start efficient: from the budget learned for this game, or from full power and work down.
        _strategy = Strategy.Efficient;
        int? learnedBudget = _learner.EfficientStart(game);
        _budgetW = Math.Clamp(learnedBudget ?? _origSlowW, _config.MinPowerCapW, _origSlowW);
        ApplyBudget(_budgetW);
        Log.Write($"Game mode: {game} started -> efficient, budget {_budgetW} W, Tctl target {_config.GameTempTarget}°C");
        AddEvent("action", learnedBudget != null
            ? $"{game} detected: starting at the {_budgetW} W it needed last time, aiming for {_config.TargetFps}+ fps"
            : $"{game} detected: aiming for {_config.TargetFps}+ fps with as little power as possible");
        _phase = Phase.GameIdle;
    }

    private void LeaveGame(string reason)
    {
        RemoveUndervolt();
        RemovePowerCap(null);
        if (_strategy == Strategy.Efficient && _stableBudgetSeconds >= 30) _learner.RecordEfficient(_budgetW);
        _learner.EndSession();
        _strategy = Strategy.None;
        _gamePid = 0;
        _fpsAvg = _fpsLow = null;
        _smu.SetTctlLimit(_originalTctlLimit);
        // Re-selecting the mode also restores the firmware's default fan curves.
        _acpi.SetPerformanceMode(_modeBeforeGame >= 0 ? _modeBeforeGame : _config.PerformanceMode);
        _coolingBoost = false;
        _csv?.Dispose();
        _csv = null;
        Log.Write($"Game mode: {reason} -> restored mode {_modeBeforeGame}, Tctl {_originalTctlLimit}°C");
        AddEvent("info", $"{reason}: settings restored, undervolt back to {_config.SafeUndervolt}");
        ActiveGame = null;
        _phase = Phase.Waiting;
    }

    private bool ApplyGameUndervolt(string why)
    {
        if (_config.GameUndervolt >= 0) return false;
        File.WriteAllText(MarkerPath, $"{_config.GameUndervolt} {DateTime.Now:o}");
        if (_smu.SetCurveOptimizerAll(_config.GameUndervolt) != 1)
        {
            File.Delete(MarkerPath);
            return false;
        }
        _uvAppliedAt = DateTime.Now;
        _appliedUv = _config.GameUndervolt;
        _phase = Phase.GameUndervolted;
        AddEvent("good", $"Undervolt {_appliedUv} applied ({why})");
        return true;
    }

    private void UpdateUndervoltGate()
    {
        // Entering needs 10 CONSECUTIVE seconds at >= 60 %; any dip below 60 % restarts the count.
        _highLoadSeconds = LastCpuLoad >= EnterLoadPct ? _highLoadSeconds + 1 : 0;
        _lowLoadSeconds = LastCpuLoad < ExitLoadPct ? _lowLoadSeconds + 1 : 0;

        if (_phase == Phase.GameUndervolted && (_strategy != Strategy.Performance || !_gameForeground))
        {
            // Back to a light game, or tabbed out: the deep offset comes off at once.
            RemoveUndervolt();
            _phase = Phase.GameIdle;
            AddEvent("info", "Undervolt paused: only used while a CPU-bound game keeps every core busy");
            return;
        }
        if (_phase == Phase.GameIdle && _highLoadSeconds >= EnterSeconds && _strategy == Strategy.Performance && _gameForeground)
        {
            ApplyGameUndervolt("CPU under load");
        }
        else if (_phase == Phase.GameUndervolted)
        {
            if (_lowLoadSeconds >= ExitSeconds)
            {
                RemoveUndervolt();
                _phase = Phase.GameIdle;
                AddEvent("info", "CPU idle: undervolt paused to protect against idle crashes");
            }
            else if (DateTime.Now.Second % 15 == 0 && WheaErrorsSince(_uvAppliedAt))
            {
                RemoveUndervolt();
                _config.GameUndervolt = Math.Min(0, _config.GameUndervolt + 5);
                _config.Save();
                CrashGuardMessage = $"CPU hardware errors were logged under the undervolt. Game undervolt reduced to {_config.GameUndervolt}.";
                Log.Write("CRASH GUARD: WHEA errors -> " + CrashGuardMessage);
                AddEvent("warn", CrashGuardMessage);
                _phase = Phase.GameIdle;
            }
        }
    }

    /// <summary>
    /// Pushes values changed in the UI while a game runs. A new undervolt is only sent while the
    /// undervolt is already on, i.e. while the CPU is under load, so a deeper value never
    /// reaches the CPU at idle; setting it to 0 removes it at once.
    /// </summary>
    private void ApplyLiveChanges()
    {
        int target = _config.GameTempTarget;
        if (target != _appliedTctl && _smu.SetTctlLimit(target) == 1)
        {
            _appliedTctl = target;
            AddEvent("action", $"CPU limit now {target}°C");
        }

        // ASUS firmware re-applies its own Turbo limits during play (seen in a BF6 session: 95 -> 93 °C).
        // Compare the CPU's actual limit with ours and put ours back.
        var t = LastTelemetry;
        if (t != null && _appliedTctl > 0 && Math.Abs(t.TctlLimit - _appliedTctl) >= 1 && (DateTime.Now - _lastReassert).TotalSeconds >= 3)
        {
            _lastReassert = DateTime.Now;
            _smu.SetTctlLimit(_appliedTctl);
            if ((DateTime.Now - _lastReassertEvent).TotalSeconds >= 60)
            {
                _lastReassertEvent = DateTime.Now;
                AddEvent("action", $"Firmware reset the CPU limit to {t.TctlLimit:F0}°C: put it back to {_appliedTctl}°C");
            }
        }

        int uv = _config.GameUndervolt;
        if (_phase == Phase.GameUndervolted && uv != _appliedUv)
        {
            if (uv >= 0)
            {
                RemoveUndervolt();
                _phase = Phase.GameIdle;
                AddEvent("action", "Game undervolt set to 0: removed");
            }
            else if (_smu.SetCurveOptimizerAll(uv) == 1)
            {
                File.WriteAllText(MarkerPath, $"{uv} {DateTime.Now:o}");
                _appliedUv = uv;
                AddEvent("action", $"Undervolt now {uv}");
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
        if (t.TctlValue >= _appliedTctl - 3) { _hotSeconds++; _coolSeconds = 0; }
        else if (t.TctlValue < _appliedTctl - 12) { _coolSeconds++; _hotSeconds = 0; }

        if (!_coolingBoost && (_hotSeconds >= 3 || t.ThermalLimited))
        {
            BoostFans($"CPU at {t.TctlValue:F0}°C");
        }
        else if (_coolingBoost && (_coolSeconds >= 60 || !_config.FanBoost))
        {
            _acpi.SetPerformanceMode(_currentMode >= 0 ? _currentMode : AsusAcpi.ModeTurbo); // restores the mode's default curve
            _coolingBoost = false;
            AddEvent("info", _config.FanBoost ? $"Cooled to {t.TctlValue:F0}°C: fans back to the normal curve" : "Fan boost turned off: fans back to the normal curve");
        }
    }

    // ---------- Efficiency: hold the FPS target with the least power ----------

    private int MaxBudgetW => Math.Max(_origSlowW, 60);

    private static int ModeForBudget(int watts) =>
        watts <= 45 ? AsusAcpi.ModeSilent : watts <= 85 ? AsusAcpi.ModeBalanced : AsusAcpi.ModeTurbo;

    /// <summary>One number drives everything: the performance mode (and with it the fan curve) and the CPU power cap.</summary>
    private void ApplyBudget(int watts)
    {
        watts = Math.Clamp(watts, _config.MinPowerCapW, MaxBudgetW);
        int mode = ModeForBudget(watts);
        if (mode != _currentMode)
        {
            _acpi.SetPerformanceMode(mode);   // also restores that mode's quieter/louder default fan curve
            _currentMode = mode;
            _coolingBoost = false;
        }
        if (watts >= MaxBudgetW)
        {
            if (_capW != null) { _smu.SetPowerLimits(_origStapmW, _origSlowW, _origFastW); _capW = null; }
        }
        else
        {
            _smu.SetPowerLimits(watts, watts, (int)(watts * 1.1));
            _capW = watts;
        }
        _budgetW = watts;
        _lastBudgetChange = DateTime.Now;
    }

    private void SampleFps()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
        bool wasForeground = _gameForeground;
        _gameForeground = _gamePid > 0 && fg == (uint)_gamePid;
        if (_gameForeground && !wasForeground && _gamePid > 0) _fps.Reset(_gamePid); // drop background frames
        var s = _gamePid > 0 ? _fps.Get(_gamePid) : null;
        _fpsAvg = s?.Fps;
        _fpsLow = s?.Low;
    }

    private void UpdateEfficiency()
    {
        var t = LastTelemetry;
        int target = _config.TargetFps;
        if (!_gameForeground)
        {
            // Tabbed out: the game's own background frame limiter says nothing about performance. Hold.
            _tightSeconds = _comfySeconds = _maxedSeconds = 0;
            return;
        }
        if (_fpsAvg is not double fps || _fpsLow is not double low)
        {
            // Not presenting right now (menu pause, alt-tab) - hold. If we never see frames, fall back.
            if (++_noFpsSeconds >= 20) SwitchToPerformance(_fps.IsRunning
                ? "its frame rate can't be measured"
                : "its frame rate can't be measured (FPS monitor " + _fps.Status + ")");
            return;
        }
        _noFpsSeconds = 0;

        bool tight = fps < target * 1.10 || low < target * 0.85;
        bool comfy = fps > target * 1.30 && low >= target * 0.95;
        _tightSeconds = tight ? _tightSeconds + 1 : 0;
        _comfySeconds = comfy ? _comfySeconds + 1 : 0;
        double since = (DateTime.Now - _lastBudgetChange).TotalSeconds;
        bool atWall = t != null && t.TctlValue >= _appliedTctl - 1 && LastCpuLoad >= GamingLoadPct;
        _wallSeconds = atWall ? _wallSeconds + 1 : 0;

        // FPS tight while already at full power or at the temperature limit: more power can't help here.
        // Count it towards max performance instead of fighting (trim for heat vs raise for FPS).
        if (tight && (_budgetW >= MaxBudgetW || atWall))
        {
            if (++_maxedSeconds >= 10)
            {
                SwitchToPerformance(atWall
                    ? $"it can't hold {target} fps even at the CPU's {_appliedTctl}°C limit"
                    : $"it needs full power to reach {target} fps");
                return;
            }
        }

        if (atWall && !tight && _wallSeconds >= 3 && since >= 3 && _budgetW > _config.MinPowerCapW)
        {
            // At the temperature limit with frames to spare: a little less power gives steadier frames.
            ApplyBudget((int)(_budgetW * 0.94));
            BudgetEvent("action", $"CPU at its {_appliedTctl}°C limit: power trimmed to {_budgetW} W for steadier frames");
        }
        else if (tight && _tightSeconds >= 2 && since >= 2)
        {
            if (_budgetW >= MaxBudgetW || atWall)
            {
                // handled above
            }
            else
            {
                ApplyBudget(Math.Min(MaxBudgetW, (int)(_budgetW * 1.2) + 3));
                BudgetEvent("action", $"FPS {fps:F0} (1% low {low:F0}) close to {target}: power raised to {_budgetW} W");
            }
        }
        else if (comfy && _comfySeconds >= 5 && since >= 4 && _budgetW > _config.MinPowerCapW)
        {
            ApplyBudget(Math.Max(_config.MinPowerCapW, (int)(_budgetW * 0.92)));
            BudgetEvent("good", $"{fps:F0} fps is plenty: power lowered to {_budgetW} W ({ModeName(_currentMode)})");
        }

        if (!tight) _maxedSeconds = 0;
        if (!tight && !comfy)
        {
            if (++_stableBudgetSeconds % 60 == 0) _learner.RecordEfficient(_budgetW);
        }

        // Firmware can rewrite power limits (e.g. after a mode change): keep our budget in place.
        if (t != null && _capW is int c && Math.Abs(t.SlowLimit - c) > 2 && (DateTime.Now - _lastCapReassert).TotalSeconds >= 3)
        {
            _lastCapReassert = DateTime.Now;
            _smu.SetPowerLimits(c, c, (int)(c * 1.1));
        }
    }

    private void SwitchToPerformance(string why)
    {
        _strategy = Strategy.Performance;
        if (_currentMode != AsusAcpi.ModeTurbo) { _acpi.SetPerformanceMode(AsusAcpi.ModeTurbo); _currentMode = AsusAcpi.ModeTurbo; _coolingBoost = false; }
        if (_capW != null) RemovePowerCap(null);
        _recoverSeconds = 0;
        AddEvent("warn", $"{ActiveGame}: {why}. Max performance: Turbo, undervolt, fans and steady clocks.");
    }

    /// <summary>In max performance, go back to saving power once the game clearly has frames to spare.</summary>
    private void CheckEfficiencyReturn()
    {
        int target = _config.TargetFps;
        bool spare = _gameForeground && _fpsAvg is double f && _fpsLow is double l && f > target * 1.5 && l >= target;
        _recoverSeconds = spare ? _recoverSeconds + 1 : 0;
        if (_recoverSeconds < 20) return;
        _strategy = Strategy.Efficient;
        _tightSeconds = _comfySeconds = _maxedSeconds = 0;
        ApplyBudget(_capW ?? MaxBudgetW);
        AddEvent("good", $"{ActiveGame} has frames to spare ({_fpsAvg:F0} fps): back to saving power");
    }

    private void BudgetEvent(string kind, string text)
    {
        if ((DateTime.Now - _lastBudgetEvent).TotalSeconds < 20) return; // keep the timeline readable
        _lastBudgetEvent = DateTime.Now;
        AddEvent(kind, text);
    }

    private static string ModeName(int mode) => mode switch
    {
        AsusAcpi.ModeSilent => "Silent", AsusAcpi.ModeBalanced => "Balanced", AsusAcpi.ModeTurbo => "Turbo", _ => "-",
    };

    private string EfficientStatus() =>
        $"Efficient · {ModeName(_currentMode)} · {_budgetW} W budget · target {_config.TargetFps}+ fps";

    // ---------- automatic game detection ----------

    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "explorer", "msedge", "msedgewebview2", "chrome", "firefox", "opera", "brave", "vivaldi", "iexplore",
        "Discord", "Teams", "ms-teams", "slack", "Zoom", "Code", "devenv", "rider64", "Claude", "DHelper",
        "ApplicationFrameHost", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "LockApp",
        "obs64", "steamwebhelper", "EpicGamesLauncher", "Battle.net", "EADesktop", "Spotify", "vlc", "mpc-hc64",
        "PotPlayerMini64", "Microsoft.Photos", "Photos", "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "Acrobat", "mstsc",
    };

    /// <summary>
    /// A game is the foreground app that covers its screen and keeps presenting frames (>= 20 fps for 5 s).
    /// Once found it stays the game until it exits, so alt-tabbing doesn't end the session.
    /// Names in the optional GameExecutables setting are always treated as games.
    /// </summary>
    private string? DetectGame()
    {
        if (_gamePid > 0)
        {
            try { using var p = Process.GetProcessById(_gamePid); if (!p.HasExited) return ActiveGame; } catch { }
            _gamePid = 0;
            return null;
        }

        // Games from the optional list, and every game D-Helper has managed before (remembered in learning.json),
        // are recognised as soon as their process runs - no need to be in front first.
        foreach (var name in _config.GameExecutables.Concat(_learner.KnownGames).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (NotGames.Contains(Path.GetFileNameWithoutExtension(name.Trim()))) continue;
            string bare = Path.GetFileNameWithoutExtension(name.Trim());
            if (bare.Length == 0) continue;
            var procs = Process.GetProcessesByName(bare);
            try
            {
                if (procs.Length > 0)
                {
                    _gamePid = procs.OrderByDescending(p => _fps.Get(p.Id)?.Fps ?? 0).First().Id;
                    return bare + ".exe";
                }
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }

        IntPtr hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out uint fgPid);
        int pid = (int)fgPid;
        string? procName = null;
        try { using var p = Process.GetProcessById(pid); procName = p.ProcessName; } catch { }
        bool looksLikeGame = procName != null && pid != Environment.ProcessId && !NotGames.Contains(procName)
            && (_fps.Get(pid)?.Fps ?? 0) >= 20 && CoversScreen(hwnd);

        if (!looksLikeGame) { _candidatePid = 0; _candidateSeconds = 0; return null; }
        if (pid != _candidatePid) { _candidatePid = pid; _candidateSeconds = 0; }
        if (++_candidateSeconds < 5) return null;
        _gamePid = pid;
        return procName + ".exe";
    }

    private static bool CoversScreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT w)) return false;
        IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        long screen = (long)(mi.rcMonitor.Right - mi.rcMonitor.Left) * (mi.rcMonitor.Bottom - mi.rcMonitor.Top);
        long ix = Math.Max(0, Math.Min(w.Right, mi.rcMonitor.Right) - Math.Max(w.Left, mi.rcMonitor.Left));
        long iy = Math.Max(0, Math.Min(w.Bottom, mi.rcMonitor.Bottom) - Math.Max(w.Top, mi.rcMonitor.Top));
        return screen > 0 && ix * iy >= screen * 0.95;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>
    /// Steady clocks. Running flat out into the temperature limit makes the CPU bounce (boost, hit the wall,
    /// hard throttle, recover), which shows up as stutter. Once the fans are already boosted, a lower power cap
    /// gives a slightly lower but steady clock just under the limit. The cap is lowered ~6 % every 3 s while the
    /// CPU sits at the wall, and raised ~5 % after 10 s with 5 °C of headroom, until it is gone again.
    /// </summary>
    private void UpdatePowerCap()
    {
        var t = LastTelemetry;
        if (t == null) return;
        if (!_config.SteadyClocks)
        {
            if (_capW != null) RemovePowerCap("Steady clocks turned off");
            return;
        }
        // Max performance with a measurable frame rate means the game is struggling: every watt is frames.
        // (BF6, CPU-bound: capped at 93 W ran ~40-50 fps vs ~55-61 fps uncapped.) Caps are only a fallback
        // for games whose frame rate can't be measured.
        if (_fpsAvg != null)
        {
            if (_capW != null) RemovePowerCap("frame rate is measurable and every watt counts");
            return;
        }

        bool loaded = LastCpuLoad >= GamingLoadPct;
        bool atWall = t.TctlValue >= _appliedTctl - 1 && loaded;
        bool headroom = t.TctlValue <= _appliedTctl - 5;
        _wallSeconds = atWall ? _wallSeconds + 1 : 0;
        _headroomSeconds = headroom ? _headroomSeconds + 1 : 0;
        _loadedSeconds = loaded ? _loadedSeconds + 1 : 0;
        double sinceChange = (DateTime.Now - _lastCapChange).TotalSeconds;
        bool learning = _learner.Status.Length > 0 && _capW != null;

        // Known game: when the CPU first hits the wall, jump straight to the cap learned in earlier sessions
        // instead of stepping down from full power. Never cap before that: below the limit, power = frames.
        if (_capW == null && _learnedStartCap is int start && atWall && _wallSeconds >= 3)
        {
            _learnedStartCap = null;
            ApplyPowerCap(start, "learned from earlier sessions");
            return;
        }

        // While the learner is searching it owns the cap; the thermal steps below only act as a safety net
        // (8 s stuck at the wall instead of 3 s) and hand the new cap back to the learner.
        if (atWall && _coolingBoost && _wallSeconds >= (learning ? 8 : 3) && sinceChange >= 3)
        {
            int current = _capW ?? (int)Math.Floor(t.SlowValue);
            int next = Math.Max(_config.MinPowerCapW, current - Math.Max(3, (int)(current * 0.06)));
            if (next < current)
            {
                ApplyPowerCap(next, $"CPU at its {_appliedTctl}°C wall");
                if (learning) _learner.Rebase(next);
            }
        }
        else if (_capW is int capNow && _headroomSeconds >= 10 && sinceChange >= 5)
        {
            // Headroom always wins, also over the learner: 5 °C+ under the limit means the cap is costing frames.
            int next = capNow + Math.Max(5, (int)(capNow * 0.10));
            if (next >= _origSlowW || next > t.SlowValue + 25) RemovePowerCap($"headroom at {t.TctlValue:F0}°C");
            else
            {
                ApplyPowerCap(next, $"headroom at {t.TctlValue:F0}°C");
                if (learning) _learner.Rebase(next);
            }
        }
        else if (learning)
        {
            // Score caps by what the player feels: FPS when it can be measured (scaled so 1 fps ≈ 30 "MHz"
            // against the learner's 25-point noise band), otherwise the effective CPU clock.
            int score = _fpsAvg is double f ? (int)(f * 30) : _clockMhz;
            if (!_gameForeground) loaded = false; // background seconds don't count in the comparison
            var step = _learner.Tick(_capW, score, t.TctlValue, loaded, atWall);
            if (_learner.LastDecision is string d) { AddEvent("good", d); _learner.LastDecision = null; }
            if (step is (int capW, string why) && capW != _capW)
                ApplyPowerCap(capW, $"learning: {why}");
            else if (_capW is int c && Math.Abs(t.SlowLimit - c) > 2 && (DateTime.Now - _lastCapReassert).TotalSeconds >= 3)
            {
                _lastCapReassert = DateTime.Now;
                _smu.SetPowerLimits(c, c, (int)(c * 1.1));
            }
        }
        else if (_capW is int c && Math.Abs(t.SlowLimit - c) > 2 && (DateTime.Now - _lastCapReassert).TotalSeconds >= 3)
        {
            _lastCapReassert = DateTime.Now; // firmware reset it: put our cap back silently
            _smu.SetPowerLimits(c, c, (int)(c * 1.1));
        }
    }

    private void ApplyPowerCap(int watts, string why)
    {
        bool raising = _capW is int old && watts > old;
        _smu.SetPowerLimits(watts, watts, (int)(watts * 1.1));
        _capW = watts;
        _lastCapChange = DateTime.Now;
        _wallSeconds = _headroomSeconds = 0;
        AddEvent(raising ? "good" : "action", raising ? $"Power cap raised to {watts} W ({why})" : $"Steady clocks: power capped to {watts} W ({why})");
    }

    private void RemovePowerCap(string? why)
    {
        if (_capW == null) return;
        if (_origSlowW > 0) _smu.SetPowerLimits(_origStapmW, _origSlowW, _origFastW);
        _capW = null;
        _lastCapChange = DateTime.Now;
        if (why != null) AddEvent("good", $"Power cap removed ({why})");
    }

    /// <summary>
    /// "Lag!" from the hotkey or the dashboard. Records what the CPU was doing and, if the CPU was the
    /// limiting factor, reacts more aggressively right away. Returns a one-line verdict.
    /// </summary>
    public string ReportLag()
    {
        string verdict;
        lock (_tickLock)
        {
            _lagCount++;
            _lagMark = true;
            _learner.RecordLag(_capW);
            var t = LastTelemetry;
            bool gaming = _phase is Phase.GameIdle or Phase.GameUndervolted;
            bool atWall = t != null && t.TctlValue >= (_appliedTctl > 0 ? _appliedTctl : t.TctlLimit) - 2;
            bool cpuLimited = atWall || _clockDrop || (t?.PowerLimited ?? false);
            string facts = t == null
                ? $"load {LastCpuLoad}%, {_clockMhz / 1000.0:F2} GHz"
                : $"{t.TctlValue:F0}°C (limit {t.TctlLimit:F0}), {_clockMhz / 1000.0:F2} GHz, load {LastCpuLoad}%" + (_capW is int cap ? $", cap {cap} W" : "");

            if (gaming && _strategy == Strategy.Efficient)
            {
                // Saving power may have been too aggressive for this moment: give it a clear margin now.
                ApplyBudget(Math.Min(MaxBudgetW, (int)(_budgetW * 1.25) + 5));
                _comfySeconds = 0;
                verdict = $"{(_fpsAvg is double f ? $"{f:F0} fps, " : "")}{facts}. Responding: power raised to {_budgetW} W ({ModeName(_currentMode)}).";
            }
            else if (gaming && cpuLimited)
            {
                BoostFans("lag reported");
                if (_config.SteadyClocks && t != null)
                {
                    int current = _capW ?? (int)Math.Floor(t.SlowValue);
                    int next = Math.Max(_config.MinPowerCapW, current - Math.Max(5, (int)(current * 0.12)));
                    if (next < current) ApplyPowerCap(next, "lag reported");
                }
                verdict = $"CPU was at its limit ({facts}). Responding: fans maxed, steadier power cap.";
            }
            else if (gaming)
                verdict = $"CPU wasn't limited ({facts}). The stutter likely came from the GPU, network or the game itself.";
            else
                verdict = $"No game detected ({facts}). D-Helper picks up fullscreen games that are drawing frames after about 5 seconds.";

            AddEvent("warn", "Lag reported: " + verdict);
            try
            {
                string path = Path.Combine(AppConfig.Folder, "lag-reports.csv");
                bool header = !File.Exists(path);
                using var w = new StreamWriter(path, append: true);
                if (header) w.WriteLine("time,game,cpu_load,clock_mhz,tctl,tctl_limit,slow_w,power_cap_w,undervolt_on,cooling_boost,cpu_limited");
                w.WriteLine(string.Join(",", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ActiveGame ?? "", LastCpuLoad, _clockMhz,
                    t?.TctlValue.ToString("F1") ?? "", t?.TctlLimit.ToString("F0") ?? "", t?.SlowValue.ToString("F1") ?? "",
                    _capW?.ToString() ?? "", _phase == Phase.GameUndervolted ? 1 : 0, _coolingBoost ? 1 : 0, cpuLimited ? 1 : 0));
            }
            catch { }
        }
        Log.Write("LAG: " + verdict);
        Changed?.Invoke();
        return verdict;
    }

    private void BoostFans(string why)
    {
        if (_coolingBoost || !_config.FanBoost) return; // opt-in: otherwise fans follow the mode's own curve
        _acpi.SetFanCurves(BoostCurve);
        _coolingBoost = true;
        AddEvent("action", $"Fans boosted ({why})");
    }

    /// <summary>Decides the headline status and, while gaming, fixes throttling automatically.</summary>
    private void AssessHealth()
    {
        var t = LastTelemetry;
        bool loaded = LastCpuLoad >= GamingLoadPct;
        bool thermal = t?.ThermalLimited == true && loaded;
        // Our own steady-clocks cap is deliberate, so neither it nor the lower clock it gives counts as throttling.
        bool power = t?.PowerLimited == true && loaded && _capW == null;
        _clockDrop = loaded && _peakClockMhz > 0 && _clockMhz > 0 && _clockMhz < _peakClockMhz * 0.85 && (_capW == null || thermal);
        bool gaming = _phase is Phase.GameIdle or Phase.GameUndervolted;
        bool throttling = thermal || power || _clockDrop;
        var advice = new List<Advice>();

        _throttleSeconds = throttling ? _throttleSeconds + 1 : 0;

        if (gaming && throttling)
        {
            // Automatic mitigation, in order of effectiveness.
            // (No instant undervolt here any more: that applied -40 at light, bursty load and crashed Windows.)
            if (thermal || _clockDrop) BoostFans("throttling detected");
        }

        if (gaming && !_gameForeground && _fpsAvg != null)
        {
            SetHealth(Health.Idle, "Game in background",
                $"{ActiveGame} isn't the active window, so its frame rate isn't counted. D-Helper keeps the current settings until you're back.");
            lock (_events) _advice = advice;
            return;
        }

        // In a game with a measurable frame rate, FPS is what the player feels, so it decides the headline.
        if (gaming && _fpsAvg is double fps)
        {
            int target = _config.TargetFps;
            double low = _fpsLow ?? fps;
            bool onTarget = fps >= target * 0.97 && low >= target * 0.8;
            string watts = t != null ? $"{t.SlowValue:F0} W" : $"{_budgetW} W";
            if (onTarget && _strategy == Strategy.Efficient)
            {
                string saving = _origSlowW > 0 && t != null && _origSlowW - t.SlowValue >= 5 ? $", {_origSlowW - t.SlowValue:F0} W below full power" : "";
                SetHealth(Health.Optimal, "Efficient",
                    $"{fps:F0} fps (1% low {low:F0}), target {target}+. Using {watts} in {ModeName(_currentMode)}{saving}: no more power or fan than the game needs.");
            }
            else if (onTarget)
            {
                SetHealth(Health.Optimal, _capW != null ? "Steady speed" : "Full speed",
                    $"{fps:F0} fps (1% low {low:F0}). This game needs max performance to stay above {target}.");
            }
            else if (_strategy == Strategy.Efficient)
            {
                SetHealth(Health.Watch, "Adding power", $"{fps:F0} fps (1% low {low:F0}) is under your {target} fps target: raising power now.");
            }
            else
            {
                SetHealth(Health.Throttling, $"Below {target} fps",
                    $"{fps:F0} fps (1% low {low:F0}) at full power. " + (thermal
                        ? $"The CPU is at its {t!.TctlLimit:F0}°C limit."
                        : "The CPU isn't limited, so the GPU or the game itself is the bottleneck."));
                if (_throttleSeconds >= 15 && thermal && _config.GameTempTarget < 100)
                    advice.Add(new Advice("raise-limit", $"Raise the CPU limit from {_config.GameTempTarget}°C to 100°C for a few hundred MHz more.", "Raise to 100°C"));
                if (thermal) advice.Add(new Advice("airflow", "Lift the back of the laptop or use a cooling pad: the fans are already at maximum.", null));
                advice.Add(new Advice("settings", "Lower the game's heaviest graphics settings (shadows, volumetrics, ray tracing) or its resolution scale.", null));
            }
            lock (_events) _advice = advice;
            return;
        }

        if (!loaded && !gaming)
        {
            SetHealth(Health.Idle, "Cool and quiet", $"CPU {LastCpuLoad}% busy. Nothing to fix.");
        }
        else if (!throttling)
        {
            string uv = _phase == Phase.GameUndervolted ? $" Undervolt {_appliedUv} is keeping temperatures down." : "";
            string cap = _capW is int w ? $" Steady clocks: power held at {w} W so the CPU stays just under its limit instead of bouncing off it." : "";
            SetHealth(Health.Optimal, _capW != null ? "Steady speed" : "Full speed", $"CPU at {_clockMhz / 1000.0:F2} GHz, no limits hit.{uv}{cap}");
        }
        else
        {
            string cause = thermal ? $"CPU reached its {t!.TctlLimit:F0}°C limit and is slowing itself down"
                : power ? $"CPU hit its {t!.SlowLimit:F0} W power limit"
                : $"CPU clock fell to {_clockMhz / 1000.0:F2} GHz from {_peakClockMhz / 1000.0:F2} GHz";

            if (gaming && _phase == Phase.GameUndervolted && _coolingBoost)
                SetHealth(_throttleSeconds >= 15 ? Health.Throttling : Health.Watch,
                    _throttleSeconds >= 15 ? "Throttling" : "Fixing it",
                    $"{cause}. Undervolt {_appliedUv} and max fans are already on.");
            else if (gaming)
                SetHealth(Health.Watch, "Fixing it", $"{cause}. Applying undervolt and fan boost now.");
            else
                SetHealth(Health.Throttling, "Throttling", $"{cause}. Game mode isn't managing this app.");

            if (_throttleSeconds >= 15 && thermal)
            {
                if (gaming && _config.GameTempTarget < 100)
                    advice.Add(new Advice("raise-limit", $"Raise the CPU limit from {_config.GameTempTarget}°C to 100°C for a few hundred MHz more (the chip's own maximum is 105°C; the laptop runs hotter).", "Raise to 100°C"));
                advice.Add(new Advice("airflow", "Lift the back of the laptop or use a cooling pad: the fans are already at maximum.", null));
                advice.Add(new Advice("fps-cap", "Cap the game's frame rate a little below your average FPS. Steady frames beat spikes and throttle dips.", null));
            }
            if (_throttleSeconds >= 5 && !gaming)
            {
                if (!_config.GameModeEnabled)
                    advice.Add(new Advice("enable-game-mode", "Turn on Game mode so D-Helper can undervolt and boost the fans automatically.", "Turn on"));
                else
                    advice.Add(new Advice("add-game", "Add this game's .exe name to the Games list so Game mode manages it.", null));
                if (_config.PerformanceMode != AsusAcpi.ModeTurbo)
                    advice.Add(new Advice("turbo", "Switch to Turbo for higher fan speeds and power limits.", "Switch to Turbo"));
            }
            if (_throttleSeconds == 1) AddEvent("warn", cause);
        }

        lock (_events) _advice = advice;
    }

    private void SetHealth(Health health, string title, string detail)
    {
        if (health != _health && health == Health.Optimal && _health is Health.Watch or Health.Throttling)
            AddEvent("good", "Back to full speed");
        _health = health;
        _healthTitle = title;
        _healthDetail = detail;
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
                RemovePowerCap(null);
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
                _capW = null;           // the SMU lost the cap too
                _phase = Phase.Waiting; // next tick re-enters the game cleanly
            }
        }
    }

    // ---------- helpers ----------

    private void AddEvent(string kind, string text)
    {
        lock (_events)
        {
            _events.AddFirst(new Event(DateTime.Now, kind, text));
            while (_events.Count > 40) _events.RemoveLast();
        }
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

    private void SampleClock()
    {
        if (_perfCounter == null || _baseMhz <= 0) return;
        try
        {
            _clockMhz = (int)(_baseMhz * _perfCounter.NextValue() / 100f);
            if (LastCpuLoad >= GamingLoadPct) _peakClockMhz = Math.Max(_peakClockMhz, _clockMhz);
        }
        catch { _clockMhz = 0; }
    }

    private static int ReadBaseMhz()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") is int mhz ? mhz : 3300;
        }
        catch { return 3300; }
    }

    private void SetPhase(Phase phase, string text)
    {
        _phase = phase;
        StatusText = text;
    }

    private void OpenCsv(string game)
    {
        try
        {
            string path = Path.Combine(AppConfig.Folder, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(game)}.csv");
            _csv = new StreamWriter(path) { AutoFlush = true };
            _csv.WriteLine("time,cpu_load,clock_mhz,tctl,tctl_limit,stapm_w,slow_w,fast_w,undervolt_on,cooling_boost,fan_cpu_rpm,fan_gpu_rpm,power_cap_w,lag,fps,fps_low,strategy,mode,budget_w,foreground");
        }
        catch { _csv = null; }
    }

    private void WriteCsv()
    {
        var t = LastTelemetry;
        if (_csv == null || t == null) return;
        _csv.WriteLine(string.Join(",", DateTime.Now.ToString("HH:mm:ss"), LastCpuLoad, _clockMhz, t.TctlValue.ToString("F1"), t.TctlLimit.ToString("F0"),
            t.StapmValue.ToString("F1"), t.SlowValue.ToString("F1"), t.FastValue.ToString("F1"),
            _phase == Phase.GameUndervolted ? 1 : 0, _coolingBoost ? 1 : 0,
            _acpi.GetFanRpm(AsusAcpi.FanCpu), _acpi.GetFanRpm(AsusAcpi.FanGpu),
            _capW?.ToString() ?? "", _lagMark ? 1 : 0,
            _fpsAvg?.ToString("F1") ?? "", _fpsLow?.ToString("F1") ?? "", _strategy, ModeName(_currentMode), _budgetW,
            _gameForeground ? 1 : 0));
        _lagMark = false;
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_tickLock)
        {
            if (_phase is Phase.GameIdle or Phase.GameUndervolted) LeaveGame("D-Helper closing");
            SafeRevert();
            _csv?.Dispose();
            _perfCounter?.Dispose();
            _fps.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
