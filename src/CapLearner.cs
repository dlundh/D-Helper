using System.Text.Json;

namespace DHelper;

/// <summary>
/// Learns the power cap that gives the fastest STEADY CPU clock for each game, and keeps tracking it as
/// conditions drift (room temperature, map, laptop position).
///
///   * Memory: the best cap per game is saved in %APPDATA%\D-Helper\learning.json and used as the starting
///     point next session, so there is no warm-up search.
///   * Speed search (hill climbing): measure the current cap for 30 s, then the cap 5 W higher, then 5 W lower,
///     and keep whichever gave the highest average clock under load. Rest 90 s, repeat.
///   * Lag reports count against the cap they happened at, and long stretches at the thermal wall are penalised,
///     so the search prefers smooth over peaky.
///   * Ties (within 25 MHz) go to the lower cap: same speed, less heat and noise.
///
/// Only power is learned. The undervolt is never tuned automatically, because an unstable value crashes.
/// </summary>
public sealed class CapLearner
{
    private const int StepW = 5, WindowSeconds = 30, MaxWindowSeconds = 60, MinLoadedSamples = 20;
    private const int SettleSeconds = 20, RestSeconds = 90, NoiseMhz = 25, LagPenaltyMhz = 120, WallPenaltyMhz = 60;

    // ---------- persistence ----------

    public sealed class CapStat
    {
        public int Seconds { get; set; }
        public double ClockSum { get; set; }
        public double TempSum { get; set; }
        public int Lags { get; set; }
    }

    public sealed class GameProfile
    {
        public int? BestCapW { get; set; }
        public int? EfficientBudgetW { get; set; }   // least power that held the FPS target last time
        public int Sessions { get; set; }
        public DateTime Updated { get; set; }
        public Dictionary<int, CapStat> Stats { get; set; } = new();
    }

    private sealed class Store
    {
        public Dictionary<string, GameProfile> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string StorePath => Path.Combine(AppConfig.Folder, "learning.json");
    private Store _store = LoadStore();

