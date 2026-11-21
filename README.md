# D-Helper

A small tray app for ASUS ROG / TUF laptops, built from the hardware protocol recovered from G-Helper 0.291 with REA
(static reverse engineering). Developed and tested on a **ROG Strix G513QY** (Ryzen 9 5980HX).

![D-Helper](docs/screenshot.png)

## What it does

| Feature | How | Works on G513QY |
|---|---|---|
| Performance mode (Silent / Balanced / Turbo) | ATKACPI device `0x00120075` | Yes |
| Battery charge limit (40–100 %) | ATKACPI device `0x00120057` | Yes |
| Keyboard backlight (Off / Low / Medium / High) | USB HID feature report `5A BA C5 C4 <level>` (ACPI `0x00050021` fallback) | Yes (HID, PID 0x1866) |
| Lighting effect (Static / Breathe / Color cycle / Rainbow / Strobe), colour, speed | USB HID output report `5D B3 …` + `5D B5` + `5D B4` | Yes |
| Light bar on/off while awake | USB HID `5D BD 01 <kb> <bar> <lid> <rear> FF` | Yes |
| CPU / GPU temperature, fan RPM | ATKACPI `0x00120094`, `0x00120097`, `0x00110013`, `0x00110014` | CPU temp + both fans; no GPU temperature on this model |
| GPU Eco / Standard | ATKACPI `0x00090020` (+ MUX `0x00090016`) | Not supported by this model's firmware, so the section is disabled |
| Game mode: undervolt, CPU temperature limit, fan boost, telemetry | Ryzen SMU through the PawnIO driver + ATKACPI fan curves | Yes (Cezanne CPUs only) |
| Tray menu, start at sign-in, re-apply after sleep | `NotifyIcon`, Task Scheduler logon task, `SystemEvents.PowerModeChanged` | Yes |

