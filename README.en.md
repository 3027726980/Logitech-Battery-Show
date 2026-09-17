# GPW2 Battery Show

[中文](README.md) | **English**

Real-time battery level of the Logitech G Pro X Superlight 2 (GPW2) mouse in the
Windows 11 system tray. Reads the device directly via the proprietary HID++
protocol — **no Logitech G HUB / Logi Options+ required**.

Implemented in C# + HidSharp + WinForms; the main executable is only ~29KB.

## Features

- Always-on tray icon with live battery percentage (numeric / classic-battery styles, light/dark theme aware)
- Left-click the tray icon: battery detail card (level, connection mode, charging state, progress bar)
- Right-click menu: force refresh (full re-probe), switch style, launch at startup, control panel, exit
- Charging detection (green): works both with the USB-C cable plugged into the PC **and a standalone charger over LIGHTSPEED**
- Three-level coloring — low (red) / mid (yellow) / normal (white); system notification below the threshold (fires once)
- Automatic detection between wired USB-C and LIGHTSPEED receiver; plug/unplug reacts within seconds (hot-plug event driven)
- Sleep / disconnected state shown in grey, with automatic re-probe on recovery

## Usage

1. Run `dist/GPW2BatteryShow.exe` (keep `HidSharp.dll` in the same folder)
2. Left-click the tray icon for details; right-click for the control panel

## Build

Requires Windows only (the bundled .NET Framework 4.8 `csc.exe` — **no SDK needed**):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output: `dist/GPW2BatteryShow.exe` + `HidSharp.dll`.

## Configuration

`%LOCALAPPDATA%\GPW2BatteryShow\config.json`

| Key | Default | Description |
|---|---|---|
| poll_interval_sec | 60 | Polling interval in seconds; too small interferes with mouse power saving |
| low_battery_threshold | 20 | Low-battery notification threshold (%), also drives the red color level |
| icon_style | numeric | Tray icon style: numeric / simple |
| popup_on_click | true | Whether left-clicking the tray icon opens the battery card |

## Project Layout

```
src/
├── Program.cs            # Entry: single-instance mutex, unhandled exception hook
├── TrayContext.cs        # Tray context: menu / polling / hot-plug events / notifications
├── BatteryPopup.cs       # Left-click battery detail card
├── ControlPanel.cs       # Control panel
├── LogitechHidpp.cs      # Pure HID++ 2.0 protocol layer (frame build / response parse)
├── Gpw2Device.cs         # Device discovery & battery query (HidSharp multi-collection I/O)
├── TrayIconRenderer.cs   # GDI+ dynamic tray icon rendering
├── AppSettings.cs        # Config persistence + autostart registry
└── Logger.cs             # Rolling log
tools/LogiDiag.cs         # On-device protocol diagnostic tool (compiled separately)
lib/HidSharp.dll          # HidSharp 2.x (Apache 2.0)
```

## Implementation Notes (verified on real hardware)

Logitech devices speak a proprietary HID++ protocol over standard USB HID reports
(short `0x10` / long `0x11`). Key behaviors confirmed during on-device debugging:

1. **Battery reading**: the GPW2 exposes the UnifiedBattery feature (ID `0x1004`).
   `func 0` returns capabilities (NOT the battery level!), while **`func 1` returns
   the SOC percentage, charging state and external-power flag** — reading the wrong
   function silently yields garbage values
2. **Multi-collection**: the receiver (PID 0xC547) MI_02 interface exposes Col01/Col02
   collections; GPW2 (protocol 4.2) long-report responses **only appear in Col02's
   read queue**, while writes go through Col01
3. **Negative replies**: requests to the receiver itself or empty slots are answered
   with a HID++ 1.0 error frame (`0x8F`) instead of timing out, which enables fast
   offline detection
4. **Wired-mode quirk**: with the USB-C cable plugged into a PC the mouse switches to
   a direct device (PID 0xC09B) that answers on `0xFF` **and all slots 1-6**;
   probing must prefer the direct connection
5. **Firmware latency**: battery query responses arrive after a fixed ~800ms
   (identical on wired and wireless) — a firmware trait that cannot be optimized
   away client-side; the software instead merges the probe and reading phases and
   caches the feature index, keeping a full re-probe at ~0.9s

## Known Limitations

- Non-charging colors are derived from a generic Li-ion voltage curve and may differ
  from G HUB by ±5% (G HUB uses a device-specific lookup table)
- Concurrent access with G HUB may occasionally cause query timeouts (handled internally)
- Bluetooth is not supported (the GPW2 has none)

## Diagnostics

If detection misbehaves, run `dist/LogiDiag.exe` (source in `tools/LogiDiag.cs`) and
share the output; the application log lives at
`%LOCALAPPDATA%\GPW2BatteryShow\log.txt`.

## License

This project's code is released under the [MIT License](LICENSE). The third-party
library HidSharp remains under its own Apache 2.0 license (see the license files in `lib/`).

## Credits

- [HidSharp](https://github.com/SeekHisKingdom/HIDSharp) (Apache 2.0) — cross-platform .NET HID library
- [device-battery-tray](https://github.com/linzifeng07/device-battery-tray) — tray UI / control panel design reference
- [OpenLogi](https://github.com/AprilNEA/OpenLogi) — UnifiedBattery function semantics and feature priority reference
- [Solaar](https://github.com/pwr-Solaar/Solaar)、[LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery) — HID++ protocol implementation reference
