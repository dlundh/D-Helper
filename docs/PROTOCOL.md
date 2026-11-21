# What REA recovered from G-Helper

Target: `GHelper.exe` 0.291 (the build running on the test laptop)
SHA-256 `4014190c083650f6ef29faabe92ed3f5740da64c3e9e6fa7f41e0e59d35c8dd8`. It is a .NET single-file bundle.
REA extracted 45 members. Main assembly: `GHelper.dll` v0.291.0.0, SHA-256 `40c2f717…3653b`,
MVID `55624d7f-fbf2-4f8d-bc8f-9bbb31d54563`, 665 types and 5,108 methods.

All findings are static. REA read metadata, constants and CIL; it did not run G-Helper.
The full bundle is in `rea-evidence-ghelper-0.291.json`.

## 1. ATKACPI transport (type `AsusACPI`)

Evidence: `ev_cd5cdf66…` (members of `AsusACPI`); method bodies in `ev_b1dfa700…` and `ev_16f4dae5…`.

| Item | Value (observed) |
|---|---|
| Device | `\\.\ATKACPI`, opened with access `0xC0000000`, share `3`, `OPEN_EXISTING`, flags `0x80` |
| IOCTL | `0x0022240C` (`CONTROL_CODE`) |
| `CallMethod(id, args)` | input = `u32 id` + `u32 args.Length` + args; output buffer = 16 bytes |
| `DeviceSet(dev, int)` | method `DEVS` (`0x53564544`), args = `u32 dev` + `i32 value`; result `out[0..3]`, `1` = OK |
| `DeviceGet(dev)` | method `DSTS` (`0x53545344`), args = `u32 dev` + `u32 0`; result `out[0..3] − 0x10000` |
| `IsSupported(dev)` | `DeviceGet(dev) >= 0`, cached |
| `GetFan(fan)` | `DeviceGet & 0xFFFF`; `>120`, or `0` with a negative raw value, means invalid. Value is hundreds of RPM |
| `SetGPUEco(on)` | reads the current state first and only writes when it differs |
| `TUFKeyboardBrightness(l)` | `DeviceSet(0x00050021, 0x80 | (l & 0x7F))` |

Device IDs used by D-Helper:

| Name in G-Helper | ID |
|---|---|
| PerformanceMode | `0x00120075` (0 Balanced, 1 Turbo, 2 Silent) |
| GPUEcoROG / GPUEcoVivo | `0x00090020` / `0x00090120` |
| GPUMuxROG / GPUMuxVivo | `0x00090016` / `0x00090026` |
| BatteryLimit | `0x00120057` |
| Temp_CPU / Temp_GPU | `0x00120094` / `0x00120097` |
| CPU_Fan / GPU_Fan / Mid_Fan | `0x00110013` / `0x00110014` / `0x00110031` |
| TUF_KB_BRIGHTNESS | `0x00050021` |

Many more IDs are listed in the evidence (fan curves, PPT power limits, panel overdrive, MiniLED and others). D-Helper doesn't use them yet.

## 2. Keyboard backlight over USB HID (types `GHelper.USB.AsusHid`, `GHelper.USB.Aura`)

Evidence: `ev_d8fa7567…`. Static arrays were read from the FieldRVA blobs with `System.Reflection.Metadata`, without loading the assembly.

* Vendor `0x0B05`, `INPUT_ID = 0x5A`, `AURA_ID = 0x5D`.
* `MAIN_AURA_PIDS` (25): 1A30 1854 1869 1866 19B6 1822 1837 1854 184A 183D 8502 1807 17E0 1ABE 1B4C 1B6E 1B2C 8854 1CE7 1CE6 1BF2 1BF3 1CD7 1CD8 4B42. `REAR_LIGHT_PIDS`: 18C6.
* `Aura.ApplyBrightness` → `DirectBrightness(level)`:
  * ACPI models (`isACPI`): `TUFKeyboardBrightness`.
  * Otherwise, the packet `5A BA C5 C4 <level>` goes through `AsusHid.WriteInput`, which pads it to the device's maximum feature-report length and calls `SetFeature` on every device with report ID 0x5A.
  * Packet template for the `5D` (Aura ID) variant: `5D BA C5 C4 <level>`.

## 2b. Lighting effect and light bar (Aura, report ID 0x5D, HID *output* reports)

Recovered from `Aura.AuraMessage`, `ApplyAura`, `AuraPowerMessage`, `ApplyPower` and `AsusHid.Write`.
The IL was printed with a static System.Reflection.Metadata reader. Enum values come from REA evidence `ev_a73050d8…`.

`AsusHid.Write` opens every device with report ID 0x5D among the Aura PIDs and writes each message as an output report, in order:

1. Effect (17 bytes): `5D B3 00 <mode> R G B <speed> 00 <x> R2 G2 B2 00 00 00 00`
   * mode: Static 0, Breathe 1, ColorCycle 2, Rainbow 3, Strobe 10. Values 4–12 and 20–27 are per-key or software modes.
   * speed: Slow `E1`, Normal `EB`, Fast `F5`.
   * `<x>`: `FF` if colour 1 is black; otherwise `1` for Breathe and `0` for everything else.
