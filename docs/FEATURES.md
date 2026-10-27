# Features

A page-by-page tour of what PC Checker does, what every button does, and
where each page stops. Sections marked **Advanced** only appear when
*Advanced mode* is on (Settings → General, or the toggle at the bottom of the
navigation pane).

The side menu has one item per section. A section with several pages shows
them as tabs above the content:

```
Home            Dashboard
Monitor         Temperatures · Power
Hardware        Overview · Health · Storage · Memory
Devices         Displays · Audio · Keyboard & Mouse
Cooling & RGB   Fans · Lighting
Apps & Drivers  Apps · Drivers · Startup · Windows Features
Clean up        Cleanup · Debloat · Registry
Performance     Speed check · Optimize · Overclock
Gaming          Overlay · Latency
Network         Network
Ask Claude      Ask Claude
Footer          Logs · Settings
```

Every page that changes something follows the same pattern: a confirmation
dialog that explains the change, a restore point (per Settings), an entry in
the Action Journal with an undo hint, and a log line. See
[SAFETY.md](SAFETY.md).

---

## Overview

### Dashboard

The first thing you see.

- **Health score** gauge from the last health check, or a *Run a health check*
  button if there is none yet.
- **Live tiles**: CPU temperature, GPU temperature, memory used, free disk
  space and current power draw, straight from the sensor service.
- **Alerts strip**: temperature alerts, critical health items, a pending
  reboot, an update that is ready, a fan or overclock watchdog notice from the
  last start.
- **Quick actions**: *Full scan* (runs Hardware → Health → Performance →
  Software → Drivers with progress), *Clean temp files*, *Check drivers*,
  *Optimize* (each opens its page) and *Export full report* (HTML, JSON or
  Markdown through a save dialog).
- **Controls row**: the active fan profile, lighting connection status, how
  many displays and the active mode, and the default audio device. Each tile
  tolerates an unavailable service and says so.
- **Hardware support**: your GPU and CPU vendor and what the app can control
  on them, from the Overclock page's detection.
- **Recent activity**: the last five Action Journal entries.
- **System summary**: CPU, GPU, RAM, storage, OS build and uptime.

---

## Monitoring

### Temperatures

Live readings from every sensor LibreHardwareMonitor can see.

- **Gauges** for CPU, GPU, the hottest drive and the motherboard or VRM,
  coloured by your warning and critical thresholds.
- One expander per piece of hardware (CPU, GPU, each drive, motherboard,
  memory, PSU or cooler) listing each temperature sensor with its current,
  minimum, maximum and average for the session and a sparkline.
- **Pause / Resume**, **Reset min/max**, and **Thresholds**, an inline editor
  for the warning and critical temperatures per component (the same values
  are in Settings → Monitoring).
- **Alerts**: the last twenty threshold crossings. Alerts re-fire only after
  the temperature drops 3 °C below the threshold or five minutes pass.
- **Advanced**: an *All sensors* table with every sensor kind: loads, clocks,
  fan speeds, voltages, power, data counters.

Limitations: the sensor driver needs administrator rights, CPU and motherboard
sensors need the PawnIO driver installed, and another monitoring tool can hold
the hardware; the
page shows the exact reason and a *Retry* button. Laptops and OEM desktops
often expose very few sensors, and virtual machines almost none.

### Power

- **Tiles**: total draw now, CPU package, GPU, battery discharge, energy used
  this session (Wh), estimated cost per hour and per day at the price in
  Settings.
- A 30-minute sparkline of total watts and a per-component table.
- **Battery**: charge, on-battery state, discharge rate and an estimated time
  remaining.
- **Power plan**: pick a plan and *Apply* (confirmed, journaled, reversible).
- **Electricity price** editor.

Limitations: the total is the sum of known sensors plus a baseline when there
is no PSU sensor; the page labels it *Estimated* in that case. A PC with no
CPU or GPU power sensors shows dashes.

### Overlay

Configures the in-game overlay; the overlay itself is a separate click-through
window. Full detail in [OVERLAY.md](OVERLAY.md).

- **Enable** toggle, **Show / Hide now**, **Edit position on screen** (makes
  the overlay draggable; drop saves a custom position).
- **Hotkey** (default `Ctrl+Shift+O`, validated as you type).
- **Corner** or custom X/Y, **opacity**, **scale**, font family and size,
  **orientation** (vertical list or horizontal row), background on/off and
  colours (background, text, accent), **sparklines**, **update interval**.
- **Metrics** list with checkboxes, reorder buttons and editable labels.
- **Presets**: save, load and delete; built-ins *Minimal*, *Full* and *Temps
  only*.
- A live preview on a sample "game" background, naming the game the FPS
  metrics are measured for.
- **FPS counter**: metrics *FPS*, *Frame time* and *1% low* for the game in
  front, counted from the Windows graphics present events like PresentMon
  (needs administrator rights, which the app has). The card explains why it is
  unavailable when Windows refuses the tracing session.
- **Game profiles**: per game (matched by its .exe name) an overlay preset and
  visibility, a fan profile and a power plan. Applied after the game has been
  in front for 2 seconds, put back when the game's process exits. Add from the
  running programs or by browsing for the .exe; edit, remove, switch on/off;
  global *Switch automatically* and *Only when the game fills the screen*; a
  status line shows the active profile. Switches are journaled (module
  *Gaming*).

Limitations: the overlay is a transparent desktop window. It shows over
borderless and windowed games; exclusive full-screen games hide every desktop
window. Some anti-cheat systems block overlays and can hide a game's frames
from the FPS counter (it then shows "--").

---

## Hardware

### Hardware

A complete inventory of the PC, scanned automatically on first visit.

