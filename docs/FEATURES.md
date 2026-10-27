# Features

A page-by-page tour of what PC Checker does, what every button does, and
where each page stops. Sections marked **Advanced** only appear when
*Advanced mode* is on (Settings → General, or the toggle at the bottom of the
navigation pane).

The side menu has one item per section. A section with several pages shows
them as tabs above the content:

```
Home            Dashboard
Monitor         Temperatures · Power · Processes
Monitor         Temperatures · Power · History
Hardware        Overview · Health · Storage · Memory
Devices         Displays · Audio · Keyboard & Mouse
Cooling & RGB   Fans · Lighting
Apps & Drivers  Apps · Drivers · Startup · Windows Features
Clean up        Cleanup · Debloat · Registry
Performance     Speed check · Optimize · Benchmark · Overclock
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
- **Live tiles**: CPU temperature, GPU temperature, memory used and current
  power draw, straight from the sensor service.
- **Check-up strip**: what the last check-up found (or what a check-up does)
  with *Run check-up*, which opens the Check-up page and starts it.
- **At a glance**: *Windows Update* (service turned off, restart pending,
  updates waiting from the last check-up, paused, or up to date, with when it
  was last checked), *Temperatures, last 24 hours* (CPU and GPU sparklines
  from History, half-hour peaks, with the day's peak; a note instead when
  History is off or has nothing yet) and *Fullest drive* (the drive with the
  largest used share from Disk space, with a bar and the free space; the
  hardware scan is the fallback). Each links to its page.
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


### Check-up

One button checks everything and turns the results into one to-do list.
Nothing changes until you choose a fix.

- **Run check-up** runs, one after another with one combined progress bar
  and a Cancel button: the health check, a cleanup scan of the Safe
  categories (the ones you picked on the Cleanup page, or the defaults), the
  installed-driver scan and the driver update check, the Windows Update
  status and update check, the startup apps with their estimated impact, the
  speed check, the state of the Debloat page's *Recommended* settings and the
  old-driver scan. It uses the same services as those pages; a part that
  fails or takes more than five minutes is skipped and named in a warning.
- **The to-do list** has one line per thing worth doing: the title, why it
  matters, the benefit of fixing it, its risk (*Safe*, *Moderate* or *Advanced*)
  and the page it comes from (a link). Serious problems come first; within
  the same level, things the app can fix by itself, safer fixes and bigger
  gains come first. Repeats are merged (a restart pending reported by both
  Health and Windows Update is listed once).
- **Fix** is offered where the owning service has a safe automatic fix:
  clean the Safe junk, stop a heavy startup app starting with Windows,
  install the recommended Windows updates or the offered driver updates,
  apply a reversible optimization behind a speed check finding, apply the
  Safe *Recommended* privacy settings that are not applied yet, and remove
  old driver packages marked *Safe to remove*. Everything else has
  **Open**, which takes you to the page.
- **Selection**: Safe fixes that suit everyone are ticked after a check-up.
  Startup apps, the privacy settings and the speed check optimizations
  (such as turning on Storage Sense for a full drive, which also empties the
  Recycle Bin over time) are choices for you and are never ticked for you,
  Moderate fixes (driver updates) are never ticked automatically, and
  Advanced ones can only be ticked in Advanced mode.
- **Fix selected** confirms what will happen, naming the setting an
  optimization changes rather than only the finding it helps with, then
  applies the ticked fixes
  one service at a time: junk, old drivers, startup apps, optimizations,
  recommended settings, driver updates, Windows updates. Each service runs
  its change through the change guard as on its own page, but the run
  shares **one restore point** created first, so a batch does not make one
  restore point per page (and Windows does not refuse the second one). If
  that restore point cannot be created, every change follows its own rules
  and the ones that need a restore point do not run. Each item shows its
  result; the Action Journal lists every change. Cancel stops before the
  next step; driver installs and removals cannot be stopped once started, so
  Cancel is hidden for a run that includes them.
- In demo mode the page runs a check-up on its first visit; in normal mode it
  waits for the button and keeps the last result for the session.

Limitations: the check-up always scans afresh, so it takes a few minutes
(the Windows and driver update checks can take a minute each). The Health and
Speed check pages show its new results; the other pages keep their own scans.

---

## Monitoring

### Temperatures

Live readings from every sensor LibreHardwareMonitor can see.

- **Gauges** for CPU, GPU, the hottest drive and the motherboard or VRM,
  coloured by your warning and critical thresholds.
- One expander per piece of hardware (CPU, GPU, each drive, motherboard,
  memory, PSU or cooler) listing each temperature sensor with its current,
  minimum, maximum and average for the session and a sparkline.
- **Drives**: the drive temperature is the NVMe composite (or SMART 194 on
  SATA). Extra NVMe sensors are listed as *Sensor N* or *Controller*, marked
  "extra sensor", with limits 15 °C above the drive limits. The warning and
  critical limits a drive reports are not shown as temperatures.
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

### Processes

*Task manager → Processes.* Everything Windows 11 Task Manager's Processes
and Details tabs do, in plain words. The list refreshes while the page is
open at the chosen **Update speed**: High (twice a second), Normal (every
second), Low (every 4 seconds) or Paused; *Pause updates* freezes it to read.

- **Tiles**: total CPU use, memory in use, the number of processes (apps and
  background programs), and the busiest program right now. A banner names any
  program that is not responding.
- **Processes view** (default): three sections like Task Manager. *Apps* are
  programs with a window on the taskbar; the helpers they started (Steam's web
  helpers, Chrome's tabs) are listed under them with summed figures, and a
  game started from Steam is an app of its own. *Background processes* run
  without a window; *Windows processes* are part of Windows (including
  Microsoft-signed programs in the Windows folder; drivers' helpers count as
  background). Expand a group with its arrow, a double-click or the Right
  arrow key. Under a browser, helpers are labelled *Tab or extension*,
  *Graphics helper*, *Network helper* and so on.
- **Details view** (switch): one flat list of every process with its file
  name, like Task Manager's Details tab.
- **Columns**: Name (icon, friendly name, window title or *what is this?*),
  Status (*Not responding*, *Suspended*, *Efficiency mode*), PID, CPU, Memory
  (private working set, the figure Task Manager shows), Disk (MB/s), Disk read
  and write, Network, GPU (the busiest graphics engine, as Task Manager
  shows it), GPU engine, Power usage and Power usage trend (Very low to Very
  high, estimated from processor, graphics and disk use; the trend covers two
  minutes), Publisher with its signature state, User name, Session,
  Architecture (x64, x86, ARM64), Elevated, Priority, Handles, Threads,
  Started, Description, Command line and File. *Columns* (or a right-click on
  a column title) picks which ones show; the choice is saved separately for
  each view, and *Reset to default columns* brings back the defaults. Busy
  cells get a stronger orange tint (heat map) that keeps the text readable in
  both themes. Click a title to sort; drag a title to move the column.
- **Search** (Ctrl+F): name, description, window title, PID, publisher or
  folder.
- ***What is this?***: a built-in catalogue explains more than 120 well-known
  Windows, Defender, driver (NVIDIA, AMD, Intel, Realtek), launcher (Steam,
  Epic, Battle.net, EA, Ubisoft), browser, chat and hardware-utility
  processes. Unknown programs show their signed publisher instead.
- **Publisher**: each program file gets an offline Authenticode check (its
  own signature, then the Windows catalogs) once per session.
- **Flags** (information only): a warning sign marks programs that run from a
  Temp or AppData folder without a valid signature, unknown and unsigned
  programs using a lot of CPU, GPU or memory, and programs with a Windows
  name running from outside the Windows folder. *Only flagged* filters the
  list. PC Checker never acts on a flag by itself.
- **Actions** (header, *More*, right-click and the selected program's card),
  each confirmed first:
  - *End task* (Delete): a program with a window is asked to close, as if
    you clicked its X, so it can save; if it is still open after a few
    seconds you choose *End now* or *Switch to it*. A program that is not
    responding, suspended or has no window is ended straight away. For a
    group, every process of the program.
  - *End process tree*, *Restart* (closes the program and starts the same
    file with the same options, with your normal rights unless it had
    administrator rights and lives in Program Files or the Windows folder;
    File Explorer is restarted by Windows itself). Store apps, Windows'
    own programs and browser helper processes are not restarted: start them
    again from the Start menu or restart the main program.
  - *Suspend* / *Resume*: pauses a program so it uses no processor time.
  - *Efficiency mode*: Windows' EcoQoS power throttling plus Low priority,
    as Task Manager sets it; turning it off restores Normal priority.
  - *Set priority* (Realtime only in Advanced mode, with a warning) and *Set
    affinity* (one tick per logical processor).
  - *Switch to* (Enter or double-click), *Minimize*, *Maximize*.
  - *Create memory dump file*: a full dump in your Temp folder
    (`name.DMP`, then `name (2).DMP`), opened in File Explorer afterwards;
    the dialog warns that it can contain anything that was open.
  - *Open file location*, *Search online*, *Properties* (Windows' file
    properties) and *Copy details* (Ctrl+C).
- **Run new task** (header): opens a program, folder, document or web
  address and remembers the last ten. It starts with your normal rights,
  even though PC Checker runs as administrator, unless you tick *Create this
  task with administrative privileges*. Only programs in Program Files or
  the Windows folder are started as administrator (a bare name such as
  `cmd` is looked up on the PATH first). The program opens through the
  desktop, so a mistyped name shows Windows' own error message.

Every change is recorded in the Action Journal with an undo hint; no restore
point is made because no Windows setting changes.

Never offered: ending, pausing, throttling or re-prioritising System,
Registry, Memory Compression, smss, csrss, wininit, winlogon, services, lsass
and PC Checker itself (Windows' own "critical" flag is checked too).
Windows processes are never suspended or put in efficiency mode, services and
other accounts' programs are not restarted, and no memory dump is made of
Local Security Authority (it holds sign-in secrets).

Limitations: Windows does not report network data per program without an
event trace, so the Network column counts open connections (TCP and UDP)
instead of showing Mbps. Power usage is an estimate, like Task Manager's.
Protected processes and programs of other accounts may refuse an action or
show no path, user, command line or memory figure. The GPU columns are empty
when the graphics driver has no *GPU Engine* performance counters. Affinity
is offered on PCs with up to 64 logical processors. Starting a program
without administrator rights needs File Explorer's desktop to be running.
CPU and disk figures are averages over the last refresh.

### History

What the PC did over the last hour, day, week, month or three months.

- **Recording**: while PC Checker is open, also when it is minimised to the
  tray, a background recorder takes one sample a minute (configurable): the
  highest CPU and GPU temperature in that minute, the average CPU and GPU
  load, power draw and memory use, and the free space of every fixed drive
  once an hour. Every health check (on the Health page, the Dashboard or a
  scheduled run) adds its score.
- **Storage**: one small JSON Lines file per day in
  `%LOCALAPPDATA%\PcChecker\history`. After 7 days a day is rolled up to
  15-minute points and after 30 days to hourly points (temperature peaks and
  the lowest health score are kept, loads and power are averaged), so 90 days
  take a few megabytes. Days older than the retention setting (default 90)
  are deleted. A line cut short by a crash or power cut is skipped and
  dropped at the next roll-up. Nothing leaves the PC.
- **Range picker**: last hour, 24 hours, 7 days, 30 days or 90 days.
- **Tiles**: hottest CPU and GPU with when it happened, energy used in kWh
  with its cost at your electricity price, and the number of notable events.
- **Charts**: one per metric (temperatures with the warning limit as a dashed
  line, loads, power, memory, health score, free space per drive) with value
  and time axes, average, highest and lowest, gaps where the PC was off, and
  the exact value under the mouse.
- **Notable events**: temperature spells above your warning limits (one per
  spell, at its peak; critical when the critical limit was reached), sudden
  drops in a drive's free space (at least 10 GB and 5 % within a day), and
  health scores that fell by 5 points or more.
- **Export CSV** saves the recorded values of the chosen range. *More* opens
  the history folder or the recording settings.
- **Settings → History**: record on or off, how often (30 seconds to 15
  minutes) and how many days to keep (7 to 365).

Limitations: nothing is recorded while PC Checker is closed, so those times
show as gaps. Temperatures, loads and power need the same sensors as the
Temperatures and Power pages; without them only memory, free space and
health scores are recorded. Demo mode shows 30 days of generated history and
writes nothing to disk.

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

### Battery

Hardware → Battery shows how worn a laptop or tablet battery is and how long a
charge lasts. It only reads; nothing on the PC changes.

- **Summary tiles**: battery health (full-charge capacity as a percentage of
  the design capacity, coloured with the same 20 % / 40 % wear limits as the
  Health page), charge cycles, full charge against design capacity in Wh, and
  how long a full charge lasts in use now and when the battery was new.
- **Capacity history**: a chart of the full-charge capacity over the life of
  the battery, one point per day or week as Windows recorded it, with the
  design capacity as a dashed line and the average loss per month.
- **Right now**: charge, whether it is charging or discharging and at how
  many watts, the time left, and the battery's name, manufacturer and
  chemistry.
- **Recent use**: the last three days from Windows' battery report, newest
  first: in use or asleep, on battery or plugged in, how long, and the charge
  before and after (the drain per hour in the tooltip).
- **Make the battery last longer** (collapsed): tips that fit the battery, such
  as a charge limit in the maker's app when the PC is mostly plugged in, battery
  saver, heat, and a replacement once it has lost 40 % or more.
- **Usage history** (collapsed): time on battery and plugged in per day or
  week, with Windows' estimate of how long a full charge lasted then.
- **Open the full report** has Windows write its detailed HTML battery report
  to `Documents\PC Checker Reports` and opens it. **Battery settings** and
  *More → Power settings* open the matching Windows Settings pages.
- **Desktops**: the page says "This PC has no battery" instead of showing
  empty charts. A UPS (battery backup) plugged in over USB is recognised and
  pointed to its maker's software.

Sources: `Win32_Battery`, `BatteryStaticData`, `BatteryFullChargedCapacity`
and `BatteryCycleCount` from WMI, the live state the Power page uses, and
`powercfg /batteryreport /xml`, whose numbers, dates and durations do not
depend on the Windows display language. A missing source becomes a warning and
the page shows what the others returned. Demo mode shows a two-year-old 57 Wh
laptop battery at 86 % health after 318 cycles.

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
- **Check for updates** searches several sources side by side and shows one
  list grouped by device. Each device shows its installed version and every
  source's newest version, release date, size and source; the newest is
  marked *Newest*, and a radio button picks the source per device (the newest
  is chosen by default; on a tie Windows Update wins). Versions are compared
  in each maker's own numbering: NVIDIA's 560.94 is the last five digits of
  the Windows version 32.0.15.6094; AMD's Adrenalin version (24.12.1) cannot
  be derived from the Windows version (32.0.21013.1000), so the app reads the
  `RadeonSoftwareVersion` the AMD driver records and otherwise says it cannot
  compare.
  - **Windows Update**: the driver catalog through the Windows Update Agent.
    *Download only* and *Install selected* work on the ticked devices whose
    chosen source is Windows Update (confirmed; restore point; reboot bar
    afterwards with *Restart now* or *Later*).
  - **NVIDIA**: NVIDIA's driver service (the one behind the NVIDIA app and
    nvidia.com's driver search) is asked by the card's PCI device id, so
    GeForce, RTX/Quadro and notebook GPUs all get their own package. *Game
    Ready* or *Studio* is chosen under *Update sources*.
  - **AMD**: the current AMD Software: Adrenalin Edition release from AMD's
    official driver page; the package is AMD's auto-detect installer, which
    picks the driver for the Radeon GPU or Ryzen APU while it runs.
  - **Intel**: Intel publishes no public version feed, so Intel graphics (Arc
    and Iris Xe, or the 7th–10th gen package), Wi-Fi and Bluetooth link to
    their exact Intel download page with your installed version alongside.
    Intel's Driver & Support Assistant is offered too (open it, or install it
    with winget).
  - **PC maker tools**: Dell Command | Update and SupportAssist, HP Support
    Assistant and HP Image Assistant, Lenovo Vantage and System Update,
    MyASUS and Armoury Crate, MSI Center and GIGABYTE Control Center are
    found by their uninstall entries, program folders and Store packages.
    Installed ones get *Open*; missing ones get *Install with winget* where
    the tool is on winget (the exact command is shown, and it only runs when
    winget's own program is found in its protected folder), and every maker
    gets its support page for your exact model (from the PC's maker and model,
    or the motherboard on self-built PCs).
- **Download and install** (NVIDIA and AMD packages): asks every time and
  shows the file, its size and the server. The file is only fetched over
  HTTPS from the maker's own download servers (every redirect is checked
  against the same list), into a fresh folder only administrators can change.
  Before it runs, Windows checks its Authenticode signature with the full
  certificate chain and revocation, and the signer must be NVIDIA Corporation,
  Advanced Micro Devices, Inc. or Intel Corporation; anything else is deleted
  without running. A restore point is created, then the maker's installer
  runs: NVIDIA's can run silently or as a silent clean install with NVIDIA's
  documented switches (`-s`, `-clean`, `-noreboot`), AMD's always opens.
  Afterwards the download folder is removed and the driver list refreshed.
- **Update sources** (collapsed at the end of the Updates tab): turn each
  source on or off (all on by default), pick the NVIDIA branch and installer
  mode, and choose whether vendor downloads ask each time (default) or never
  happen (the vendor's page opens instead).
- When a source cannot be reached, its page changed or it does not know a
  device, the other sources still show, a note says what happened, and the
  device gets *Open the vendor page*.
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

Limitations: Intel versions are not compared automatically (no public feed);
AMD versions are only compared when the AMD driver records its Adrenalin
version; vendor packages are never run unless signed by the maker. The app
never downloads from third-party driver sites.

### Windows Update

The Windows Update tab (Apps & Drivers, after Old drivers) covers software
updates; driver updates stay on the Drivers page.

- **Status tiles**: updates waiting (and how much to download), last check,
  last successful install (from the Windows Update Agent, or the history when
  Windows did not record it) and whether updates are paused. A restart bar
  appears while Windows waits for a restart to finish an update.
- **Set by policy**: when Group Policy or device management
  (MDM) sets Windows Update policies, an info bar lists them in plain English
  (pause disabled, active hours set, automatic updates off, an update server,
  deferrals, a pinned Windows version). Controls that a policy owns are shown
  read-only. With an organisation's update server (WSUS) configured, *Check
  for updates* asks that server instead of going around it.
- **Updates** tab: *Check for updates* asks Windows Update (the same service
  id the Drivers page uses) for software that is not installed yet. Each
  update shows its KB article, size, type (cumulative, security, feature
  update, definitions or other, decided from Windows Update's
  language-independent category ids), Microsoft's severity rating and
  whether it needs a restart. Optional updates (previews, new Windows
  versions) are never ticked for you, and updates that ask questions while
  they install are left to Settings. *Install selected* and *Install all*
  confirm first, create a restore point (per Settings; the install does not
  start if it fails), then download and install one update at a time with
  progress. Cancel stops before the next update. Every update gets its own
  result with the error explained in plain English (for example 0x800F0922,
  0x80070070, 0xC1900101), and installed ones leave the list.
- **History** tab: the last 100 installs, newest first, with type, result and
  date. Failed and cancelled installs are marked red and their row explains
  what went wrong; *Failed only* filters the list. *Update history in
  Settings* opens the Settings page where updates can be uninstalled.
- **Pause & active hours** tab:
  - *Pause updates* for 1 to 5 weeks, or *Extend pause* while paused. PC
    Checker writes the same values the Settings app writes
    (`PauseUpdatesStartTime`, `PauseUpdatesExpiryTime` and the feature and
    quality pairs under
    `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings`, as UTC times like
    `2026-11-03T09:30:15Z`), keeps the original start when extending and
    never goes past the limit Windows allows (35 days from the start, or
    `FlightSettingsMaxPauseDays`). The values are read back after writing;
    if Windows did not keep them, on a build older than Windows 10 version
    2004, or when a policy owns pausing, the page offers to open Settings
    instead. *Resume* removes the values, as Settings does.
  - *Active hours*: start and end hour, at most 18 hours apart (fewer when
    the ActiveHoursMaxRange policy says so) and allowed to run past
    midnight. Saving writes `ActiveHoursStart`/`ActiveHoursEnd` and the
    hand-picked pair `UserChoiceActiveHoursStart`/`UserChoiceActiveHoursEnd`
    that Settings restores them from. While Windows adjusts active hours
    automatically, or when saving fails, the page sends you to Settings
    instead. The read-back only confirms the values were stored, so the
    result also says to check Settings.
  - Every change is confirmed and recorded in the Action Journal with the
    previous value; pause and active hours do not create restore points.
- **Roll back** tab (going back after an update). The lists are read the
  first time the tab opens (DISM takes a moment); *Refresh* reads them again.
  - *Installed updates*: every update `Get-HotFix` (`Win32_QuickFixEngineering`)
    lists, with its KB, title and type from the Windows Update history and
    the install date, plus the cumulative update's package from
    `DISM /Online /Get-Packages`. Each row says how it would be removed:
    - standalone updates (.NET, ordinary and security updates) with
      `wusa.exe /uninstall /kb:<n> /quiet /norestart`;
    - the monthly Windows cumulative update with
      `DISM /Online /Remove-Package /PackageName:<Package_for_RollupFix…> /Quiet /NoRestart`,
      because wusa cannot remove the combined servicing stack and cumulative
      updates Windows 10 and 11 ship. The package is the installed RollupFix
      whose version matches the build in the update's title (for example
      `(26100.4061)`), or the newest installed one when the title has no
      build. Releases that ship as an enablement package are matched on the
      build they are serviced from (25H2 `26200` on `26100`, 23H2 `22631` on
      `22621`, Windows 10 22H2 `19045` on `19041`) with the same revision.
      Cumulative updates that a newer one replaced cannot be removed;
    - servicing stack updates are refused with the reason: Windows marks them
      permanent and needs them for every later update. They are recognised by
      their title, and, in any display language or without an update history,
      by the DISM package list: an update with no package of its own while a
      `Package_for_ServicingStack` package is installed is refused too.
    *Uninstall* explains what is lost, creates a restore point (the removal
    does not start if it fails), runs the tool from the system folder and
    reports the result. It never restarts; a restart bar offers *Restart now*
    when Windows needs one to finish. Afterwards it offers to hide the update
    so Windows Update does not reinstall it.
  - *Hide this update* (on a pending update's row in the Updates tab, or on an
    installed one) sets `IUpdate.IsHidden`, the flag Microsoft's "Show or
    hide updates" troubleshooter uses: Windows Update no longer offers or
    installs it. *Hidden updates* lists them with *Show again*. An update is
    found by its Windows Update id (from the check or the install history),
    or by KB among the updates that are not installed; required updates
    cannot be hidden.
  - *Go back to the previous version of Windows*: after a feature update,
    `DISM /Online /Get-OSUninstallWindow` gives the window (10 days by
    default), counted from the upgrade date (`InstallDate`, or when
    `Windows.old` was created) to show how many days are left. *Go back…*
    asks twice (back up your files; apps installed since may need
    reinstalling; you need the old password; it cannot be undone here), then
    runs `DISM /Online /Initiate-OSUninstall /Quiet /NoRestart` and offers
    the restart that carries it out. *Keep the option for* sets
    `DISM /Online /Set-OSUninstallWindow /Value:<14–60>`. Without
    `C:\Windows.old`, or when DISM says the option has gone, the card says
    there is nothing to go back to. Storage Sense, Disk Cleanup or deleting
    Windows.old ends the option; the Cleanup page's Windows.old category says
    the same.
  - *Open Drivers*: driver updates are rolled back on the Drivers page.
  - wusa and DISM print translated text, so results are decided from their
    exit codes only (0, 3010 and 1641 restart needed, 2359303 and 0x80070490
    not installed, 0x80240017 not applicable, 0x800F0825 permanent, 0x800F0806
    restart first, 5 and 740 access denied, 1618 another install running, and
    so on), each with a plain-English message. DISM runs with `/English` and
    its package list is parsed by the package identities, which are never
    translated.
  - Uninstall, hide, show again, go back and the window change are recorded
    in the Action Journal; only *Uninstall* creates a restore point (going
    back replaces Windows together with its restore points).
- **Links**: Windows Update, Update history, Advanced options, Active hours
  and Recovery in the Settings app.

Limitations: the history is what the Windows Update Agent recorded (updates
installed by other tools such as DISM or a standalone installer are not in
it); restarts are left to you or to Windows outside active hours. Going back
is only possible within Windows' own window and while Windows.old exists; an
update Windows Update no longer lists cannot be hidden until it is offered
again.

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
- **Find leftovers**: after a program is uninstalled the page offers to look
  for what it left behind; the Leftovers tab opens and scans with that
  program's traces marked *Just uninstalled* and ticked.

### Leftovers

- **Scan** (read only, cancellable) compares what is on disk with every
  installed program and Store app (names, makers, package names and install
  folders, with fuzzy matching that ignores versions, "x64", brackets and
  "Inc."/"GmbH"). It reports:
  - folders in Program Files, Program Files (x86), ProgramData and
    AppData\Local, Roaming and LocalLow that nothing installed uses, one level
    deeper inside a maker's folder that still has other programs, and empty
    folders uninstallers left;
  - registry keys under `HKLM\SOFTWARE`, `HKLM\SOFTWARE\WOW6432Node` and
    `HKCU\Software` of makers or products that are no longer installed;
  - services whose program file is gone, scheduled tasks that start a missing
    program, and Start menu shortcuts to missing targets.
- **Each finding** says what it is, why it looks orphaned, its size and a
  confidence: **High** (empty folders, things whose whole program folder is
  gone, traces of the program just uninstalled), **Medium** (probably left
  behind) or **Low** (recently changed, or inside a maker's folder that still
  has programs). Only High is ticked; *Select recommended* ticks it again.
- **Never reported**: Windows and Microsoft folders and keys, shared folders
  (Common Files, Package Cache, InstallShield data, Store packages), NVIDIA,
  Intel, AMD, Realtek and other driver parts, game libraries (Steam, Epic,
  GOG, Ubisoft, Xbox), developer caches, GUID-named installer folders,
  hidden dot-folders, links (junctions) and anything outside the listed
  folders. Services and shortcuts that point into the Windows folder are left
  to Windows.
- **Remove selected** (confirmed) creates a restore point first and checks
  every item again right before touching it: folders and shortcuts go to the
  Recycle Bin; registry keys are exported to one sealed `.reg` backup (the
  Registry page's format, so it also appears under Registry → Backups);
  scheduled tasks are exported to XML and deleted; services are deleted with
  `sc.exe` only while their program is still missing and they are stopped.
  Folders are only removed one or two levels inside the listed roots, never
  through or inside a link. Every item is written to the Action Journal.
- **Restore list** puts removed registry keys and scheduled tasks back. Task
  backups are sealed in `HKLM` and only imported unchanged, through a folder
  only administrators can write to, and never over an existing task.
- **Limitations**: folders of portable programs (which have no uninstall
  entry) can show up; they are scored Low or Medium when they hold programs
  or changed recently, so check before ticking them. A folder Windows will
  not let PC Checker read completely is not reported. Per-user folders and
  keys are those of the account PC Checker runs as.

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
  exactly what was there (or the Windows default, which it says). When
  Windows cannot make the restore point, the page asks whether to turn on
  System Protection, continue without one (reversible tweaks only) or cancel.
- **Results** are read back from the PC after every batch: *Applied*,
  *Already applied*, *Skipped* or *Failed*, each with a plain reason, and
  applied changes say whether they show after a File Explorer restart, a
  sign-out or a Windows restart. Per-user tweaks go to the account signed in
  to Windows, even when PC Checker runs as another administrator.
- **Restart Explorer** (this session only, not elevated) or **Restart later**
  is offered after taskbar, Start, File Explorer and context menu changes; a
  sign-out note and a restart bar appear when those are needed.
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
  Windows.old. Its card warns that removing it, including through Storage
  Sense or Disk Cleanup, ends the option to go back to the previous version of
  Windows (Windows Update → Roll back shows the days left).

Safety rules: never outside the category roots, never through junctions or
symbolic links, never files modified in the last 60 minutes (configurable),
never `desktop.ini`, and browser caches are skipped while the browser runs.

### Disk space

- **Drives**: every fixed drive as a card with a used/free bar. Click one to
  measure it, or *Choose folder…* to measure a single folder. The scan reads
  with several threads at once and can be cancelled; it never changes
  anything.
- **Summary**: what was scanned, the space used, the largest folder and the
  number of files and folders. A note explains gaps: folders Windows keeps
  private, space Windows reports that no file accounts for, links that were
  not followed and OneDrive online-only files (counted, but they take no
  space).
- **Folders**: the sub-folders of the current folder, largest first, with a
  bar for their share. Click a name to go deeper; the breadcrumb and the up
  button go back. The files directly in a folder show as one line.
- **Largest files**, **File types** (video, images, games, archives and disk
  images, installers, Windows and programs, other) and **Old files** (big
  files nobody opened or changed for a year; size and age are options).
- **Duplicates**: *Find duplicates* compares the files from the last scan by
  size, then a quick SHA-256 of the first and last 64 KB, then a full SHA-256.
  Groups are listed by wasted space; *Keep newest* and *Keep oldest* tick
  every other copy. Hard links to the same data count once.
- **Actions** on every row: *Show in File Explorer* and *Copy path*. Tick
  files or folders in any list and *Move to Recycle Bin*; the confirmation
  lists the items and their total size, and warns about game folders and
  OneDrive items.

Safety rules: items only ever go to the Recycle Bin, never straight to
deletion, and only on drives inside the PC (removable and network drives have
no Recycle Bin). If Windows cannot recycle an item it asks before deleting it.
The Windows folder, Program Files, ProgramData, the profile folders
themselves (and their AppData folders), Windows.old, restore points, the
Recycle Bin, the page and hibernation files, files marked System and PC
Checker's own folders cannot be ticked, nor can any folder that contains one
of them. Junctions and symbolic links are never followed, and each item is
checked again right before it moves. Every move is written to the Action
Journal; no restore point is made because the Recycle Bin is the undo. The
space comes back when the Recycle Bin is emptied (Cleanup page).

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

### Benchmark

Six short tests score the PC against a reference machine, a mid-range 2024
desktop (6-core/12-thread processor, dual-channel DDR5-6000, PCIe 4.0 NVMe,
GeForce RTX 4060-class graphics), which scores about **1,000** on each test.
Nothing on the PC is changed.

- **Processor, single-core** and **multi-core**: Deflate compression of
  generated text, a prime sieve, a double-precision matrix multiply and
  SHA-256, on one thread and then on every logical processor. Each kernel
  checks its answer against the first one, so a processor that computes a
  wrong result (unstable overclock or undervolt) is called out.
- **Memory bandwidth**: several threads copy and write a large buffer
  (256 MB, less on PCs short of free memory). **Memory latency**: a pointer
  chase through a random single cycle of cache lines, so every load waits for
  memory.
- **System drive**: the Storage page's benchmark (sequential 1 MB and random
  4K, queue depth 1, unbuffered) on the Windows drive with a 256 MB temporary
  file that is deleted afterwards, also when cancelled.
- **Graphics compute**: the Overclock stress test's DirectX 11 compute load
  at full intensity without result checking, timed after a 3-second warm-up;
  throughput is reported in billions of shader steps per second. Skipped with
  the reason when there is no DirectX 11 card.
- Each timed test repeats its work for 12 seconds (10–20 in **Options**) and
  reports the median after dropping outliers (1.5 × the interquartile range);
  a note asks to run again when the repeats varied by more than 10 %. Scores
  are measured ÷ reference × 1,000 (inverted for latency); a test with several
  numbers and the **overall score** use the geometric mean.
- **Before a run** the page checks the power source, how busy the processor
  is (over one second; above 15 % is a warning) and CPU/GPU temperatures
  (80 °C or more). Warnings are shown in the confirmation and saved with the
  run, which shows them as a banner later.
- **Results**: summary tiles (overall score, change since the last run with
  the same tests, tests with a score, date) and one card per test with the
  score, a bar whose middle marker is the reference PC, the raw numbers, the
  number of repeats and their spread. **Run** on a card runs that test alone.
  **Cancel** keeps the tests that finished.
- **History**: every run is saved in `benchmarks.json` next to the settings
  (newest 100), with an optional note such as "Before overclock". **Show**
  puts an older run on the cards; **More → Remove this run** deletes one after
  confirming.
- **Compare two runs** side by side with the change in percent for each test
  and each raw value (changes under 3 % count as normal variation). The
  overall score is only compared when both runs completed the same tests, and
  notes say when the scoring, the hardware or the conditions differed.
- **More → Copy results / Save results as text / Copy comparison / Save
  comparison as text** produce plain text ready for a forum post.

The reference numbers are PC Checker's own estimates for its own workloads;
compare scores with other PC Checker runs, not with other benchmark apps.

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
About (version, licences, GitHub link, *Show welcome again*). A note shows when the app is running
in demo mode. Overlay options are on the Overlay page, logging options on the
Logs page and cleanup categories on the Cleanup page.

### Your setup (backup and restore)

**Settings → General → Your setup** saves everything you set up in PC Checker
to one `.pcchecker` file and brings it back on this PC after a reinstall, or on
a new one. *Back up your setup* and *Restore your setup from a file* are also
quick actions (Ctrl+K).

- **What the file holds**: every settings section of `settings.json` (theme,
  language, alerts and limits, restore points, updates, logging, cleanup,
  network and latency tests, history recording), fan profiles and curves,
  lighting profiles, the overlay layout and presets, game profiles, display
  profiles, the memory warning (the memory profiles themselves are built in),
  keyboard and mouse profiles, overclock limits and profiles, schedules and
  tray options, and, if you tick it, the benchmark history from
  `benchmarks.json`. The data looks exactly like the matching part of
  `settings.json`, inside a small versioned envelope (`Format`,
  `SchemaVersion`, and a `Version` per part).
- **Never included**: the Claude API key (Windows protects it with DPAPI for
  this account on this PC only, so it would not work elsewhere anyway; the
  dialog says so), the recorded History data and logs, and this PC's own state:
  window size, recent searches, the welcome flag, the log folder, the active
  fan, lighting and overclock profiles and the overclock consent. An import
  never takes these from a file either, even a hand-edited one.
- **Restore**: pick a file and PC Checker checks it first: is it JSON, is it a
  setup file, which version. Parts made by a newer version, parts it does not
  know and parts whose values are not in the expected form are listed as
  skipped with the reason; the rest can still be imported. A plain
  `settings.json` (for example a copy you kept, or the `settings.corrupt-*.bak`
  the app sets aside) is read as an older layout and converted. The preview
  shows each part with a tick box and what it would change ("General › Theme:
  System → Dark", "Profiles "Silent": added", "5 runs in the file, 3 new"),
  and parts that already match are left unticked. Overclock limits and
  profiles are never ticked by default.
- **How values are applied**: for each ticked part the file's values replace
  this PC's field by field; a field the file does not have keeps its current
  value. Values are clamped to the ranges the Settings page allows. Empty
  entries and unnamed profiles in a hand-edited file are left out, and empty
  text gets its default, so a damaged file cannot break a page. A shorter
  history retention gets the usual 24-hour grace period, so importing never
  deletes recorded history straight away. Benchmark runs are added (runs you
  already have are kept). The history holds the newest 100 runs, so if new
  runs would go over that the preview says how many of your oldest runs would
  be removed, and *Undo* puts them back. Imported overclock profiles are still checked
  against your limits and the card's own range when you apply one; nothing is
  applied to hardware by an import.
- **Schedules**: importing *Schedules and tray* creates, updates or removes
  PC Checker's Windows scheduled tasks and the *Start with Windows* task to
  match, through the same guarded path as the Schedules switches. A task that
  cannot be created is left as it was and the import says so. Import, reset
  and undo all list the task changes and ask before touching Task Scheduler.
  If the settings then cannot be saved, the tasks are put back as they were.
- **Backup first, undo after**: before every import (and every reset) the
  current setup, benchmark history included, is saved to
  `%LOCALAPPDATA%\PcChecker\backups\setup-<time>.pcchecker`. If that fails,
  nothing is changed. *Undo* puts the changed parts back from that backup and
  removes the benchmark runs the import added (and restores any it pushed
  out). Older backups stay in the folder (*Open backups folder*) and can be
  restored like any other file. The app never deletes them, so the folder
  grows by one full backup (benchmark history included) per import or reset;
  delete old ones by hand when you no longer need them.
- **Reset one part**: tick parts, such as only fans or only the overlay, to
  put them back to their defaults, with the same automatic backup and undo.
  The API key is kept.
- Every import, reset and undo is written to the Action Journal with the
  backup's path. Open pages (Fans, Lighting, Overlay, Displays, Keyboard &
  Mouse, Overclock, Benchmark) reload their lists after an import. In demo
  mode the automatic backups are kept in memory and two sample ones are listed.

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

## Quick search and welcome

### Quick search

Press **Ctrl+K** anywhere in the window, or click *Search* at the top of the
navigation pane, to open a search box over the page.

- **What it finds**: every page and tab, every option on the Settings page
  (plus a few that live on their own pages: the overlay hotkey and opacity,
  the logging level, hiding serial numbers in logs and the cleanup dry run),
  and quick actions: *Run a full scan*, *Find temporary files to clean*
  (opens Cleanup and scans; nothing is removed until you confirm), *Check for
  driver updates*, *Optimize this PC*, *Export a full report*, *Show or hide
  the overlay*, *Run a latency check*, *Run a disk benchmark*, *Check health
  now*, *Run a speed check*, *Check for PC Checker updates*, *Show the
  welcome again*, *Back up your setup* and *Restore your setup from a file*.
- **Matching**: case, accents and punctuation do not matter. It matches the
  whole title, the start of it, every word you type at the start of (or inside)
  a word, the initials ("wf" for Windows Features, "km" for Keyboard & Mouse)
  and letters in order from the start of a word. A page is also found by its
  section ("monitor power") and by everyday words ("uninstall" finds Apps,
  "junk" finds Cleanup). In a translated interface both the translated and the
  English words work.
- **Recent first**: the items you opened last (up to eight, kept in settings)
  are listed first when the box is empty, followed by the quick actions, and
  are lifted above similar matches while you type.
- **Keys**: Up and Down move the highlight, Page Up and Page Down jump, Enter
  opens, Esc (or a click outside the box) closes. Ctrl+K again also closes it.
- **What opening does**: a page or tab opens; a setting opens its page, opens
  the collapsed section it is in, scrolls it into view and flashes it briefly;
  a quick action opens its page and presses the same button you would, so its
  confirmation dialog and safety steps still apply (a disk benchmark, for
  example, still asks first). Nothing changes the PC without asking.

### Welcome

The first time PC Checker opens, a short welcome explains what it does in three
lines, lets you choose the language (it applies after a restart, with a
*Restart now* button) and explains Advanced mode with a switch for it. Choose
*Run a full scan* to start straight away or *Look around first* to close it.
It appears once: never in screenshot runs or `--scheduled` runs, and not for
people upgrading from an earlier version (their settings file already exists).
**Settings → About → Show welcome again** (or *Show the welcome again* in quick
search) brings it back.

## Accessibility

Every page works from the keyboard and with a screen reader, in the light and
dark themes and in Windows high-contrast mode.

- **Keys**: Ctrl+K quick search, F6 and Shift+F6 to move between the menu, the
  section tabs and the page, Ctrl+Tab and Ctrl+Shift+Tab (or Ctrl+Page Down
  and Ctrl+Page Up) for the next or previous tab, Esc to close a dialog, quick
  search or the tray popup, and Alt plus a letter for each page's main action
  (Alt+S *Scan*, Alt+R *Refresh* and so on). Read-only tables are one Tab stop
  with arrow keys inside; the fan curve is edited with the arrow keys, Insert
  and Delete.
- **Screen readers**: buttons, images, lists and tables have names; tooltips
  are read as help; decorative icons are skipped.
- **Colours**: status colours and small text meet WCAG AA contrast (4.5:1) on
  both themes, with deeper shades in the light theme. In high-contrast mode the
  app uses the Windows contrast colours throughout.

The full list of shortcuts, the colour table and the known gaps are in
[ACCESSIBILITY.md](ACCESSIBILITY.md).

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