2. SET: `5D B5 00 00 00`
3. APPLY: `5D B4`

Power zones (8 bytes): `5D BD 01 <kb> <bar> <lid> <rear> FF`

| byte | bits |
|---|---|
| kb | BootLogo 01, BootKeyb 02, AwakeLogo 04, AwakeKeyb 08, SleepLogo 10, SleepKeyb 20, ShutdownLogo 40, ShutdownKeyb 80 |
| bar | AwakeBar 01 and 04, BootBar 02, SleepBar 08, ShutdownBar 10 |
| lid / rear | Boot 01+10, Awake 02+20, Sleep 04+40, Shutdown 08+80 (G-Helper fills rear from the lid settings) |

G-Helper settings on this laptop before testing: Static, `#BB0000`, all zones on, light bar on only while awake on AC.

## 2c. Ryzen SMU via PawnIO (types `PawnIO.PawnIOWrapper`, `PawnIO.RyzenSmuService`)

* Device `\\?\GLOBALROOT\Device\PawnIO`. Load-module IOCTL `0xA1B2C084` (`-1582161788`); execute IOCTL `0xA1B2C104` (`-1582161660`).
  Execute input = 32-byte ASCII function name + u64 args; output = u64 values. Requires administrator.
* Module: `GHelper.RyzenSMU.bin`, a 38,196-byte embedded resource extracted to `src/RyzenSMU.bin`.
  Functions used: `ioctl_get_code_name`, `ioctl_resolve_pm_table`, `ioctl_update_pm_table`, `ioctl_read_pm_table`,
  `ioctl_read_smu_register`, `ioctl_write_smu_register`.
* Mailbox: wait for rsp != 0, write rsp = 0, write args[0..5], write cmd, wait for rsp != 0 (200 ms); `1` = OK.
* Cezanne (code name 13 → family 3): MP1 `0x3B10528/0x3B10564/0x3B10998`, PSMU `0x3B10A20/0x3B10A80/0x3B10A88`.
  * Curve Optimizer all-core: MP1 `0x55`, arg `0x100000 + offset`
  * Tctl limit: MP1 `0x19`
  * STAPM `0x14` + PSMU `0x31`; fast `0x15` + `0x32`; slow `0x16` + `0x33` + `0x34` (mW)
* PM table `0x400005` (observed on this laptop): [0/1] STAPM limit/value, [2/3] fast, [4/5] slow, [16/17] Tctl limit/value.
* Fan curves: ACPI `0x00110024` (CPU) / `0x00110025` (GPU), 16 bytes = 8 temperatures + 8 duty %, sent unchanged.

## 3. Live read-only probe on this laptop (ROG Strix G513QY)

These reads used only `DSTS`; nothing was written.

| Device | Raw `DeviceGet` | Meaning |
|---|---|---|
| PerformanceMode | 3 | present |
| GPU Eco / MUX | −65536 | not present, so no Eco/MUX control through ACPI on this model |
| BatteryLimit | 524288 | present |
| KB brightness (ACPI) | −65536 | not present, so the keyboard is controlled over HID |
| Temp CPU / GPU | 88 / −65536 | CPU 88 °C; no GPU sensor |
| Fan CPU / GPU / Mid | 41 / 45 / −65536 | 4100 / 4500 RPM; no mid fan |

ASUS HID devices present: `VID_0B05&PID_1866` (COL01, 04, 05, 06, 07).

## 4. Write tests on this laptop (2026-11-20, G-Helper closed)

| Control | Result |
|---|---|
| Silent (`0x00120075` = 2) | accepted; over 30 s the fans fell from 4100/4500 to 2700/3000 RPM and the CPU from 88 to 76 °C |
| Turbo (`0x00120075` = 1) | accepted; over 20 s the fans rose from 2700/3000 to 4200/4500 RPM |
| Balanced (`0x00120075` = 0) | accepted (restored) |
| Battery limit 80 → 60 | accepted by the firmware (`DEVS` result 1) |
| Keyboard High → Off → Low over HID | accepted by 1 HID collection each time |
| Aura Blue → Rainbow → bar off → bar on → Static #BB0000 (2026-11-21) | every message written to 1 HID collection; the user saw every change on the light bar |

| SMU telemetry, read only | Cezanne detected; STAPM 186 W / slow 155 W / fast 186 W limits; ~50 W draw; Tctl 67–77/93 °C |
| Game-mode controller (harness as "game", offset −10) | Turbo + Tctl 93 at detection; full load hit 93–95 °C in 2 s → fan boost; CO −10 ON after 4 s of load; removed 2 s after load dropped; Balanced and CO 0 restored on exit; no marker left behind |

## Open items

* The keyboard HID writes were accepted. The visible change was not observed by the tester (needs a human looking at the keys).
* The battery limit write was accepted. The actual charging stop at the limit has not been observed.
* `PerformanceMode` read returns `3`. That looks like a presence/status value, not the current mode, so D-Helper keeps the
  mode in its own config, as G-Helper does.
* Whether G-Helper also changes the Windows power mode when it switches performance mode (`GHelper.Mode.PowerNative`) was not analysed.