- **Categories** on the left with counts: System, Processor, Graphics,
  Memory, Storage, Network, Displays, Audio, USB, Battery, Input and All
  devices.
- Each item is a card with the typed, human-labelled fields first ("Cores: 8
  (16 threads)", "VRAM: 12 GB", "Secure Boot: on", "TPM 2.0") and an *All
  properties (raw)* list underneath with a search box, so nothing Windows
  knows is hidden.
- **Scan**, **Export JSON** (save dialog) and **Copy summary**.
- A summary strip: CPU, GPU, RAM total and slots, storage total, OS build.
- A warning bar if a WMI class could not be read; the rest still loads.

### Health

Runs each analyzer independently and combines the result into one score
(start at 100; Critical −25, Warning −8).

- **Issues found**: every item that is not *Good*, ordered by severity, with a
  recommendation and an *Explain with Claude* button when the assistant is
  configured.
- **Drives**: SMART status, temperature, wear, power-on hours, reallocated and
  pending sectors, host writes, with a plain-English verdict per drive.
  **Advanced** shows the full SMART attribute table.
- **Battery**: design versus full-charge capacity, wear, cycle count.
- **Device problems**: devices Windows has flagged, the problem code
  explained, what to try, and *Open Device Manager*.
- **Recent system errors**: Critical and Error events from the System and
  Application logs for the last seven days, with a filter and the top error
  sources.
- **Reliability history** (last 30 days) and **crash dumps** (minidumps and
  `MEMORY.DMP`, flagged when a blue screen happened in the last 30 days).
- **Memory diagnostic**: the last Windows Memory Diagnostic result.
- **Windows Update**: last successful install and pending-reboot flags.
- **Free space** warnings (under 10 % warning, under 5 % critical).
- **Thermal** items from the live sensors, and **security basics**
  (antivirus state, firewall profiles, BitLocker where available).
- **Deep tools** card linking to the Storage page (surface scan, `chkdsk`,
  benchmark) and the Memory page (memory test).

### Storage

Tools for every physical disk. Each device card shows model, size, media and
bus type, health badge, volumes, a *system disk* badge and a link to the
vendor's own tool (Samsung Magician, Western Digital Dashboard, SeaTools,
Crucial Storage Executive, Kingston SSD Manager, Solidigm Storage Tool, SK
hynix Drive Manager, KIOXIA SSD Utility, Corsair SSD Toolbox, ADATA SSD
ToolBox).

- **Surface scan** (read-only): *Quick* samples the first and last 2 % of the
  disk plus 200 random blocks; *Full* reads every block. Choose the block
  size, then *Start* or *Cancel*. A live block map colours each block OK,
  Slow or Bad; the page shows MB/s, bad and slow counts, elapsed time and ETA,
  and ends with a summary and what to do if bad blocks were found (back up
  now, plan a replacement). The scan never writes to the disk.
- **Benchmark**: pick a volume and a test size (256 MB, 1 GB or 4 GB). The
  app writes a temporary file with unbuffered, write-through I/O, then
  measures sequential read and write and random 4K read and write (MB/s and
  IOPS), deletes the file and compares your numbers with typical SATA SSD,
  NVMe and HDD figures. Needs free space of the test size plus 10 %.
- **Check disk**: *Scan now* runs `chkdsk X: /scan` online and streams the
  output; *Schedule full repair* runs `chkdsk /f /r` at the next restart after
  a confirmation that explains the restart.
- **Repair volume**: `Repair-Volume` in Scan, SpotFix or OfflineScanAndFix
  mode.
- **Optimize**: `Optimize-Volume` with re-trim for SSDs or defragment for
  HDDs, output streamed.
- **Disk error events**: the last 30 days of disk, NTFS, storahci, stornvme
  and volmgr events with each common event id explained.
- **Open Disk Management**.

Limitations: a full scan of a large HDD takes hours; the page says so and
suggests *Quick* first. Everything that writes (check disk, repair, optimize)
is confirmed first.

### Memory

- **Overview**: total, used and available with a gauge; commit charge and
  limit; cached and standby bytes; memory compression and SysMain state; page
  files; the module table (slot, size, rated and configured speed,
  manufacturer, part number); an **XMP/EXPO** card that says, for example,
  "Your RAM is rated for 6000 MT/s but runs at 4800. Enable XMP or EXPO in
  BIOS" with the steps; a dual-channel note; and the last Windows Memory
  Diagnostic result.
- **Warnings**: a single module, mixed sizes, XMP off, page file disabled,
  low available memory.
- **Run quick memory test**: pick what percentage of free memory to test and
  how many passes. The app fills that memory with patterns (0x55…, 0xAA…,
  walking ones, seeded random) across all cores, verifies each pass and lists
  any mismatches with advice. This is a user-mode test of memory that is
  currently free; it is not a replacement for a boot-time test.
- **Schedule Windows Memory Diagnostic**: sets `bcdedit /bootsequence
  {memdiag}` after confirmation and offers to restart. Undo hint provided.
- **Clear standby list**: purges the standby cache (useful before a benchmark
  or when a game stutters; Windows refills it over time).
- **Profiles**: *Balanced* (Windows defaults), *Gaming* (compression and
  SysMain on, fixed page file of 1.5× RAM, at least 8 GB, on the fastest SSD),
  *Low-memory PC* (2× RAM page file) and, in **Advanced**, *Workstation*
  (compression off, 1× RAM page file). Each explains why and may need a
  restart.
- **Advanced**: a manual page-file editor per drive.

### Displays

- **Arrangement preview**: rectangles proportional to each display's
  position, with numbers and a star on the primary; **Identify** flashes a
  big number on every monitor for three seconds.
