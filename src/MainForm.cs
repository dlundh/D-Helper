using System.Drawing.Drawing2D;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace DHelper;

public sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(28, 28, 30);
    private static readonly Color Panel = Color.FromArgb(40, 40, 44);
    private static readonly Color Fg = Color.FromArgb(235, 235, 240);
    private static readonly Color Muted = Color.FromArgb(150, 150, 160);
    private static readonly Color Accent = Color.FromArgb(0, 150, 255);
    private static readonly Color ButtonBg = Color.FromArgb(58, 58, 64);

    private readonly AsusAcpi _acpi;
    private readonly AppConfig _config;
    private readonly bool _startMinimized;
    private readonly NotifyIcon _tray;
    private readonly Timer _sensorTimer;

    private readonly Label _sensors = new();
    private readonly Label _gpuStatus = new();
    private readonly Label _batteryValue = new();
    private readonly TrackBar _battery = new();
    private readonly Dictionary<int, Button> _modeButtons = new();
    private readonly Dictionary<bool, Button> _gpuButtons = new();
    private readonly Dictionary<int, Button> _kbButtons = new();
    private readonly ToolStripMenuItem[] _trayModeItems = new ToolStripMenuItem[3];
    private readonly Dictionary<int, Button> _lightModeButtons = new();
    private readonly Dictionary<int, Button> _lightSpeedButtons = new();
    private readonly CheckBox _lightBar = new();
    private Button _colorButton = null!;
    private readonly NumericUpDown _gameUv = new();
    private bool _loadingUi = true;

    private bool _gpuSupported;
    private bool _gpuBusy;
    private bool _exiting;

    private readonly RyzenSmu _smu;
    private GameController? _game;
    private readonly Label _gameStatus = new();
    private readonly Label _telemetry = new();

    public MainForm(AsusAcpi acpi, RyzenSmu smu, AppConfig config, bool startMinimized)
    {
        _acpi = acpi;
        _smu = smu;
        _config = config;
        _startMinimized = startMinimized;

        Text = "D-Helper";
        Icon = CreateAppIcon();
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        BuildUi();

        _tray = new NotifyIcon { Icon = Icon, Text = "D-Helper", Visible = true, ContextMenuStrip = BuildTrayMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

        _sensorTimer = new Timer { Interval = 2000 };
        _sensorTimer.Tick += (_, _) => RefreshSensors();

        // On first run there is no saved choice yet, so don't overwrite the laptop's current state.
        if (_config.ApplyOnStartup && AppConfig.Exists)
        {
            ApplyMode(_config.PerformanceMode, save: false);
            _acpi.SetBatteryLimit(_config.BatteryLimit);
            SetKeyboardHardware(_config.KeyboardLevel);
            ReapplyLighting();
        }
        HighlightLighting();
        _loadingUi = false;
        if (AppConfig.Exists)
        {
            HighlightMode(_config.PerformanceMode);
            HighlightKeyboard(_config.KeyboardLevel);
        }
        else
        {
            // Nothing chosen yet: the hardware state is whatever another tool last set, so don't pretend to know it.
            _batteryValue.Text = "not set";
        }
        RefreshGpuState();
        RefreshSensors();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _game = new GameController(_acpi, _smu, _config);
        _game.Changed += () => { if (IsHandleCreated) BeginInvoke(RefreshGameStatus); };
        if (_game.CrashGuardMessage != null)
            _tray.ShowBalloonTip(10000, "D-Helper crash guard", _game.CrashGuardMessage, ToolTipIcon.Warning);
    }

    private void RefreshGameStatus()
    {
        if (_game == null) return;
        _gameStatus.Text = _game.StatusText;
        _gameStatus.ForeColor = _game.CurrentPhase switch
        {
            GameController.Phase.GameUndervolted => Color.FromArgb(90, 200, 120),
            GameController.Phase.GameIdle => Color.FromArgb(240, 200, 80),
            GameController.Phase.Unavailable => Color.FromArgb(240, 110, 100),
            _ => Muted,
        };
        var t = _game.LastTelemetry;
        _telemetry.Text = t == null ? "" :
            $"Tctl {t.TctlValue:F0}/{t.TctlLimit:F0}°C   CPU {_game.LastCpuLoad}%   power {t.SlowValue:F0} W" +
            (t.ThermalLimited ? "   THERMAL LIMIT" : t.PowerLimited ? "   POWER LIMIT" : "");
        if (_game.CrashGuardMessage != null) _gameUv.Value = Math.Clamp(_config.GameUndervolt, -50, 0);
    }

    // ---------- UI construction ----------

    private void BuildUi()
    {
        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = true, // flows into a second column when taller than the screen
            MaximumSize = new Size(0, Screen.PrimaryScreen!.WorkingArea.Height - 110),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Bg,
        };

        var title = new Label
        {
            Text = "D-Helper",
            Font = new Font("Segoe UI Semibold", 16f),
            AutoSize = true,
            ForeColor = Fg,
        };
        var model = new Label
        {
            Text = GetModelName(),
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(3, 0, 3, 8),
        };
        _sensors.AutoSize = true;
        _sensors.ForeColor = Fg;
        _sensors.Font = new Font("Consolas", 10f);
        _sensors.Margin = new Padding(3, 0, 3, 8);

        root.Controls.Add(title);
        root.Controls.Add(model);
        root.Controls.Add(_sensors);

        // Performance mode
        root.Controls.Add(Section("Performance mode",
            ModeButton("Silent", AsusAcpi.ModeSilent),
            ModeButton("Balanced", AsusAcpi.ModeBalanced),
            ModeButton("Turbo", AsusAcpi.ModeTurbo)));

        // GPU mode
        var eco = MakeButton("Eco", (_, _) => SwitchGpu(true));
        var std = MakeButton("Standard", (_, _) => SwitchGpu(false));
        _gpuButtons[true] = eco;
        _gpuButtons[false] = std;
        _gpuStatus.AutoSize = true;
        _gpuStatus.ForeColor = Muted;
        _gpuStatus.Margin = new Padding(3, 4, 3, 0);
        var gpuSection = Section("GPU mode", eco, std);
        ((FlowLayoutPanel)gpuSection.Controls[0]).Controls.Add(_gpuStatus);
        root.Controls.Add(gpuSection);

        // Battery limit
        _battery.Minimum = 40;
        _battery.Maximum = 100;
        _battery.SmallChange = 5;
        _battery.LargeChange = 5;
        _battery.TickFrequency = 10;
        _battery.Width = 300;
        _battery.Value = Math.Clamp(_config.BatteryLimit, 40, 100);
        _battery.BackColor = Panel;
        _battery.ValueChanged += (_, _) =>
        {
            int snapped = (int)Math.Round(_battery.Value / 5.0) * 5;
            if (snapped != _battery.Value) _battery.Value = snapped;
            _batteryValue.Text = $"{_battery.Value}%";
        };
        _battery.MouseUp += (_, _) => ApplyBattery();
        _battery.KeyUp += (_, _) => ApplyBattery();
        _batteryValue.Text = $"{_battery.Value}%";
        _batteryValue.AutoSize = true;
        _batteryValue.Margin = new Padding(3, 10, 3, 3);
        root.Controls.Add(Section("Battery charge limit", _battery, _batteryValue));

        // Keyboard backlight
        string[] kbNames = { "Off", "Low", "Medium", "High" };
        var kb = new Control[4];
        for (int i = 0; i < 4; i++)
        {
            int level = i;
            var b = MakeButton(kbNames[i], (_, _) => ApplyKeyboard(level));
            _kbButtons[level] = b;
            kb[i] = b;
        }
        root.Controls.Add(Section("Keyboard backlight", kb));

        // Lighting: effect, colour, speed, light bar power
        (string name, AsusHid.AuraMode mode)[] effects =
        {
            ("Static", AsusHid.AuraMode.Static), ("Breathe", AsusHid.AuraMode.Breathe),
            ("Color cycle", AsusHid.AuraMode.ColorCycle), ("Rainbow", AsusHid.AuraMode.Rainbow),
            ("Strobe", AsusHid.AuraMode.Strobe),
        };
        var effectButtons = new List<Control>();
        foreach (var (name, mode) in effects)
        {
            var b = MakeButton(name, (_, _) => { _config.LightMode = (int)mode; ApplyLighting(); });
            _lightModeButtons[(int)mode] = b;
            effectButtons.Add(b);
        }
        root.Controls.Add(Section("Lighting effect", effectButtons.ToArray()));

        _colorButton = MakeButton("Color", (_, _) => PickColor());
        _colorButton.MinimumSize = new Size(96, 34);
        var speeds = new[] { ("Slow", 0), ("Normal", 1), ("Fast", 2) }.Select(s =>
        {
            var b = MakeButton(s.Item1, (_, _) => { _config.LightSpeed = s.Item2; ApplyLighting(); });
            _lightSpeedButtons[s.Item2] = b;
            return (Control)b;
        });
        _lightBar.Text = "Light bar on";
        _lightBar.AutoSize = true;
        _lightBar.ForeColor = Fg;
        _lightBar.Margin = new Padding(8, 9, 3, 3);
        _lightBar.Checked = _config.LightBarAwake;
        _lightBar.CheckedChanged += (_, _) =>
        {
            if (_loadingUi) return;
            _config.LightBarAwake = _lightBar.Checked;
            ApplyLightBarPower();
        };
        root.Controls.Add(Section("Color, speed and light bar",
            new Control[] { _colorButton }.Concat(speeds).Append(_lightBar).ToArray()));

        // Game mode
        var gameEnabled = new CheckBox
        {
            Text = "Game mode (Turbo + load-gated undervolt + cooling)",
            AutoSize = true,
            Checked = _config.GameModeEnabled,
            ForeColor = Fg,
        };
        gameEnabled.CheckedChanged += (_, _) => { _config.GameModeEnabled = gameEnabled.Checked; _config.Save(); };
        _gameStatus.AutoSize = true;
        _gameStatus.MaximumSize = new Size(420, 0);
        _gameStatus.ForeColor = Muted;
        _gameStatus.Text = "Starting…";
        _telemetry.AutoSize = true;
        _telemetry.ForeColor = Fg;
        _telemetry.Font = new Font("Consolas", 9.5f);

        var gamesLabel = new Label { Text = "Games (exe names, comma separated):", AutoSize = true, ForeColor = Muted, Margin = new Padding(3, 8, 3, 0) };
        var games = new TextBox
        {
            Text = string.Join(", ", _config.GameExecutables),
            Width = 410,
            BackColor = ButtonBg,
            ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle,
        };
        games.Leave += (_, _) =>
        {
            _config.GameExecutables = games.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            _config.Save();
        };

        _gameUv.Minimum = -50;
        _gameUv.Maximum = 0;
        _gameUv.Increment = 5;
        _gameUv.Value = Math.Clamp(_config.GameUndervolt, -50, 0);
        _gameUv.Width = 60;
        _gameUv.BackColor = ButtonBg;
        _gameUv.ForeColor = Fg;
        _gameUv.ValueChanged += (_, _) => { _config.GameUndervolt = (int)_gameUv.Value; _config.Save(); };

        var target = new NumericUpDown { Minimum = 75, Maximum = 95, Value = Math.Clamp(_config.GameTempTarget, 75, 95), Width = 60, BackColor = ButtonBg, ForeColor = Fg };
        target.ValueChanged += (_, _) => { _config.GameTempTarget = (int)target.Value; _config.Save(); };

        root.Controls.Add(Section("Game mode",
            gameEnabled, _gameStatus, _telemetry, gamesLabel, games,
            new Label { Text = "Undervolt under load:", AutoSize = true, ForeColor = Fg, Margin = new Padding(3, 10, 0, 0) }, _gameUv,
            new Label { Text = "CPU limit °C:", AutoSize = true, ForeColor = Fg, Margin = new Padding(12, 10, 0, 0) }, target));

        // Options
        var runAtLogin = new CheckBox
        {
            Text = "Start D-Helper when I sign in",
            AutoSize = true,
            Checked = AppConfig.RunAtLogin,
            ForeColor = Fg,
        };
        runAtLogin.CheckedChanged += (_, _) => AppConfig.RunAtLogin = runAtLogin.Checked;

        var applyOnStart = new CheckBox
        {
            Text = "Re-apply my settings at start and after sleep",
            AutoSize = true,
            Checked = _config.ApplyOnStartup,
            ForeColor = Fg,
        };
        applyOnStart.CheckedChanged += (_, _) => { _config.ApplyOnStartup = applyOnStart.Checked; _config.Save(); };

        root.Controls.Add(Section("Options", runAtLogin, applyOnStart));

        var hide = MakeButton("Hide to tray", (_, _) => Hide());
        var quit = MakeButton("Quit", (_, _) => ExitApp());
        var footer = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0), BackColor = Bg };
        footer.Controls.Add(hide);
        footer.Controls.Add(quit);
        root.Controls.Add(footer);

        Controls.Add(root);
    }

    private static Panel Section(string caption, params Control[] controls)
    {
        var outer = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Panel,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 0, 10),
            MinimumSize = new Size(440, 0),
        };
        var inner = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            MaximumSize = new Size(440, 0),
            BackColor = Panel,
            Dock = DockStyle.Bottom,
        };
        var label = new Label
        {
            Text = caption.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = Muted,
            Font = new Font("Segoe UI Semibold", 8.5f),
            Dock = DockStyle.Top,
        };
        foreach (var c in controls) inner.Controls.Add(c);
        outer.Controls.Add(inner);
        outer.Controls.Add(label);
        return outer;
    }

    private Button ModeButton(string text, int mode)
    {
        var b = MakeButton(text, (_, _) => ApplyMode(mode, save: true));
        _modeButtons[mode] = b;
        return b;
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = ButtonBg,
            ForeColor = Fg,
            AutoSize = true,
            MinimumSize = new Size(96, 34),
            Margin = new Padding(3),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = ButtonBg;
        b.FlatAppearance.BorderSize = 2;
        b.Click += onClick;
        return b;
    }

    private static void Highlight<T>(Dictionary<T, Button> buttons, T selected) where T : notnull
    {
        foreach (var (key, b) in buttons)
        {
            bool on = EqualityComparer<T>.Default.Equals(key, selected);
            b.FlatAppearance.BorderColor = on ? Accent : ButtonBg;
            b.BackColor = on ? Color.FromArgb(20, 70, 110) : ButtonBg;
        }
    }

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
        menu.Items.Add("Quit", null, (_, _) => ExitApp());
        return menu;
    }

    // ---------- Actions ----------

    private void ApplyMode(int mode, bool save)
    {
        int result = _acpi.SetPerformanceMode(mode);
        if (result != 1) Log.Write($"Performance mode {mode} not accepted (result {result})");
        if (save)
        {
            _config.PerformanceMode = mode;
            _config.Save();
        }
        HighlightMode(mode);
    }

    private void HighlightMode(int mode)
    {
        Highlight(_modeButtons, mode);
        foreach (var item in _trayModeItems)
            if (item != null) item.Checked = (int)item.Tag! == mode;
        string name = mode switch { AsusAcpi.ModeSilent => "Silent", AsusAcpi.ModeTurbo => "Turbo", _ => "Balanced" };
        if (_tray != null) _tray.Text = $"D-Helper — {name}";
    }

    private void ApplyBattery()
    {
        if (_battery.Value == _config.BatteryLimit && AppConfig.Exists) return;
        _acpi.SetBatteryLimit(_battery.Value);
        _batteryValue.Text = $"{_battery.Value}%";
        _config.BatteryLimit = _battery.Value;
        _config.Save();
    }

    private void ApplyKeyboard(int level)
    {
        SetKeyboardHardware(level);
        _config.KeyboardLevel = level;
        _config.Save();
        HighlightKeyboard(level);
    }

    private void HighlightKeyboard(int level) => Highlight(_kbButtons, level);

    private void ReapplyLighting()
    {
        if (!_config.LightingSet) return;
        AsusHid.SetAura((AsusHid.AuraMode)_config.LightMode, Color.FromArgb(_config.LightColor), (AsusHid.AuraSpeed)_config.LightSpeed);
        AsusHid.SetPower(new AsusHid.AuraPower(AwakeBar: _config.LightBarAwake));
    }

    private void ApplyLighting()
    {
        _config.LightingSet = true;
        _config.Save();
        AsusHid.SetAura((AsusHid.AuraMode)_config.LightMode, Color.FromArgb(_config.LightColor), (AsusHid.AuraSpeed)_config.LightSpeed);
        HighlightLighting();
    }

    private void ApplyLightBarPower()
    {
        _config.LightingSet = true;
        _config.Save();
        AsusHid.SetPower(new AsusHid.AuraPower(AwakeBar: _config.LightBarAwake));
    }

    private void PickColor()
    {
        using var dialog = new ColorDialog { Color = Color.FromArgb(_config.LightColor), FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _config.LightColor = dialog.Color.ToArgb();
        ApplyLighting();
    }

    private void HighlightLighting()
    {
        if (_config.LightingSet)
        {
            Highlight(_lightModeButtons, _config.LightMode);
            Highlight(_lightSpeedButtons, _config.LightSpeed);
        }
        var c = Color.FromArgb(_config.LightColor);
        _colorButton.BackColor = c;
        _colorButton.ForeColor = c.GetBrightness() > 0.5f ? Color.Black : Color.White;
        _colorButton.FlatAppearance.BorderColor = c;
    }

    /// <summary>ROG laptops take brightness over USB HID; TUF/Vivobook-style models use the ACPI device instead.</summary>
    private void SetKeyboardHardware(int level)
    {
        if (AsusHid.SetKeyboardBrightness(level) > 0) return;
        if (_acpi.IsSupported(AsusAcpi.KeyboardBrightness)) _acpi.SetKeyboardBrightness(level);
    }

    private async void SwitchGpu(bool eco)
    {
        if (!_gpuSupported || _gpuBusy) return;
        if (_acpi.GetGpuEco() == (eco ? 1 : 0)) return;

        if (eco)
        {
            var answer = MessageBox.Show(this,
                "Eco turns the dedicated GPU off.\n\nClose games and apps that use the dedicated GPU first, or they may crash.\n\nSwitch to Eco now?",
                "D-Helper", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.OK) return;
        }

        _gpuBusy = true;
        _gpuStatus.Text = "Switching…";
        foreach (var b in _gpuButtons.Values) b.Enabled = false;
        try
        {
            int result = await Task.Run(() => _acpi.SetGpuEco(eco));
            if (result != 1) Log.Write($"GPU Eco={eco} not accepted (result {result})");
            await Task.Delay(1500);
        }
        finally
        {
            _gpuBusy = false;
            RefreshGpuState();
        }
    }

    private void RefreshGpuState()
    {
        int mux = _acpi.GetGpuMux();
        int eco = _acpi.GetGpuEco();
        _gpuSupported = eco >= 0;

        if (!_gpuSupported)
        {
            _gpuStatus.Text = "GPU switching isn't supported on this model";
            foreach (var b in _gpuButtons.Values) b.Enabled = false;
            return;
        }
        if (mux == 0)
        {
            _gpuStatus.Text = "MUX is set to Ultimate (dGPU only), so Eco isn't available";
            foreach (var b in _gpuButtons.Values) b.Enabled = false;
            return;
        }

        foreach (var b in _gpuButtons.Values) b.Enabled = true;
        Highlight(_gpuButtons, eco == 1);
        _gpuStatus.Text = eco == 1 ? "Dedicated GPU is off" : "Dedicated GPU is on";
    }

    private void RefreshSensors()
    {
        if (!Visible) return;

        string Fmt(int temp, int rpm) =>
            $"{(temp > 0 ? temp + "°C" : "--"),5}  {(rpm >= 0 ? rpm + " RPM" : "--"),9}";

        int cpuT = _acpi.GetTemp(AsusAcpi.TempCpu);
        int gpuT = _acpi.GetTemp(AsusAcpi.TempGpu);
        int cpuF = _acpi.GetFanRpm(AsusAcpi.FanCpu);
        int gpuF = _acpi.GetFanRpm(AsusAcpi.FanGpu);
        _sensors.Text = $"CPU {Fmt(cpuT, cpuF)}\nGPU {Fmt(gpuT, gpuF)}";
        RefreshGameStatus();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || !_config.ApplyOnStartup) return;
        BeginInvoke(() =>
        {
            Log.Write("Resumed from sleep, re-applying settings");
            _game?.OnResume();
            ApplyMode(_config.PerformanceMode, save: false);
            _acpi.SetBatteryLimit(_config.BatteryLimit);
            SetKeyboardHardware(_config.KeyboardLevel);
            ReapplyLighting();
            RefreshGpuState();
        });
    }

    // ---------- Window / tray lifecycle ----------

    protected override void SetVisibleCore(bool value)
    {
        if (_startMinimized && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { RefreshGpuState(); RefreshSensors(); _sensorTimer.Start(); }
        else _sensorTimer.Stop();
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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _game?.Dispose(); // leaves game mode and restores the safe undervolt
        _sensorTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    // ---------- Helpers ----------

    private static string GetModelName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var product = key?.GetValue("SystemProductName") as string;
            return string.IsNullOrWhiteSpace(product) ? "ASUS laptop" : product.Trim();
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
            using var brush = new SolidBrush(Accent);
            g.FillEllipse(brush, 2, 2, 60, 60);
            using var font = new Font("Segoe UI", 30f, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("D", font);
            g.DrawString("D", font, Brushes.White, (64 - size.Width) / 2, (64 - size.Height) / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
