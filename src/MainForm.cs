using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace DHelper;

/// <summary>
/// Window + tray. The dashboard is an HTML page (ui/index.html, embedded) shown in WebView2;
/// this class owns all hardware actions and talks to the page through JSON messages:
///   C# -> page: {type:"config"}, {type:"state"} every second, {type:"toast"}
///   page -> C#: {cmd:"ready"|"mode"|"battery"|"keyboard"|"light"|"lightBar"|"gameEnabled"|"games"|
///                     "gameUv"|"gameTarget"|"advice"|"runAtLogin"|"applyOnStart"|"hide"|"quit"}
/// </summary>
public sealed class MainForm : Form
{
    private readonly AsusAcpi _acpi;
    private readonly RyzenSmu _smu;
    private readonly AppConfig _config;
    private readonly bool _startMinimized;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem[] _trayModeItems = new ToolStripMenuItem[3];
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(12, 14, 34) };
    private readonly GameController _game;
    private readonly string _model;
    private bool _pageReady, _exiting;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public MainForm(AsusAcpi acpi, RyzenSmu smu, AppConfig config, bool startMinimized)
    {
        _acpi = acpi;
        _smu = smu;
        _config = config;
        _startMinimized = startMinimized;
        _model = GetModelName();

        Text = "D-Helper";
        // The icon embedded in DHelper.exe (DHelper.ico); the drawn one is only a fallback.
        Icon = (Environment.ProcessPath != null ? Icon.ExtractAssociatedIcon(Environment.ProcessPath) : null) ?? CreateAppIcon();
        BackColor = Color.FromArgb(12, 14, 34);
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Size = new Size(Math.Min(1320, area.Width - 40), Math.Min(900, area.Height - 40));
        MinimumSize = new Size(900, 600);
        Controls.Add(_web);

        _tray = new NotifyIcon { Icon = Icon, Text = "D-Helper", Visible = true, ContextMenuStrip = BuildTrayMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

        // On first run there is no saved choice yet, so don't overwrite the laptop's current state.
        if (_config.ApplyOnStartup && AppConfig.Exists) ApplySavedSettings();
        UpdateTrayMode(_config.PerformanceMode);

        _game = new GameController(_acpi, _smu, _config);
        _game.Changed += () => { if (IsHandleCreated && !IsDisposed) BeginInvoke(PostState); };
        if (_game.CrashGuardMessage != null)
            _tray.ShowBalloonTip(10000, "D-Helper crash guard", _game.CrashGuardMessage, ToolTipIcon.Warning);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _ = InitWebAsync();
    }

    // ---------- WebView2 ----------

    private async Task InitWebAsync()
    {
        try
        {
            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D-Helper", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsStatusBarEnabled = false;
            _web.CoreWebView2.WebMessageReceived += (_, e) => HandleMessage(e.WebMessageAsJson);
            _web.CoreWebView2.NavigateToString(LoadPage());
        }
        catch (Exception ex)
        {
            Log.Write("WebView2 failed: " + ex.Message);
            Controls.Remove(_web);
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12),
                Text = "The dashboard needs the Microsoft Edge WebView2 Runtime.\nInstall it from microsoft.com/edge/webview2 and restart D-Helper.\n\n" + ex.Message,
            });
        }
    }

    private static string LoadPage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DHelper.ui.index.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Post(object message)
    {
        if (!_pageReady || _web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json)); } catch { }
    }

    private void PostConfig() => Post(new
    {
        type = "config",
        model = _model,
        mode = _config.PerformanceMode,
        battery = _config.BatteryLimit,
        batteryNotSet = !AppConfig.Exists,
        gpuSupported = _acpi.GetGpuEco() >= 0 && _acpi.GetGpuMux() != 0,
        keyboard = _config.KeyboardLevel,
        lightMode = _config.LightMode,
        lightColor = ToHex(Color.FromArgb(_config.LightColor)),
        lightSpeed = _config.LightSpeed,
        lightBar = _config.LightBarAwake,
        gameEnabled = _config.GameModeEnabled,
        games = _config.GameExecutables,
        gameUv = _config.GameUndervolt,
        safeUv = _config.SafeUndervolt,
        gameTarget = _config.GameTempTarget,
        steadyClocks = _config.SteadyClocks,
        targetFps = _config.TargetFps,
        fanBoost = _config.FanBoost,
        hotkey = _hotkeyRegistered ? "Ctrl+Alt+L" : null,
        runAtLogin = AppConfig.RunAtLogin,
        applyOnStart = _config.ApplyOnStartup,
    });

    private void PostState()
    {
        var s = _game.GetSnapshot();
        UpdateTray(s);
        Post(new
        {
            type = "state",
            s.Phase, s.StatusText, s.Game, s.Health, s.HealthTitle, s.HealthDetail,
            s.CpuLoad, s.Tctl, s.TctlLimit, s.PowerW, s.PowerLimitW,
            s.ClockMhz, s.PeakClockMhz, s.MaxClockMhz, s.CpuFanRpm, s.GpuFanRpm,
            s.ThermalLimited, s.PowerLimited, s.ClockDrop,
            s.UndervoltActive, s.UndervoltValue, s.CoolingBoost, s.SmuReady, s.SmuStatus,
            s.PowerCapW, s.LagCount, s.LearningStatus,
            s.Fps, s.FpsLow, s.TargetFps, s.Strategy, s.BudgetW, s.SavingW, s.ModeName, s.GameForeground,
            learned = _game.Learner.Summary(),
            events =s.Events.Select(e => new { time = e.Time.ToString("HH:mm:ss"), e.Kind, e.Text }),
            advice = s.Advice,
            s.CrashGuardMessage,
            logError = Log.LastError,
        });
        if (s.CrashGuardMessage != null) _game.DismissCrashGuard();
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            string cmd = m.GetProperty("cmd").GetString() ?? "";
            int Int() => m.GetProperty("value").GetInt32();
            bool Bool() => m.GetProperty("value").GetBoolean();

            switch (cmd)
            {
                case "ready": _pageReady = true; PostConfig(); PostState(); break;
                case "mode": ApplyMode(Int(), save: true); break;
                case "battery":
                    _config.BatteryLimit = Math.Clamp(Int(), 40, 100);
                    _acpi.SetBatteryLimit(_config.BatteryLimit);
                    _config.Save();
                    break;
                case "keyboard":
                    _config.KeyboardLevel = Math.Clamp(Int(), 0, 3);
                    SetKeyboardHardware(_config.KeyboardLevel);
                    _config.Save();
                    break;
                case "light":
                    _config.LightMode = m.GetProperty("mode").GetInt32();
                    _config.LightSpeed = m.GetProperty("speed").GetInt32();
                    _config.LightColor = ColorTranslator.FromHtml(m.GetProperty("color").GetString() ?? "#bb0000").ToArgb();
                    _config.LightingSet = true;
                    _config.Save();
                    AsusHid.SetAura((AsusHid.AuraMode)_config.LightMode, Color.FromArgb(_config.LightColor), (AsusHid.AuraSpeed)_config.LightSpeed);
                    break;
                case "lightBar":
                    _config.LightBarAwake = Bool();
                    _config.LightingSet = true;
                    _config.Save();
                    AsusHid.SetPower(new AsusHid.AuraPower(AwakeBar: _config.LightBarAwake));
                    break;
                case "gameEnabled": _config.GameModeEnabled = Bool(); _config.Save(); break;
                case "games":
                    _config.GameExecutables = (m.GetProperty("value").GetString() ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    _config.Save();
                    break;
                case "gameUv": _config.GameUndervolt = Math.Clamp(Int(), -50, 0); _config.Save(); break;
                case "gameTarget": _config.GameTempTarget = Math.Clamp(Int(), 75, 100); _config.Save(); break;
                case "steady": _config.SteadyClocks = Bool(); _config.Save(); break;
                case "fanBoost": _config.FanBoost = Bool(); _config.Save(); break;
                case "targetFps": _config.TargetFps = Math.Clamp(Int(), 30, 240); _config.Save(); break;
                case "lag": ReportLag(); break;
                case "resetLearning": _game.Learner.Reset(); Post(new { type = "toast", text = "Learning reset: D-Helper will learn each game again." }); break;
                case "advice": _game.RunAdvice(m.GetProperty("id").GetString() ?? ""); PostConfig(); break;
                case "runAtLogin": AppConfig.RunAtLogin = Bool(); PostConfig(); break;
                case "applyOnStart": _config.ApplyOnStartup = Bool(); _config.Save(); break;
                case "export": RunExport(); break;
                case "hide": Hide(); break;
                case "quit": ExitApp(); break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("UI message error: " + ex.Message);
        }
    }

    // ---------- hardware actions ----------

    private void ApplySavedSettings()
    {
        ApplyMode(_config.PerformanceMode, save: false);
        _acpi.SetBatteryLimit(_config.BatteryLimit);
        SetKeyboardHardware(_config.KeyboardLevel);
        if (_config.LightingSet)
        {
            AsusHid.SetAura((AsusHid.AuraMode)_config.LightMode, Color.FromArgb(_config.LightColor), (AsusHid.AuraSpeed)_config.LightSpeed);
            AsusHid.SetPower(new AsusHid.AuraPower(AwakeBar: _config.LightBarAwake));
        }
    }

    private void ApplyMode(int mode, bool save)
    {
        int result = _acpi.SetPerformanceMode(mode);
        if (result != 1) Log.Write($"Performance mode {mode} not accepted (result {result})");
        if (save)
        {
            _config.PerformanceMode = mode;
            _config.Save();
            PostConfig();
        }
        UpdateTrayMode(mode);
    }

    /// <summary>ROG laptops take brightness over USB HID; TUF/Vivobook-style models use the ACPI device instead.</summary>
    private void SetKeyboardHardware(int level)
    {
        if (AsusHid.SetKeyboardBrightness(level) > 0) return;
        if (_acpi.IsSupported(AsusAcpi.KeyboardBrightness)) _acpi.SetKeyboardBrightness(level);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        BeginInvoke(() =>
        {
            Log.Write("Resumed from sleep");
            _game.OnResume();
            if (_config.ApplyOnStartup) ApplySavedSettings();
        });
    }

    // ---------- tray ----------

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        (string name, int mode)[] modes = { ("Silent", AsusAcpi.ModeSilent), ("Balanced", AsusAcpi.ModeBalanced), ("Turbo", AsusAcpi.ModeTurbo) };
        for (int i = 0; i < modes.Length; i++)
        {
            int mode = modes[i].mode;
            _trayModeItems[i] = new ToolStripMenuItem(modes[i].name, null, (_, _) => ApplyMode(mode, save: true)) { Tag = mode };
            menu.Items.Add(_trayModeItems[i]);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open D-Helper", null, (_, _) => ShowFromTray());
        menu.Items.Add("Export diagnostics", null, (_, _) => RunExport());
        menu.Items.Add("Quit", null, (_, _) => ExitApp());
        return menu;
    }

    private void UpdateTrayMode(int mode)
    {
        foreach (var item in _trayModeItems)
            if (item != null) item.Checked = (int)item.Tag! == mode;
    }

    private GameController.Health _lastTrayHealth;

    private void UpdateTray(GameController.Snapshot s)
    {
        string temp = s.Tctl.HasValue ? $" · {s.Tctl:F0}°C" : "";
        string text = $"D-Helper · {s.HealthTitle}{temp}";
        _tray.Text = text.Length > 63 ? text[..63] : text;

        // Notify once when throttling starts and the window is hidden.
        if (s.Health == GameController.Health.Throttling && _lastTrayHealth != GameController.Health.Throttling && !Visible)
            _tray.ShowBalloonTip(5000, "D-Helper: throttling", s.HealthDetail, ToolTipIcon.Warning);
        _lastTrayHealth = s.Health;
    }

    // ---------- Export diagnostics ----------

    /// <summary>
    /// Copies settings, learning data, the log, lag reports and the latest session logs into
    /// Documents\D-Helper\diagnostics\yyyyMMdd-HHmmss, plus a summary of the live state. Returns the folder.
    /// </summary>
    private string ExportDiagnostics()
    {
        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "D-Helper", "diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dest);

        // Files may be open for writing (the current session log), so copy with shared read access.
        void CopyShared(string src, string name)
        {
            try
            {
                using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = File.Create(Path.Combine(dest, name));
                input.CopyTo(output);
            }
            catch (Exception ex) { File.AppendAllText(Path.Combine(dest, "export-errors.txt"), $"{name}: {ex.Message}{Environment.NewLine}"); }
        }

        string data = AppConfig.Folder;
        foreach (var name in new[] { "config.json", "learning.json", "log.txt", "log-2.txt", "lag-reports.csv" })
            if (File.Exists(Path.Combine(data, name))) CopyShared(Path.Combine(data, name), name);
        foreach (var f in new DirectoryInfo(data).GetFiles("session-*.csv").OrderByDescending(f => f.LastWriteTime).Take(5))
            CopyShared(f.FullName, f.Name);

        var s = _game.GetSnapshot();
        var summary = new
        {
            exported = DateTime.Now,
            app = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            exe = Environment.ProcessPath,
            dataFolder = data,
            model = _model,
            smu = _smu.Status,
            logError = Log.LastError,
            hotkey = _hotkeyRegistered,
            state = s with { Events = s.Events.Take(40).ToList() },
            learned = _game.Learner.Summary(),
        };
        File.WriteAllText(Path.Combine(dest, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(Json) { WriteIndented = true }));
        Log.Write("Diagnostics exported to " + dest);
        return dest;
    }

    private void RunExport()
    {
        try
        {
            string dest = ExportDiagnostics();
            Post(new { type = "toast", text = "Diagnostics saved to Documents\\D-Helper\\diagnostics\\" + Path.GetFileName(dest) });
            System.Diagnostics.Process.Start("explorer.exe", $"\"{dest}\"");
        }
        catch (Exception ex)
        {
            Post(new { type = "toast", text = "Export failed: " + ex.Message });
        }
    }

    // ---------- "Lag!" hotkey (Ctrl+Alt+L, works in-game) ----------

    private const int WM_HOTKEY = 0x0312, LagHotkeyId = 0x4C41;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private bool _hotkeyRegistered;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeyRegistered = RegisterHotKey(Handle, LagHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.L);
        if (!_hotkeyRegistered) Log.Write("Could not register Ctrl+Alt+L (another app uses it)");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == LagHotkeyId) ReportLag();
        base.WndProc(ref m);
    }

    private void ReportLag()
    {
        System.Media.SystemSounds.Asterisk.Play(); // audible "got it" while in a fullscreen game
        string verdict = _game.ReportLag();
        Post(new { type = "toast", text = verdict });
        if (!Visible) _tray.ShowBalloonTip(4000, "Lag reported", verdict, ToolTipIcon.Info);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- window lifecycle ----------

    protected override void SetVisibleCore(bool value)
    {
        if (_startMinimized && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private void ExitApp()
    {
        _exiting = true;
        if (_hotkeyRegistered) UnregisterHotKey(Handle, LagHotkeyId);
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _game.Dispose(); // leaves game mode and restores the safe undervolt
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    // ---------- helpers ----------

    private static string ToHex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static string GetModelName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var product = key?.GetValue("SystemProductName") as string;
            if (string.IsNullOrWhiteSpace(product)) return "ASUS laptop";
            product = product.Trim();
            int us = product.IndexOf('_'); // "ROG Strix G513QY_G513QY" -> "ROG Strix G513QY"
            return us > 0 ? product[..us] : product;
        }
        catch { return "ASUS laptop"; }
    }

    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            path.AddArc(2, 2, 24, 24, 180, 90); path.AddArc(38, 2, 24, 24, 270, 90);
            path.AddArc(38, 38, 24, 24, 0, 90); path.AddArc(2, 38, 24, 24, 90, 90);
            path.CloseFigure();
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(255, 45, 72), Color.FromArgb(212, 20, 44), 90f);
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI", 34f, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("D", font);
            using var gold = new SolidBrush(Color.FromArgb(255, 197, 61));
            g.DrawString("D", font, gold, (64 - size.Width) / 2, (64 - size.Height) / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