- Per display: name, connection (HDMI, DisplayPort, internal and so on),
  size, current mode, scale, HDR state with a toggle when supported,
  **orientation**, **resolution** (grouped by aspect ratio) and **refresh
  rate** (filtered by resolution), *Apply* with a 15-second *Keep these
  settings?* countdown that reverts automatically, **Set as primary**, and
  **brightness** and **contrast** sliders over DDC/CI on external monitors or
  WMI on laptop panels (hidden with a tooltip when the monitor does not
  answer).
- **Profiles**: save the current layout, apply, delete.
- Shortcuts: Windows display settings, Night light, advanced graphics
  settings, colour calibration (`dccw.exe`) and your GPU control panel
  (NVIDIA app or Control Panel, AMD Software, Intel Graphics Software).

Limitations: variable refresh rate is controlled in Windows Graphics settings;
the page links there. Many monitors ignore or answer DDC/CI slowly; the page
says when it gave up.

### Audio

- **Output devices** and **Input devices** as cards: name, interface, default
  and communications badges, state, format, a volume slider with mute, a live
  level meter, *Set as default*, *Set as default for calls*, and *Properties*
  (opens the Sound control panel).
- **Per-app volume mixer**: every audio session with its own slider and mute.
- **Quick tools**: *Restart audio service* (confirmed), *Run troubleshooter*,
  *Open sound settings*, *Open volume mixer*, *Open control panel*.

Limitations: exclusive mode, enhancements and spatial sound are per-device
properties in Windows; the page links to them rather than duplicating them.

### Keyboard & Mouse

- **Mouse**: pointer speed (1–20, shown as the familiar "6/11"), *Enhance
  pointer precision* with an explanation of acceleration and why gamers turn
  it off, double-click speed with a test target, swap buttons, scroll lines or
  one screen, pointer trails, hide pointer while typing, snap to default
  button. *Apply* and *Revert*.
- **Keyboard**: repeat delay and rate with a test box, cursor blink rate,
  Sticky Keys, Filter Keys, Toggle Keys.
- **Polling rate**: *Measure* for five seconds using raw input and shows the
  live and final Hz with an interpretation (125, 250, 500, 1000, 4000, 8000 Hz
  and what each is good for).
- **Profiles**: *Windows default*, *Gaming*, *Precision work*,
  *Accessibility*, each applied with one click after confirmation.
- **Detected devices** with links to the vendor's software (Logitech G HUB,
  Razer Synapse, Corsair iCUE, SteelSeries GG, Glorious Core, ASUS Armoury
  Crate, HyperX NGENUITY, Microsoft Mouse and Keyboard Center, Wootility,
  Keychron Launcher) and shortcuts to the Windows mouse, typing and
  control-panel pages.
- RGB for keyboards and mice lives on the **Lighting** page; this page links
  to it.

Limitations: DPI, button mapping and onboard profiles live in the vendor's
software; the app cannot change them.

### Fans

Finds every fan that LibreHardwareMonitor exposes a control for: motherboard
headers on supported Super I/O chips, NVIDIA and AMD GPU fans, and some
coolers and controllers.