    private static Store LoadStore()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var s = JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath));
                if (s != null) { s.Games = new(s.Games, StringComparer.OrdinalIgnoreCase); return s; }
            }
        }
        catch (Exception ex) { Log.Write("Learning store unreadable, starting fresh: " + ex.Message); }
        return new Store();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Folder);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Learning store save failed: " + ex.Message); }
    }

    public void Reset(string? game = null)
    {
        if (game == null) _store = new Store();
        else _store.Games.Remove(game);
        Save();
        _center = null;
        _stage = Stage.Settling;
        _stageSeconds = 0;
    }

    // ---------- session state ----------

    private enum Stage { Off, Settling, MeasureCenter, ProbeUp, ProbeDown, Resting }

    private sealed class Window
    {
        public int CapW;
        public int Seconds, Loaded, Wall, Lags;
        public double ClockSum;
        public double AvgClock => Loaded == 0 ? 0 : ClockSum / Loaded;
        public double Score => AvgClock - Lags * LagPenaltyMhz - (Loaded > 0 && Wall > Loaded * 0.4 ? WallPenaltyMhz : 0);
    }

    private string? _game;
    private GameProfile? _profile;
    private int _maxCapW, _minCapW;
    private int? _center;
    private Stage _stage = Stage.Off;
    private int _stageSeconds;
    private Window? _current;
    private readonly Dictionary<Stage, Window> _results = new();

    public string Status { get; private set; } = "";

    /// <summary>Starts learning for a game. Returns the learned starting cap, if any.</summary>
    public int? BeginSession(string game, int maxCapW, int minCapW)
    {
        _game = game;
        _maxCapW = maxCapW;
        _minCapW = minCapW;
        if (!_store.Games.TryGetValue(game, out _profile))
        {
            _profile = new GameProfile();
            _store.Games[game] = _profile;
        }
        _profile.Sessions++;
        _center = _profile.BestCapW is int best ? Math.Clamp(best, minCapW, maxCapW) : null;
        _stage = Stage.Settling;
        _stageSeconds = 0;
        _results.Clear();
        Status = _center is int c ? $"Starting from learned {c} W" : "Learning this game: finding a first cap";
        return _center;
    }

    public void EndSession()
    {
        if (_profile != null && _center is int c)
        {
            _profile.BestCapW = c;
            _profile.Updated = DateTime.Now;
        }
        Save();
        _game = null;
        _profile = null;
        _stage = Stage.Off;
        Status = "";
    }

    /// <summary>The safety controller moved the cap (CPU stuck at the wall): start measuring around it.</summary>
    public void Rebase(int capW)
    {
        if (_game == null) return;
        _center = capW;
        _stage = Stage.Settling;
        _stageSeconds = 0;
        _current = null;
    }

    /// <summary>The budget this game settled at in earlier sessions (efficient mode), if known.</summary>
    public int? EfficientStart(string game) =>
        _store.Games.TryGetValue(game, out var p) ? p.EfficientBudgetW : null;

    /// <summary>Remember the budget that holds the FPS target, smoothed so one odd scene doesn't dominate.</summary>
    public void RecordEfficient(int budgetW)
    {
        if (_profile == null) return;
        _profile.EfficientBudgetW = _profile.EfficientBudgetW is int old ? (int)Math.Round(old * 0.7 + budgetW * 0.3) : budgetW;
        _profile.Updated = DateTime.Now;
        Save();
    }

    public void RecordLag(int? capW)
    {
        if (_current != null) _current.Lags++;
        if (_profile != null && capW is int c) Stat(c).Lags++;
    }

    private CapStat Stat(int capW)
    {
        if (!_profile!.Stats.TryGetValue(capW, out var s)) _profile.Stats[capW] = s = new CapStat();
        return s;
    }

    /// <summary>
    /// Called every second while the game runs. Returns a cap to apply now (with the reason), or null to keep the current one.
    /// </summary>
    public (int capW, string reason)? Tick(int? currentCapW, int clockMhz, float tctl, bool loaded, bool atWall)
    {
        if (_game == null || _profile == null) return null;

        // Long-term memory: what each cap delivered under load.
        if (loaded && currentCapW is int cur && clockMhz > 0)
        {
            var s = Stat(cur);
            s.Seconds++;
            s.ClockSum += clockMhz;
            s.TempSum += tctl;
        }

        // No center yet: the thermal controller is still finding a first cap.
        if (_center == null)
        {
            if (currentCapW is int found) { _center = found; _stage = Stage.Settling; _stageSeconds = 0; }
            else { Status = "Learning this game: waiting for the first cap"; return null; }
        }
        int center = _center.Value;

        _stageSeconds++;
        switch (_stage)
        {
            case Stage.Settling:
                Status = $"Settling at {center} W";
                if (_stageSeconds >= SettleSeconds) return StartWindow(Stage.MeasureCenter, center, "measuring");
                if (currentCapW != center) return (center, "learned best");
                return null;

            case Stage.Resting:
                Status = $"Best so far {center} W · next test in {RestSeconds - _stageSeconds} s";
                if (_stageSeconds >= RestSeconds) return StartWindow(Stage.MeasureCenter, center, "measuring");
                return null;
        }

        // Measuring one of the three windows.
        var w = _current!;
        w.Seconds++;
        if (loaded) { w.Loaded++; w.ClockSum += clockMhz; if (atWall) w.Wall++; }
        Status = $"{StageLabel(_stage)} {w.CapW} W · {w.Seconds}/{WindowSeconds} s";

        bool enough = w.Seconds >= WindowSeconds && w.Loaded >= MinLoadedSamples;
        if (!enough && w.Seconds < MaxWindowSeconds) return null;

        if (w.Loaded < MinLoadedSamples)
        {
            // Mostly menus/idle: not a fair comparison. Go back to the center and try again later.
            _results.Clear();
            return StartRest(center, "not enough gameplay to compare");
        }

        _results[_stage] = w;
        switch (_stage)
        {
            case Stage.MeasureCenter:
                if (center + StepW <= _maxCapW) return StartWindow(Stage.ProbeUp, center + StepW, "testing more power");
                if (center - StepW >= _minCapW) return StartWindow(Stage.ProbeDown, center - StepW, "testing less power");
                return StartRest(center, "at the edge of the range");
            case Stage.ProbeUp:
                if (center - StepW >= _minCapW) return StartWindow(Stage.ProbeDown, center - StepW, "testing less power");
                return Decide();
            default:
                return Decide();
        }
    }

    private (int, string)? Decide()
    {
        int center = _center!.Value;
        var c = _results[Stage.MeasureCenter];
        Window best = c;
        foreach (var w in _results.Values)
        {
            bool clearlyBetter = w.Score > best.Score + NoiseMhz;
            bool sameButCooler = Math.Abs(w.Score - best.Score) <= NoiseMhz && w.CapW < best.CapW && w.Score >= c.Score - NoiseMhz;
            if (clearlyBetter || sameButCooler) best = w;
        }
        string summary = string.Join(", ", _results.Values.OrderBy(w => w.CapW).Select(w => $"{w.CapW} W {w.AvgClock / 1000:F2} GHz" + (w.Lags > 0 ? $" ({w.Lags} lag)" : "")));
        _results.Clear();

        if (best.CapW != center)
        {
            _center = best.CapW;
            _profile!.BestCapW = best.CapW;
            _profile.Updated = DateTime.Now;
            Save();
            LastDecision = $"Learned: {best.CapW} W is faster ({summary})";
            return StartRest(best.CapW, "faster steady clock");
        }
        _profile!.BestCapW = center;
        Save();
        LastDecision = $"Confirmed {center} W is best ({summary})";
        return StartRest(center, "confirmed best");
    }

    /// <summary>Set when a comparison finishes; the controller turns it into a timeline event and clears it.</summary>
    public string? LastDecision { get; set; }

    private (int, string)? StartWindow(Stage stage, int capW, string reason)
    {
        _stage = stage;
        _stageSeconds = 0;
        _current = new Window { CapW = capW };
        Status = $"{StageLabel(stage)} {capW} W · 0/{WindowSeconds} s";
        return (capW, reason);
    }

    private (int, string)? StartRest(int capW, string reason)
    {
        _stage = Stage.Resting;
        _stageSeconds = 0;
        _current = null;
        return (capW, reason);
    }

    private static string StageLabel(Stage s) => s switch
    {
        Stage.MeasureCenter => "Measuring",
        Stage.ProbeUp => "Testing",
        Stage.ProbeDown => "Testing",
        _ => "",
    };

    /// <summary>Summary for the dashboard: what has been learned for each game.</summary>
    public IReadOnlyList<object> Summary() => _store.Games
        .OrderByDescending(g => g.Value.Updated)
        .Select(g =>
        {
            var best = g.Value.BestCapW is int b && g.Value.Stats.TryGetValue(b, out var s) && s.Seconds > 0 ? s : null;
            return (object)new
            {
                game = g.Key,
                bestCapW = g.Value.BestCapW,
                efficientBudgetW = g.Value.EfficientBudgetW,
                bestClockMhz = best == null ? (int?)null : (int)(best.ClockSum / best.Seconds),
                sessions = g.Value.Sessions,
                testedCaps = g.Value.Stats.Count,
                minutes = g.Value.Stats.Values.Sum(x => x.Seconds) / 60,
            };
        })
        .ToList();
}
