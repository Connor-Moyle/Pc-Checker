# Features

A page-by-page tour of what PC Checker does. Pages marked **Advanced** are
hidden until you turn on *Advanced mode* in Settings.

## Dashboard

The first thing you see. A health score out of 100, the key temperatures
(CPU, GPU, hottest drive), active alerts, and quick actions: run a full scan,
clean up junk, check for driver updates, export a report.

## Hardware

Everything connected to the PC with every property Windows knows about:
system and BIOS, motherboard, CPU, graphics, memory modules, physical disks
with their partitions, logical drives, network adapters, monitors, audio, USB,
battery, input devices and every Plug and Play device. Each item shows typed
fields plus the raw property bag so nothing is hidden. Search and export.

## Health

- **Disk SMART**: the drive's own self-check: reallocated and pending sectors,
  wear level, power-on hours, temperature, with plain-English verdicts.
- **Battery wear**: design versus full-charge capacity, cycle count.
- **Device problems**: devices Windows has flagged, with the problem code
  explained and what to try.
- **System errors**: recent errors and critical events from the event logs.
- **Reliability history** and **crash dumps** (blue screens).
- **Memory diagnostics** results.
- A combined health score with the reasons behind it.

## Temperatures

Live readings for every sensor the hardware exposes: CPU, GPU, VRM and chipset,
drives, memory, PSU and AIO coolers where supported. Minimum, maximum and
average over the session, sparklines, configurable warning and critical
thresholds, and alerts when a part runs hot.

## Power

Per-component power draw where sensors exist, battery charge and discharge
rate, an estimated total (clearly labelled as an estimate when there is no PSU
sensor), energy used this session and what it costs at your electricity price.
Switch power plans from here.

## Drivers

Every installed driver with version, date, signature, provider and problem
code. Flags drivers that are unsigned, very old or have problems. Checks the
Windows Update driver catalog for newer versions and can download and install
them after you confirm. Backs up all current drivers to a folder. Links to the
right vendor download page for your GPU, CPU, motherboard and system maker.

## Software

Everything installed, from the 64-bit and 32-bit registry, per-user installs
and Store apps. Shows where each program lives, how big it is and when it was
installed. Detects programs that were **installed alongside** something else
(toolbars, bundled browsers, "security" add-ons) and groups runtime
dependencies separately so they are not mistaken for bloat. Uninstall anything,
quietly or with the vendor's own wizard, including Store apps. Critical
components (Edge, WebView2, Defender, runtimes) are protected.

## Performance

A slowdown scan that looks at startup items, services, hungry processes,
memory and disk pressure, the active power plan, fast startup, visual effects,
boot time, HDD versus SSD and TRIM, pending updates and thermal throttling.
Findings are prioritised and each one links to the action that fixes it.

## Optimize

One-click, reversible tweaks grouped by risk:

- **Safe**: power plan, startup items, visual effects, Game Mode, background
  apps, Storage Sense, transparency, animations, TRIM, drive optimisation.
- **Moderate**: service start modes, hibernation, Game Bar.
- **Advanced**: DNS, telemetry level, SysMain, search indexing, `DISM`
  repair, `sfc /scannow`, network reset.

Every change is confirmed first, creates a restore point (per your settings)
and is recorded in the Action Journal with how to undo it.

## Cleanup

A junk-file scanner by category: temp folders, Windows Update cache, delivery
optimisation, prefetch, thumbnails, icon cache, recycle bin, browser caches
(Chrome, Edge, Firefox), crash dumps, error reports, shader caches, old
Windows installations and more. Dry-run first, see exactly which files are
affected, then clean. Strict safety rules: see [SAFETY.md](SAFETY.md).

## Windows Features

Enable or disable optional features and capabilities, and remove the inbox
apps you do not use. Each app has a risk label and a note on what removing it
affects (for example, removing the Xbox app can break Game Bar; the Store is
marked *do not remove*).

## Overclock (Advanced)

- **GPU** (NVIDIA): core and memory clock offsets, power limit, temperature
  limit, fan curve where supported. AMD and Intel GPUs are read-only with a
  link to the vendor's tuning tool.
- **CPU**: through Intel XTU or AMD Ryzen Master command-line tools when they
  are installed; otherwise guidance for BIOS settings. XMP/EXPO detection.
- Profiles, a built-in stress test with a temperature watchdog, automatic
  revert after a crash, and a safety validator that clamps every value to the
  limits you set. A consent gate explains the risks before anything is enabled.
- **Claude overclock advisor**: produces a staged plan you apply one step at a
  time. See [CLAUDE-INTEGRATION.md](CLAUDE-INTEGRATION.md).

## Overlay

A customisable in-game overlay: pick which metrics to show (temperatures,
loads, clocks, power, memory, network, battery, time), set position, opacity,
scale, fonts and colours, save presets and toggle it with a hotkey. It is
click-through and stays on top. See [OVERLAY.md](OVERLAY.md).

## Logs

A live log viewer with per-category levels, filtering and export, plus the
Action Journal: every change the app made, when, and how to undo it.
See [LOGGING.md](LOGGING.md).

## Ask Claude

Chat about this PC with the scan results as context ("why is my boot slow?",
"is this drive failing?"). Optional, off until you add an API key.
See [CLAUDE-INTEGRATION.md](CLAUDE-INTEGRATION.md).

## Settings

Theme (system, light, dark), advanced mode, startup behaviour, electricity
price, update channel, restore point policy, temperature thresholds, overlay,
logging, cleanup categories, overclock limits and the Claude connection.

## Updates

The app checks GitHub Releases for new versions and can download and apply
them in place. Choose the Stable or Beta channel in Settings.

## Reports

Export a full report of hardware, health, performance, software, drivers and
sensors as HTML, JSON or Markdown to share with a technician or keep for
later.