- An info bar lists what can be controlled on this PC and what cannot (for
  example "GPU fans: via NVIDIA driver; case fans: Nuvoton NCT6798D on
  motherboard"), or says plainly that no controllable fans were found.
- **Fan cards**: name, RPM, percent, mode badge and a mode selector: *Auto*,
  *Manual* (slider, clamped to the minimum in Settings unless you explicitly
  allow lower), or *Curve*.
- **Curve editor**: a 0–100 °C by 0–100 % chart with draggable points,
  double-click to add or remove, a temperature source picker, hysteresis and
  ramp-step boxes, and a live marker showing the current temperature and the
  speed the curve produces.
- **Profiles**: *Silent*, *Balanced* and *Performance* built in, plus your own
  saved to Settings. *Apply*, *Save as…*, *Revert all to auto*, and the
  *Revert to automatic on exit* toggle.
- **Failsafe**: if any temperature crosses its critical threshold while a
  manual or curve profile is active and the fan is below 100 %, the app
  forces 100 % and raises an alert.
- **Watchdog**: while software control is active the app keeps a session
  file; if it finds one on the next start (a crash), it reverts graphics card
  fans to automatic and tells you on the Dashboard. Motherboard fans keep the
  speed they were left at until the PC restarts, so the app holds them on the
  Balanced curve with its overheating protection on until you restart.
- **Advanced**: the raw control list with identifiers, ranges and current
  software values.

Limitations: laptops and most OEM desktops do not expose fan controls to
software; AIO pumps and RGB controllers that only speak a vendor protocol are
not controllable. See [SUPPORTED-HARDWARE.md](SUPPORTED-HARDWARE.md).

### Lighting

RGB control through [OpenRGB](https://openrgb.org), one open-source service
that speaks to hundreds of motherboards, RAM kits, GPUs, keyboards, mice,
coolers and strips.

- **Status card**: connection state and OpenRGB version, *Connect* /
  *Reconnect*, *Launch OpenRGB server* (starts OpenRGB with the SDK server),
  *Download OpenRGB*, a *Why OpenRGB?* note and the vendor suites as
  alternatives (ASUS Armoury Crate, MSI Center, Gigabyte Control Center,
  ASRock Polychrome, Corsair iCUE, Razer Synapse, Logitech G HUB, SteelSeries
  GG, NZXT CAM).
- **Top controls**: *Sync all* colour picker, *Apply to all*, brightness for
  all, *Turn off all*, profile picker with *Apply*, *Save* and *Delete*.
- **Device cards**: type icon, name, vendor, LED count and zones; a mode
  picker; a colour swatch and picker; speed and brightness sliders when the
  mode supports them; a per-zone expander with zone colours. Changes apply
  immediately with a short debounce.
- **Peripherals** section groups keyboards, mice and headsets.
- **Profiles**: *Gaming (blue/cyan)*, *Work (warm white, 40 %)*, *Night
  (off)*, *Rainbow* built in, plus your own.

Limitations: the page does nothing without OpenRGB running with its SDK
server enabled (Settings → SDK Server in OpenRGB). Devices OpenRGB does not
support cannot be controlled; use the vendor's suite. Lighting changes are
cosmetic, so they are journaled but do not create restore points.

---

## Software

### Drivers

- **Scan drivers**: every installed driver with device, class, provider,
  version, date, age, signature, status and problem code. Filters for
  third-party only, problems only and old only; a search box; a row expander
  with hardware ids, `.inf` name and location.
- **Check for updates**: asks the Windows Update driver catalog through the
  Windows Update Agent and lists what is available with size and date.
  *Download selected* and *Install selected* (confirmed; restore point;
  reboot bar afterwards with *Restart now* or *Later*).
- **Back up all drivers** to a folder you pick (`pnputil /export-driver`).
- **Old drivers** (its own tab): superseded driver-store packages (same
  original INF, provider and class with a newer date or version), packages no
  connected device uses, and drivers of disconnected devices. Grouped as Safe
  to remove, Probably safe and Keep, with sizes from the driver store and the
  total space you can free. Removal exports each package to
  `%LOCALAPPDATA%\PcChecker\driver-backups\<time>` first, forgets
  disconnected devices with `pnputil /remove-device`, and deletes packages
  with `pnputil /delete-driver` (never forced).
- **Vendor links**: NVIDIA, AMD and Intel driver pages for your GPU and
  chipset, your motherboard vendor's support page, and *Windows Update
  optional updates*.
- **Advanced**: *Install .inf*, *Roll back* (where the app can map the device
  to its driver package; otherwise it opens Device Manager and explains) and
  the driver store list.
- Summary tiles: total, third-party, with problems, older than three years,
  unsigned, updates available.

Limitations: updates come only from Windows Update; the app never downloads
from third-party driver sites.

### Software

- **Scan**: programs from the 64-bit, 32-bit and per-user registry plus Store
  apps, with version, publisher, install date, size, location and source
  badge. *Calculate sizes* measures folders that did not report a size.
  *Export CSV*.
- **Installed alongside**: programs installed within minutes of another with
  a different publisher, or installed inside another program's folder, or on
  the known-bundler list, are grouped with the reason. Runtime dependencies
  (Visual C++, .NET) are grouped separately so they are not mistaken for
  bloat. *Uninstall extras* removes a bundle's children.
- **Uninstall** (confirmed): quiet with the installer's silent switch (MSI,
  Inno Setup, NSIS, InstallShield, Squirrel, Store) or normal with the
  vendor's wizard. Exit codes are translated (reboot required, cancelled, not
  installed).
- **Protected** entries (Edge, WebView2, Defender, .NET and Visual C++
  runtimes, driver packages) show a lock with the reason. **Advanced** lets
  you override for non-critical ones with a warning.

### Startup

- **Startup apps**: registry Run keys, startup folders and logon tasks with an
  enabled toggle, name, publisher, command, kind and location; search;
  *Add…*, *Remove* (confirmed; shortcuts are moved to a backup folder, not
  deleted) and *Open file location*.
- **Scheduled logon tasks** in their own grid.
- **Advanced**: *Services at startup* with a start-mode picker; critical
  Microsoft services are protected and cannot be disabled.
- **Boot options**: last boot time and duration, *Fast startup* toggle with
  the pros and cons, *Boot menu timeout*, *Safe mode next boot* (danger
  styling, confirmed, with the way back explained), a link to schedule the
  memory diagnostic, *Restart to UEFI/BIOS* and *Advanced startup* (both with
  a countdown and a cancel).
- Shortcuts: Windows Startup Apps settings, Task Scheduler, System
  Configuration.

### Windows Features

Three sections with a plain-English note on what each is:

- **Optional features** (`.NET Framework 3.5`, Hyper-V, Virtual Machine
  Platform, Windows Subsystem for Linux, Sandbox, SMB 1.0 with its risk note,
  Telnet client, legacy components, Print to PDF and so on) with state and
  risk badges and *Enable* / *Disable*.
- **Capabilities** (OpenSSH client and server, Notepad, Paint, WordPad, Steps
  Recorder, XPS Viewer, language packs, RSAT tools and so on).
- **Built-in apps**: friendly name, package name, description, a risk label
  (*Safe to remove*, *Think twice*, *Not recommended*) and *Remove*
  (confirmed; **Advanced** adds *also remove for new users*). Removing the
  Xbox app breaks Game Bar; the Store is marked *do not remove*.

Every change is confirmed, creates a restore point and shows a reboot bar
when needed.

### Debloat

Turns off ads, data collection, AI features and clutter, and removes
pre-installed apps, with the coverage of the popular debloat tools (WinUtil,
Win11Debloat, O&O ShutUp10++, Sophia Script and others; full list and table in
[DEBLOAT.md](DEBLOAT.md)).

- **Presets**: Recommended (highlighted), Gaming, Privacy max, Minimal UI,
  Remove consumer apps and Custom. A preset only ticks boxes.
- **Categories**: Privacy & telemetry, Ads & suggestions, AI & Copilot,
  Taskbar & Start, File Explorer, Edge & OneDrive, Gaming & Xbox, and
  (**Advanced**) Windows Update and Background services, plus Apps. Each row
  shows its state (*Applied*, *Not applied*, *Partly applied*, *Not
  available* with the reason), a risk badge, *One-way* / *Explorer restart* /
  *Windows 11* badges, a warning when something stops working, and which other
  tools offer it. Hover a row for the exact registry values, services and tasks.
