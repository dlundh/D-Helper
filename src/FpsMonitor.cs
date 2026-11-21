using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace DHelper;

/// <summary>
/// Frame-rate measurement for every process, from the graphics kernel's present events (no overlay, nothing
/// injected into games).
///
/// Recovered with REA from G-Helper 0.291 (GHelper.Overlay.EtwFpsMonitor), Windows 11 path:
///   real-time ETW session, provider Microsoft-Windows-DxgKrnl {802EC45A-1E99-4B83-9920-87C98277BA9D},
///   level Information, keyword 0x8000000, event-ID filter = 184 (present info). One event per presented frame.
/// G-Helper tracks one chosen process; D-Helper keeps the last 10 s of frames for every process, which also
/// lets it find the game by itself (the fullscreen foreground app that is presenting frames).
/// Requires administrator rights.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const int PresentInfoEventId = 184;
    private const ulong KeywordPresent = 0x8000000;

    // Microsoft-Windows-DXGI, Present_Start (event 42): one per IDXGISwapChain::Present call. This is what
    // DirectX 11/12 games are reliably seen through (G-Helper's Windows 10 path; also what PresentMon uses).
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private const int DxgiPresentStartEventId = 42;
    private const double KeepMs = 10_000;

    private readonly string _sessionName;
    private TraceEventSession? _session;
    private Thread? _thread;
    private readonly object _lock = new();
    private readonly Dictionary<int, Queue<double>> _frames = new();   // pid -> frame timestamps (ms, session clock)
    private readonly Dictionary<int, long> _lastSeen = new();          // pid -> Stopwatch ticks when last frame arrived
    private readonly Dictionary<int, long> _dxgiSeen = new();          // pid -> when a DXGI present was last seen

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "not started";
    public long DxgiEvents, KernelEvents;

    public FpsMonitor(string sessionName = "D-Helper-FPS") => _sessionName = sessionName;

    public void Start()
    {
        try
        {
            _session = new TraceEventSession(_sessionName) { StopOnDispose = true };
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, 0,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { DxgiPresentStartEventId } });
            var options = new TraceEventProviderOptions { EventIDsToEnable = new List<int> { PresentInfoEventId } };
            _session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, KeywordPresent, options);
            _session.Source.AllEvents += OnEvent;
            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Status = "stopped: " + ex.Message; IsRunning = false; }
            }) { IsBackground = true, Name = "D-Helper FPS" };
            _thread.Start();
            IsRunning = true;
            Status = "measuring";
        }
        catch (Exception ex)
        {
            // Seen with EA anti-cheat running: enabling ANY provider is refused while the game is open.
            // A session started before the game launches keeps working, so clean up and retry later.
            Status = ex is UnauthorizedAccessException
                ? "blocked (a game's anti-cheat refuses new traces while it runs; retrying)"
                : "unavailable: " + ex.Message;
            Log.Write("FPS monitor: " + Status);
            try { _session?.Dispose(); } catch { }
            _session = null;
            IsRunning = false;
        }
        _lastAttempt = Stopwatch.GetTimestamp();
    }

    private long _lastAttempt;

    /// <summary>Called every second: retries a blocked start every 20 s.</summary>
    public void EnsureRunning()
    {
        if (IsRunning || Stopwatch.GetElapsedTime(_lastAttempt).TotalSeconds < 20) return;
        Start();
    }

    private void OnEvent(TraceEvent e)
    {
        bool dxgi = e.ProviderGuid == Dxgi && (int)e.ID == DxgiPresentStartEventId;
        bool kernel = e.ProviderGuid == DxgKrnl && (int)e.ID == PresentInfoEventId;
        if (!dxgi && !kernel) return;
        int pid = e.ProcessID;
        if (pid <= 4) return;
        double t = e.TimeStampRelativeMSec;
        lock (_lock)
        {
            if (dxgi) { DxgiEvents++; _dxgiSeen[pid] = Stopwatch.GetTimestamp(); }
            else
            {
                KernelEvents++;
                // Same frame seen twice? Prefer the DXGI count for processes that use DXGI.
                if (_dxgiSeen.TryGetValue(pid, out long seen) && Stopwatch.GetElapsedTime(seen).TotalSeconds < 2) return;
            }
            if (!_frames.TryGetValue(pid, out var q)) _frames[pid] = q = new Queue<double>(1024);
            q.Enqueue(t);
            while (q.Count > 0 && t - q.Peek() > KeepMs) q.Dequeue();
            _lastSeen[pid] = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Average FPS over the last second of frames and the "1 % low" FPS over the last 10 s, or null if the process isn't presenting.</summary>
    public (double Fps, double Low)? Get(int pid)
    {
        lock (_lock)
        {
            if (!_frames.TryGetValue(pid, out var q) || q.Count < 3) return null;
            if (Stopwatch.GetElapsedTime(_lastSeen[pid]).TotalSeconds > 3) return null; // stopped presenting

            var times = q.ToArray();
            double last = times[^1];
            int inLastSecond = times.Count(x => last - x <= 1000);
            double span = last - times.First(x => last - x <= 1000);
            double fps = inLastSecond > 1 && span > 0 ? (inLastSecond - 1) * 1000.0 / span : 0;

            var gaps = new double[times.Length - 1];
            for (int i = 1; i < times.Length; i++) gaps[i - 1] = times[i] - times[i - 1];
            Array.Sort(gaps);
            double p99 = gaps[Math.Min(gaps.Length - 1, (int)(gaps.Length * 0.99))];
            double low = p99 > 0 ? 1000.0 / p99 : fps;
            return (fps, Math.Min(low, fps));
        }
    }

    /// <summary>Forget a process's frame history (e.g. frames it rendered while tabbed out).</summary>
    public void Reset(int pid)
    {
        lock (_lock) _frames.Remove(pid);
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        IsRunning = false;
    }
}