Settings, a log and per-game CSV sessions are kept in `%APPDATA%\D-Helper\`.

## Dashboard

The window is a live dashboard (HTML in WebView2) that answers "is my laptop throttling right now?" at a glance:

* **Verdict:** *Full speed* (gold), *Fixing it* (amber), *Throttling* (red) or *Cool and quiet*, with the reason in plain words.
* **Temperature ring** against the CPU limit, plus tiles for effective CPU clock (with peak marker), SMU power, load and both fans.
* **2-minute chart** of temperature and clock, with throttling moments shaded red.
* **What D-Helper is doing:** a timeline of every automatic action. **How to fix it** cards appear when throttling persists,
  some with one-click fixes.

A tray balloon warns you if throttling starts while the window is hidden.

## Game mode: least power for 60+ FPS

There are no per-game settings. **Games are detected automatically**: any fullscreen foreground app that keeps
presenting frames (≥ 20 fps for 5 s; browsers, video players and other desktop apps are excluded). D-Helper
measures every game's frame rate itself from the graphics kernel's present events (ETW, recovered from G-Helper's
FPS monitor) and works towards **one global goal: stay above the target FPS (default 60) with as little power,
clock and fan noise as possible.**

* **Efficient (default):** one power budget drives the performance mode (Silent ≤ 45 W, Balanced ≤ 85 W, Turbo above,
  each with its own firmware fan curve) and the CPU power cap. With a clear margin (> +30 % FPS and good 1 % lows for
  5 s) the budget drops 8 % at a time; when FPS gets close to the target (< +10 %, or 1 % lows < 85 %) it rises ~20 %
  within 2 s. Easy games end up quiet and cool; you lose only frames you couldn't see the difference of.
* **Max performance:** if a game can't reach the target even at full power, D-Helper switches to everything below
  (Turbo, undervolt, fans, steady clocks, speed learning), and returns to Efficient once the game has 50 % to spare.
* **Memory:** the budget each game settles at is remembered, so the next session starts right there.
* `Ctrl+Alt+L` in Efficient mode immediately gives the game ~25 % more power.

The mechanisms underneath:

1. Sets the CPU temperature limit (default 93 °C) and picks the mode from the power budget. ASUS firmware sometimes
   resets limits mid-game; D-Helper checks them every second and puts them back within 3 s.
2. Applies the **game undervolt** (Curve Optimizer all-core, default −40) after 3 s at ≥ 25 % CPU load,
   or **immediately when throttling is detected**. It stays on through normal play and is only removed when the CPU is
   truly idle (< 12 % for 5 s, e.g. alt-tabbed) or the game closes, because a deep offset that is stable under load can
   crash at idle. At the desktop the safe offset (0) is always in effect.
3. **Fans first:** an aggressive fan curve kicks in 3 °C below the limit, or at once when throttling is detected.
4. **Throttle detection** every second: thermal limit, power limit, or the effective clock falling below 85 % of its
   recent peak under load. If it persists 15 s with undervolt and fan boost already on, D-Helper suggests next steps.
5. **Steady clocks (auto power cap):** if the CPU keeps hitting its limit even with the fans boosted, D-Helper lowers
   the CPU power limit ~6 % every 3 s until it sits just under the limit, giving a slightly lower but steady clock
   instead of boost-and-crash stutter. After 10 s with 5 °C of headroom it gives power back ~5 % at a time, and the
   original limits return when the game closes. (Tested: capping a 133 W draw to 80 W cut Tctl from 95 to 85 °C in 1 s.)
6. **Report lag — `Ctrl+Alt+L`, works in-game:** plays a short sound, records the moment (chart marker, timeline,
   `lag-reports.csv`) and checks whether the CPU was the bottleneck. If it was, D-Helper reacts harder at once
   (fans maxed, power cap stepped down ~12 %); if not, it tells you the stutter likely came from the GPU, network or game.
7. **Crash guard:** if Windows goes down while the undervolt is on, or CPU hardware errors (WHEA) are logged,
   the game undervolt is made 5 less aggressive automatically.
8. Logs a CSV of load, effective clock, temperature, power, power cap, lag marks, undervolt state and fan speed for each session.

The CPU temperature limit can be set from 75 to 100 °C (the Ryzen 9 5980HX's own maximum is 105 °C).

Undervolt and temperature-limit settings live in the CPU and reset at every reboot.
The SMU code only writes on AMD Cezanne (Ryzen 5000 mobile) CPUs; on anything else Game mode shows as unavailable.

## Requirements

* Windows 10/11 x64, the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) and the
  Microsoft Edge WebView2 Runtime (built into Windows 11)
* ASUS System Control Interface driver (provides `\\.\ATKACPI`; present if Armoury Crate or G-Helper works)
* [PawnIO](https://pawnio.eu/) driver for Game mode (G-Helper installs it). D-Helper runs as administrator because of it.

**Quit G-Helper and Armoury Crate before using D-Helper.** They write the same firmware settings and will override D-Helper's.

On the very first start, D-Helper only reads sensors and changes nothing. It applies a setting when you click it,
and from then on it re-applies your choices at start and after sleep (configurable under Options).

## Build

```powershell
git clone https://github.com/dlundh/D-Helper.git
cd D-Helper\src
dotnet publish -c Release -o ..\app -p:PublishSingleFile=true -p:DebugType=none
..\app\DHelper.exe
```

The build output in `app\` is `DHelper.exe` plus `RyzenSMU.bin`, which must stay next to the exe.

Optional shortcuts that start D-Helper through its sign-in task (no UAC prompt) with the D-Helper icon; pin the Start
menu one to the taskbar and it shares a button with the running window:

```powershell
Copy-Item src\DHelper.ico app\
.\tools\make-shortcut.ps1 -Path "$([Environment]::GetFolderPath('Desktop'))\D-Helper.lnk"
# all-users Start menu (run as administrator):
.\tools\make-shortcut.ps1 -Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\D-Helper.lnk"
```

Start D-Helper before games that use EA anti-cheat: it blocks starting frame-rate tracing while the game runs.

## Layout

```
src/
  AsusAcpi.cs        ATKACPI DeviceIoControl wrapper (modes, battery, sensors, fan curves)
  AsusHid.cs         USB HID: keyboard backlight, Aura lighting and light bar (SetupAPI + hid.dll)
  RyzenSmu.cs        Ryzen SMU via PawnIO: undervolt, Tctl limit, power limits, PM-table telemetry
  GameController.cs  auto game detection, efficient/performance strategy, load-gated undervolt, fan boost,
                     steady clocks, crash guard, CSV logging
  FpsMonitor.cs      per-process FPS and 1 % lows from DxgKrnl present events (ETW)
  CapLearner.cs      per-game memory: efficient budget and fastest steady power cap
  MainForm.cs        window, tray and WebView2 bridge (JSON messages to/from the dashboard)
  ui/index.html      the dashboard (embedded; opens standalone in a browser with sample data)
  AppConfig.cs       settings, start at sign-in (Task Scheduler), log
  RyzenSMU.bin       signed PawnIO RyzenSMU module (third-party, see below)
docs/
  PROTOCOL.md        everything recovered from G-Helper, with REA evidence IDs and hardware test results
```

## Disclaimer

Undervolting and changing temperature limits can make a system unstable. Use at your own risk; this project is not
affiliated with ASUS, AMD or the G-Helper project.

## License and credits

D-Helper is licensed under the **GNU GPL v3.0** (see [LICENSE](LICENSE)).

* The hardware protocol was learned by statically analysing [G-Helper](https://github.com/seerge/g-helper)
  by seerge (GPL-3.0). D-Helper is a separate, much smaller implementation written from those findings.
* `src/RyzenSMU.bin` is the RyzenSMU module of the [PawnIO](https://pawnio.eu/) project
  ([PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)), redistributed unmodified as bundled in G-Helper 0.291.
  It remains under its own license.