- **Apply selected** / **Revert selected** in the bar at the bottom, or per
  row. The confirmation lists every change grouped by risk. A restore point is
  always created first and previous values are saved, so *Revert* puts back
  exactly what was there (or the Windows default, which it says).
- **Restart Explorer** is offered after taskbar, Start and context menu
  changes; a restart bar appears when Windows needs one.
- **Import / Export** the selection as JSON, including WinUtil exports.
- **Check these yourself** links to settings with no safe policy, and
  **Deliberately not offered** explains why disabling Defender or Windows
  Update and removing Edge or the Store are left out.

### Cleanup

- **Scan** by category: your temp files, Windows temp, Windows Update
  downloads, Delivery Optimization, prefetch, thumbnails, icon cache, Recycle
  Bin, Chrome, Edge and Firefox caches, crash dumps, error reports, Windows
  logs, DirectX and NVIDIA shader caches, font cache, Internet temporary
  files and (**Advanced**) the previous Windows installation.
- Each category is a card with a checkbox, a risk badge, size and file count
  after the scan, and an expander with the largest files and *Open folder*.
- **Dry run** shows exactly what would be removed. **Clean selected** then
  deletes file by file and reports what was freed, skipped (locked or in use)
  and failed.
- Shortcuts: *Open Storage settings* and *Run Windows Disk Cleanup* for
  Windows.old.

Safety rules: never outside the category roots, never through junctions or
symbolic links, never files modified in the last 60 minutes (configurable),
never `desktop.ini`, and browser caches are skipped while the browser runs.

### Registry

- **Honest intro**: registry "errors" rarely slow Windows down; the page
  removes leftovers that point at things that no longer exist, so lists stay
  tidy and some error dialogs or broken menu entries go away.
- **Categories** with a description, where they look and a risk badge:
  leftover uninstall entries, App Paths, shared file counters, file
  associations and Open with entries, startup entries, MUI cache, fonts, help
  files, services with a missing program (report only) and, in advanced
  mode, orphaned COM components, installer folder records and right-click
  menu handlers.
- **Scan** is read only and cancellable. Results are grouped per category
  with a checkbox (fixable entries only), the problem in plain English, the
  key path (copy button) and the missing file or folder.
- **Fix selected** asks first, creates a restore point, backs every entry up
  to a `.reg` file, checks each entry again and only then removes it.
- **Backups** lists every fix with *Restore*, *Delete* and *Open folder*;
  backups older than 90 days are offered for cleanup, never removed
  automatically. Details: [REGISTRY.md](REGISTRY.md).

---

## Performance

### Performance

- **Scan for slowdowns** looks at startup items, services, hungry processes,
  memory and commit pressure, page-file configuration, boot time, drive type
  and free space, TRIM, the power plan, fast startup, hibernation, visual
  effects, transparency, Game Mode, Game Bar, background apps, Storage Sense,
  SysMain, Windows Search, pending reboot, Windows Update age, uptime,
  thermal throttling and more.
- **Score** gauge and findings ordered by severity, each with the detail, the
  impact and a *Fix* button that runs the matching Optimize action (confirmed)
  or opens the right page.
- **Startup programs** with an enabled toggle, **Services** (**Advanced**)
  with a start-mode picker, **Top processes** by CPU and memory with *End
  task* (confirmed; system processes are protected), and a **System snapshot**.
- **Quick wins** card linking to Latency, Startup, Memory and Cleanup.

### Optimize

Reversible tweaks grouped by category, each row with its description, risk
badge, current state, *Apply* and *Revert*:

- **Safe**: power plan (Balanced, High performance), visual effects, Game
  Mode, Game Bar, background apps, Storage Sense, transparency, animations,
  focus notifications, TRIM, optimise drives, clean temporary files (opens
  Cleanup).
- **Moderate**: fast startup on/off, hibernation off.
- **From other pages**: the Latency fixes (HAGS, USB selective suspend, NIC
  power saving, PCIe ASPM, core parking, pointer precision, Game Mode),
  Memory (compression, SysMain, standby list, automatic page file), Startup
  (fast startup, boot timeout) and Keyboard & Mouse (pointer precision, gaming
  profile).
- **Advanced** (with a warning bar): Ultimate performance plan, Cloudflare
  DNS, minimal telemetry, SysMain off, Search indexing off, `DISM
  /RestoreHealth`, `sfc /scannow`, network reset, HPET off, dynamic tick off,
  Nagle off, MSI mode for the GPU. Long-running repairs stream their output.
- **Apply all safe recommendations** runs the safe set in sequence with
  progress.

### Latency

- **Run latency check** with a sample length (5, 10 or 30 seconds) and an
  *include network* toggle.
- **Score** and tiles: DPC time %, interrupt time %, DPCs per second,
  interrupts per second, timer resolution (current and minimum), mouse polling
  Hz and DNS lookup time.
- **Network table**: your gateway, 1.1.1.1, 8.8.8.8 and any targets you add
  in Settings, with average, minimum, maximum, jitter and loss and coloured
  badges.
- **Findings** with *Fix* buttons: HAGS, USB selective suspend, NIC power
  saving, PCIe ASPM, core parking, pointer precision, Game Mode, the power
  plan, and in **Advanced** HPET, dynamic tick, Nagle and MSI mode (each with
  an honest note on when it helps). Timer resolution is explained as
  information only because Windows 11 manages it per process.
- **Learn** expander: what DPC and ISR mean, what good values look like, and
  that real-time DPC tracing needs LatencyMon or Windows Performance
  Analyzer (linked).
- **History** of the last five runs.

Limitations: the performance counters can be corrupt on some systems; the
page shows the `lodctr /R` hint. Ping results depend on your network at that
moment.

### Network

- **Connection summary**: active adapter, IP address, router and DNS servers,
  link speed and Wi-Fi signal.
- **Wi-Fi**: network name, band and channel, a signal gauge with the dBm
  estimate, Wi-Fi standard, link rates and security, read from `netsh wlan`
  by the shape of the output so localized Windows works. Findings in plain
  English: weak or fair signal, crowded 2.4 GHz channel (with the least
  crowded of 1/6/11), the same network available on 5/6 GHz, a link rate too
  low for the standard, open/WEP/TKIP security, and Wi-Fi power saving (one
  click fix). Windows 11 24H2 needs Location permission for the scan; the
  page explains it and opens the right Settings page.
- **Nearby networks** table and **channel congestion** bars per band.
- **Speed test** (HTTP, `speed.cloudflare.com` by default): idle ping,
  download and upload over four connections, ping under load and the
  bufferbloat grade (A+ to F) with an explanation, data used, live progress
  and Cancel. Not run automatically because it uses data.
- **Packet loss test** (50, 100 or 500 pings to the router, 1.1.1.1 and
  8.8.8.8): loss, average, jitter and 95th percentile per target, plus where
  the loss starts (home network or provider).
- **Tune Wi-Fi and network**: Wi-Fi power saving, "allow the computer to
  turn off this device", TCP auto-tuning, ECN, RSS, a fast public DNS
  provider and, in Advanced mode, DNS over HTTPS and prefer IPv4. Adapter
  driver settings (preferred band, roaming aggressiveness, MIMO power save,
  transmit power, wireless mode, channel width, Energy-Efficient Ethernet,
  speed and duplex) with curated recommendations. Saved Wi-Fi networks:
  order, auto-connect, metered, forget (backed up first). Every change asks
  first, goes through the change guard and has **Revert**, also after a
  restart.
- **Quick improve Wi-Fi**: a checklist of the recommended changes that differ
  from the current state, applied one by one, then a re-test with the
  results before and after side by side.
- **Better Wi-Fi without software**: router placement, band trade-offs, the
  best channel to set on the router, firmware, mesh or Ethernet.
- **Adapters** table with errors and discards, and **fix tools**: flush DNS,
  renew IP, reset network stack (Advanced), Wi-Fi and network settings.

Limitations: the router's channel and QoS can only be changed on the router;
the nearby list is Windows' latest scan; speed results depend on the server
and the time of day. See [NETWORK.md](NETWORK.md).

### Overclock

Behind a one-time **consent gate** that explains the risks (instability,
crashes, data loss, heat, warranty), what the app can and cannot control on
this PC, and the safety features.

- **Supported hardware** matrix: feature × vendor × level (Full, Partial,
  Experimental, Monitor only, Unsupported) with a one-line reason for your
  machine, and vendor badges next to the GPU and CPU names.
- **GPU tuning** (when a method is available): core offset, memory offset,
  power limit, temperature limit and fan percent sliders with numeric boxes,
  ranges clamped by the driver's limits and your Settings. *Apply*
  (confirmed; lists the changes, the restore point and the watchdog), *Reset
  to defaults*, *Save as profile*, load profile, presets *Default*, *Mild*
  (+50/+200) and *Moderate* (+100/+500). Live clock, temperature, power and
  fan readouts.
  - **NVIDIA**: through NVAPI. Clock offsets are fully supported; power and
    temperature limits where the driver API exposes them (the page notes when
    it does not).
  - **AMD**: through ADL/Overdrive8, marked **experimental**. Every value is
    validated against the driver's own minimum and maximum. If any call
    fails the page falls back to *Open AMD Software: Adrenalin Edition*.
  - **Intel Arc**: through IGCL, marked **experimental**; integrated Intel
    graphics are monitor only. Fallback: *Open Intel Graphics Software*.
- **CPU**: Intel XTU CLI when installed (lists the tunables it recognises and
  refuses to guess when a name does not match); AMD Ryzen Master launch, and
  `ryzenadj` power limits when the tool is present (mobile and APU focus).
  Otherwise plain BIOS guidance and a download link.
- **Memory**: XMP/EXPO state and a link to the Memory page.
- **Stress test**: target (CPU, GPU, both), duration, abort temperatures,
  live readouts (elapsed time, GPU work rate, errors, temperatures and
  clocks), temperature sparklines, and a verdict with plain-English reasons
  (errors, peak temperatures, average clocks, throttling, driver resets). It
  needs no overclock consent: consent only gates applying clocks.
  - **CPU**: a built-in all-core workload that verifies its own results.
  - **GPU, built in**: a vendor-neutral DirectX 11 compute workload, so no
    game is needed (NVIDIA, AMD and Intel, integrated graphics included).
    Pick the graphics card when the PC has more than one (the one with the
    most video memory is recommended) and an intensity: *Light* (small
    buffer, about half-time load), *Medium* (near-constant load, like a
    demanding game) or *Heavy* (back-to-back work over up to 1 GB of video
    memory, capped at a quarter of the card's memory). The work per pass is
    calibrated so each pass takes 50 to 100 ms, far below the Windows driver
    timeout. With *Check results for errors* on, part of the result is read
    back every couple of seconds and compared with the first run; any
    difference counts as an error, the same kind of fault that shows up as
    artifacts in games. If the graphics driver resets, the test stops,
    reports "The graphics driver reset — the GPU is not stable at these
    settings" and resets an applied overclock to defaults.
  - **GPU, guided**: tick *Use my own game or benchmark instead* and the app
    watches temperatures and clocks while your own load runs.
  - **Safety**: the watchdog samples temperatures every second and stops at
    your limits (resetting an applied overclock); a laptop on battery below
    20 % cannot start a test and above that gets a warning; *Stop* ends
    everything within a second.
- **Claude advisor** (when configured): describe your goal and get a staged
  plan with warnings and assumptions; *Apply this step* loads the step into
  the sliders for you to confirm, *Run suggested stress test* runs it, *Send
  results back* asks for the next step. The disclaimer is always visible.
- **Watchdog**: a session marker is written when a profile is applied; if the
  PC did not shut down cleanly the next start resets the GPU and tells you.
  GPU offsets do not persist across reboots in any case.
- **Advanced**: raw limits, offsets up to the driver's range beyond your
  Settings maximum (still validated), and the session file.

---

## Assistant

### Ask Claude

Optional; nothing is sent until you add an API key and press send.

- **Not configured** state: what the feature does, what is sent, the cost
  note, *Get an API key*, a key box with *Save*, model and effort pickers, a
  *Share scan context* toggle and *Test connection*.
- **Chat**: messages with *Copy*, an input box (Enter sends, Shift+Enter
  newline), *Send* / *Stop*, suggested questions ("Why is my PC slow?", "Is my
  disk healthy?", "What should I update first?", "Explain my health report"),
  a *Context being shared* expander that shows the exact summary text that
  goes with your message, and *Clear chat*.
- Other pages can hand it a question (*Explain with Claude* on Health).

Details, privacy and troubleshooting in
[CLAUDE-INTEGRATION.md](CLAUDE-INTEGRATION.md).

---

## Footer

### Logs

- **Live log**: level and category filters, search, pause, clear, copy, *Open
  log folder*, *Export diagnostics* (a zip of the logs, the journal, settings
  with the API key removed and a system summary).
- **Logging settings**: minimum level, per-category overrides, file logging,
  folder, rolling interval, size limit, files kept, JSON format, output
  template, in-app buffer, redact serial numbers, log sensor samples.
- **Action Journal**: every change the app made with time, module, action,
  target, outcome, restore point number, before and after values and the undo
  hint; *Open journal file* and *Open System Restore*.

Details in [LOGGING.md](LOGGING.md).

### Settings

Sections with immediate save: General (theme, advanced mode, start minimized,
check health on startup), Tray and Schedules (see the next two sections),
Updates (version, channel, auto-check, auto-download, *Check now*,
*Download*, *Install*, release notes, portable-build note), Restore points (before, after, bypass frequency limit,
and the restore-point list with *Create now*, *Enable System Protection* and
*Open System Restore*), Monitoring thresholds, Electricity price, Fans
(revert on exit, minimum percent), Lighting (OpenRGB host and port,
auto-connect), Latency (ping targets and count), Overclock limits (GPU core
and memory offset, highest GPU power limit, highest GPU and CPU
temperature), Memory and Startup warnings, Claude, Data and privacy
(open settings and log folders, reset settings, export diagnostics) and
About (version, licences, GitHub link). A note shows when the app is running
in demo mode. Overlay options are on the Overlay page, logging options on the
Logs page and cleanup categories on the Cleanup page.

### Tray

PC Checker keeps an icon in the notification area next to the clock.

- **Tooltip**: hover the icon for "PC Checker — CPU 62 °C · GPU 58 °C",
  refreshed every 2 seconds, plus the number of active alerts.
- **Popup** (left-click): a compact, rounded panel next to the tray with CPU,
  GPU and hottest-drive temperatures coloured by your Monitoring thresholds,
  CPU and GPU load, RAM use, estimated power draw, the alert count, a fan
  profile quick switch, the overlay on/off switch and the buttons *Open PC
  Checker*, *Quick clean* and *Exit*. It hides when you click elsewhere.
- **Menu** (right-click): *Open PC Checker*, *Show overlay*, *Fan profile ▸*,
  *Check health now* and *Exit PC Checker*.
- **Quick clean** deletes temporary files from the Safe cleanup categories you
  use on the Cleanup page, after you confirm. Files changed recently (the
  "skip files newer than" Cleanup setting, an hour by default), files in use and the Recycle Bin are left alone. The
  result arrives as a notification.
- **Fan profile switch** applies a built-in or saved profile; the first switch
  in a session asks for the same consent as the Fans page.
- **Exit** from the tray runs the normal shutdown: fans go back to automatic
  (when that option is on), the overclock session is marked clean and the logs
  are flushed.

Settings → Tray:

- *Show the PC Checker icon in the tray* (on by default). While *Minimize to
  the tray* or *Close button keeps PC Checker in the tray* is on, the icon
  stays so you can get back to the window; the setting says so.
- *Minimize to the tray*: the minimize button hides the window; monitoring,
  alerts and the overlay keep running Together with *Start minimized*, PC
  Checker starts hidden in the tray.
- *Close button keeps PC Checker in the tray*: closing hides the window. Exit
  from the tray menu to quit.
- *Start with Windows*: adds the task `PC Checker\Start` to Task Scheduler
  (`schtasks /Create /XML`: at your sign-in, 30 seconds later, highest
  privileges). Because the task has highest privileges, the app starts
  without a UAC prompt. Unlike Task Scheduler's defaults, the task also starts
  on battery power and has no time limit, so a laptop on battery still starts
  PC Checker and Windows never ends a long-running session (which would skip
  the normal shutdown that puts fans back to automatic). Turning it off
  removes the task.
- *When started with Windows, stay in the tray*: the task starts
  `PcChecker.exe --tray`, which opens with only the tray icon.
- *Show the temperature as the tray icon*: the icon becomes the hotter of the
  CPU and GPU in °C, white below your warning level, yellow at warning and red
  at critical.
- *Temperature alerts as notifications*: while the window is hidden or in the
  background, a temperature alert shows as a Windows notification, at most one
  per part every 10 minutes. Clicking it opens Temperatures.

Starting `PcChecker.exe` a second time brings the running copy to the front,
even when it is hidden in the tray.

### Schedules

Settings → Schedules runs four maintenance jobs on a timetable through Windows
Task Scheduler, so they run even while PC Checker is closed:

| Job | What it does | Needs your attention when |
|---|---|---|
| Health check | Runs the full health check and keeps the score and critical items. | The score dropped 5 points or more since the last run, or a new critical problem appeared (on the first run: score below 70 or any critical item). |
| Junk file cleanup | Scans and cleans the **Safe** categories you use on the Cleanup page. Never Moderate or Advanced categories, never the Recycle Bin, never files changed recently (the Cleanup setting, an hour by default). Optional dry run that only counts. | A file could not be removed. |
| Driver update check | Asks Windows Update for newer drivers. Nothing is downloaded or installed. | Updates are available. |
| PC report | Saves a full HTML report (hardware, health, performance, drivers) to `Documents\PC Checker Reports` and keeps the newest N scheduled reports (10 by default). Reports you saved yourself are never removed. | Never. With a notification after every run, clicking it opens the report. |

For each job: an on/off switch, daily or weekly (with the day), the time
(24-hour), the job's options, the next run, the task's state in Task Scheduler
(including its last result in plain words), the last result with when it ran
and how long it took, and *Run now* (*Clean now* for cleanup, which asks
first). Turning a job on explains what will happen and creates the task;
changing the time or day shows *Save*, which updates it; turning it off
removes the task.

How it works:

- One task per job in the `PC Checker` folder of Task Scheduler, registered
  with `schtasks /Create /TN "PC Checker\<Job>" /XML <definition> /F`. The
  definition runs `"<path to PcChecker.exe>" --scheduled <job>` daily or
  weekly at your time with highest privileges, also on battery power (Task
  Scheduler's defaults would skip a laptop's runs on battery), with a one-hour
  hard stop behind the job's own 10-minute limit.
- The definition file for this and for *Start with Windows* is written to a
  new folder under `%ProgramData%` that only Administrators and SYSTEM can
  change, and is deleted straight after. It never goes through your `%TEMP%`
  folder, where another program running as you could swap it for its own
  command before Task Scheduler reads it.
- A task (a schedule or *Start with Windows*) is only created when
  `PcChecker.exe`, its folder and every folder above it can be changed by
  Administrators, SYSTEM and TrustedInstaller only, as under `Program Files`.
  From a folder such as Downloads or the Desktop, another program running as
  you could replace `PcChecker.exe` and be started with administrator rights
  without a UAC prompt, so PC Checker asks you to move it to
  `C:\Program Files\PC Checker` first.
- When PC Checker runs as a different administrator account than the person
  signed in (a standard account that typed an administrator's password at the
  UAC prompt), no task is created: Task Scheduler would only start it while
  that administrator is signed in, never for you.
  Status is read with `schtasks /Query /FO CSV /V /NH` by column position, so it
  works on Windows in any language.
- `PcChecker.exe --scheduled <job>` runs without a window. If PC Checker is
  already open in your Windows session, the job is handed to it over a local
  named pipe tied to your session and account; otherwise it runs on its own. Each run stops after 10
  minutes at most, writes its result to
  `%LOCALAPPDATA%\PcChecker\schedules\<job>-last.json` and the log, shows a
  notification and exits with code 0 (success) or 1 (failure), which Task
  Scheduler shows as the task's last result.
- *Notify only when something needs attention* (on by default) keeps quiet
  runs quiet. Turn it off to get a notification after every run. Clicking a
  notification opens the matching page (or the report).
- If you move `PcChecker.exe`, the tasks still point at the old path. PC
  Checker notices on start (a Dashboard notice) and Settings → Schedules shows
  *Fix tasks*, which points every enabled task at the current file. Self-updates
  keep the same path, so they need nothing.
- Creating or removing a task is recorded in the Action Journal. No restore
  point is made for it: a scheduled task is not a system-file change and is
  removed with one switch.
- *Reset to defaults* also removes PC Checker's tasks and the *Start with
  Windows* task.

Limitations: tasks run in your Windows session, so Task Scheduler skips a run
while you are signed out or the PC is off or asleep; the next scheduled time
runs as normal. In demo mode nothing is scheduled ("Demo mode: schedules are
not created") and the page shows sample results.

---

## Updates

The app checks GitHub Releases for new versions and, when installed with the
Setup installer, downloads and applies them in place. The portable build only
tells you a version is available and links the release page. Choose the
Stable or Beta channel in Settings.

## Reports

*Export full report* on the Dashboard writes hardware, health, performance,
software, drivers and sensors as a standalone HTML page (dark theme, print
friendly), JSON or Markdown. Reports contain the same data the app shows,
including serial numbers unless *Hide serial numbers and MAC addresses in logs* is on.

## Demo mode and screenshots

`PcChecker.exe --demo` runs every page with realistic sample data for a
gaming desktop and never touches the real machine or your settings.
`PcChecker.exe --screenshots <dir>` captures every page, the overlay and the
tray popup to PNG; the
`Screenshots` workflow does this whenever app code changes on `main` (and on
demand). See [DEMO-MODE.md](DEMO-MODE.md).
