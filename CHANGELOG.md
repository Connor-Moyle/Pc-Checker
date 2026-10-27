# Changelog

All notable changes to PC Checker are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **Task manager → Services**: every Windows service with its display name,
  service name, description, status, startup type (Automatic, Automatic
  (Delayed), Manual, Disabled), log-on account, PID and group; search and
  filters (running, stopped, other companies only, disabled); start, stop,
  restart, pause, continue and change the startup type; open the Services
  console or the file, search online, go to the process, and show what the
  service depends on and what depends on it. Core Windows services are locked
  with a plain reason, a known background service gets a "safe to set to
  Manual" note, and every action is confirmed and journaled; a startup-type
  change records the previous type so it can be set back. Services are read
  through WMI and changed through the Service Control Manager APIs, never by
  parsing `sc.exe` text.
- **Task manager → Users**: everyone signed in to the PC (user, session,
  state, client and sign-in time) with each person's summed processor and
  memory use; switch user (locks to the sign-in screen), disconnect, sign
  another user out (confirmed, administrator) and send a short on-screen
  message, using the Remote Desktop Services (WTS) APIs.
- **Replace Task Manager**: an option in Settings → General and on the
  Processes *More* menu that makes Ctrl+Shift+Esc and every other way of
  opening Task Manager open PC Checker on the Processes tab instead, exactly
  as Process Explorer does it, through the Image File Execution Options
  Debugger value. It is only offered from a Program Files install (so a
  user-writable copy cannot take Task Manager's elevated slot), is confirmed
  and journaled with any previous value, and turning it off restores that
  value. A leftover setting pointing at a PC Checker that no longer exists is
  cleaned up automatically at the next start, and one pointing at another copy
  is offered to be pointed at this one. *Open Windows Task Manager* on the
  Processes *More* menu turns the option off and opens Task Manager. A new
  `--taskmgr` command line opens straight on the Processes tab, also when PC
  Checker is already running.
- **PawnIO from inside the app**: the welcome has a ticked *Install PawnIO*
  choice, later starts ask once when the sensor driver is missing (*Install*,
  *Not now* for a week, or *Don't ask again*), and Settings → Monitoring has
  a *Sensor driver (PawnIO)* row with its status, Install or Repair, and
  Remove. Nobody has to find and install the driver by hand to see the CPU
  and motherboard fans.
- **Windows Update → Roll back**: lists installed updates (KB, title, date,
  type) and removes the one you pick behind a restore point, with wusa or,
  for the combined servicing stack and cumulative update, DISM and its
  RollupFix package; servicing stack updates are refused with the reason.
  *Hide this update* keeps a pending or removed update from being offered or
  reinstalled (the same flag as Microsoft's "Show or hide updates" tool), and
  *Hidden updates* shows them again. After a feature update, *Go back* returns
  to the previous version of Windows while the window lasts (days left shown,
  two confirmations, restart offered) and *Keep the option for* extends the
  window up to 60 days. Results come from exit codes, so they read the same
  on every Windows language. The Cleanup page's Windows.old entry now warns
  that removing it, including through Storage Sense, ends going back.
- **Update prompt**: when GitHub has a newer release, PC Checker asks with
  *Update now*, *Later* or *Skip this version* and shows what is new. *Update
  now* downloads the new PcChecker.exe, checks its SHA-256 and restarts into
  it. Checks run when the app opens and every 6 hours while it runs, also
  from the tray (as a notification). Settings → Updates → *When a new version
  is available* chooses between asking, downloading first and then asking to
  restart, or just telling you. The Dashboard banner has an *Update now*
  button.
- **Check-up** (Home → Check-up): one button runs the health check, a cleanup
  scan of the Safe categories, the driver and Windows update checks, startup
  analysis, the speed check, the Recommended debloat settings and the
  old-driver scan with one progress view, and turns the results into a single
  ranked to-do list. Each item says why it matters, what fixing it brings,
  its risk and its page, and offers *Fix* where the owning page has a safe
  automatic fix or *Open* otherwise. *Fix selected* applies the ticked fixes
  one service at a time behind one shared restore point and reports a result
  per item; Moderate and Advanced fixes are never ticked automatically.
- **Dashboard**: a Windows Update tile (restart pending, updates waiting,
  paused or up to date), CPU and GPU temperature sparklines for the last 24
  hours from History, the fullest drive from Disk space and a check-up strip
  with *Run check-up*.
- **Battery** (Hardware → Battery): battery health, charge cycles and full
  charge against design capacity, how long a full charge lasts now and when
  new, a capacity history chart, the last three days of use and tips such as
  charge limits, all read from WMI and Windows' battery report (parsed from
  its language-independent XML). *Open the full report* saves Windows' HTML
  battery report to Documents and opens it. Desktops show "This PC has no
  battery", with a note for UPS battery backups.
- **Leftovers** (Apps & Drivers → Leftovers): find folders, empty folders,
  registry keys, services, scheduled tasks and Start menu shortcuts that
  removed programs left behind, each with the reason, size and a
  High/Medium/Low confidence (only High is ticked). Shared and system folders
  are never reported and links are never followed. Removal creates a restore
  point, moves files to the Recycle Bin, backs registry keys up to `.reg` and
  tasks to XML first, deletes services only when stopped, and a restore list
  puts keys and tasks back. The Apps page offers *Find leftovers* right after
  an uninstall.
- **Your setup** (Settings → General, also in quick search): back up every
  setting and profile (fans and curves, lighting, overlay presets, game,
  display and keyboard and mouse profiles, overclock limits and profiles,
  schedules and tray options, and optionally the benchmark history) to one
  `.pcchecker` file, and restore it on this or another PC. The import checks
  the file, previews what each part changes with a tick box per part, skips
  unknown or newer parts with a note, saves an automatic backup of the current
  setup first and offers *Undo*. *Reset one part* puts single parts back to
  their defaults. The Claude API key is never exported or imported.
- **Accessibility pass** over the whole app (no new page; see
  [docs/ACCESSIBILITY.md](docs/ACCESSIBILITY.md)): every icon-only button,
  image, list and data table has a screen-reader name, other controls take
  theirs from the label beside them, tooltips are read as help text and
  decorative icons are hidden from screen readers. Keyboard: Esc closes
  dialogs and the tray popup, F6 moves between the menu, the tabs and the page,
  Ctrl+Tab switches tabs, read-only tables are one Tab stop with arrow keys
  inside, the section tabs show a focus ring, the fan curve can be edited with
  the arrow keys, and each page's main action has an Alt access key. Colours:
  the light theme gets its own deeper status colours, badge text and small
  notes meet WCAG AA contrast in both themes, and Windows high-contrast mode
  switches the whole app to the system colours.
- **Debloat** catches up with current WinUtil, Win11Debloat, ShutUp10++,
  Sophia Script, AtlasOS, ReviOS, Tiny11 and xd-AntiSpy releases: Edge AI and
  more Edge nag policies, Settings Home and account ads, phone suggestions,
  desktop Spotlight, Find my device, the full location service, more
  telemetry policies and tasks, personal-data app permissions, settings sync,
  Remote Assistance, Windows preferences (dark mode, Sticky Keys shortcut,
  snapping, Alt+Tab, taskbar combining, Num Lock), Start and File Explorer
  options, Office, Outlook, Chrome, Brave, Firefox, developer-tool and NVIDIA
  telemetry (only when installed), WPBT and device co-installer blocks,
  Reserved Storage, Modern Standby networking, and more than 110 more removable
  apps including PC-maker apps. AMD, NVIDIA, Intel and audio driver control
  panels can never be removed. See `docs/DEBLOAT.md`.
- **Cleanup** covers far more: AMD, NVIDIA and Intel driver leftovers, logs
  and web caches; Opera, Opera GX, Vivaldi, Chromium, Waterfox, LibreWolf,
  Floorp, Zen, Arc and Yandex caches; Discord, Teams, Spotify, Office, VS
  Code, Slack, Zoom, OBS, Adobe, Steam, Epic, Battle.net, EA, Ubisoft and GOG
  caches; developer caches (Advanced); Store app temp files, Windows
  Installer leftovers, Remote Desktop cache and old CHKDSK fragments; opt-in
  privacy items (recent items, jump lists, clipboard, browser cookies,
  history and sessions); and Windows' own tools for Windows Update cleanup
  (DISM), Disk Cleanup items, upgrade leftovers, Windows.old and older
  restore points. Categories are grouped with a filter, skip themselves while
  their app runs or a restart is pending, and keep excluded data such as
  Spotify downloads and the AMD chipset uninstaller.
- **AMD support**: the discrete GPU is used everywhere instead of the
  integrated Radeon of Ryzen 7000/9000; Ryzen-aware temperature limits
  (95 °C by design, Tdie over Tctl, separate GPU hot spot and memory limits);
  dual-CCD X3D advice that keeps Balanced, core parking and Game Bar; Health
  checks for AMD Chipset Software, the 3D V-Cache optimizer, WHEA hardware
  errors and graphics driver timeouts; EXPO detection from part numbers with
  Infinity Fabric guidance; Radeon undervolt, presets and Overdrive8 fan
  curve on the Overclock page; ryzenadj only on laptops and APUs; Windows
  Update driver offers that would replace AMD Software are unticked and can
  be hidden. Debloat adds AMD & NVIDIA software tweaks (offered only when that
  software is installed), and the AMD and NVIDIA installer-folder cleanup also
  waits for versioned driver self-extractors started from a download folder.

### Fixed
- **Debloat tweaks that did not seem to apply.** Batches that need a restore
  point now ask (turn on System Protection, continue without, or cancel)
  instead of stopping with every tweak failed; per-user tweaks go to the
  account signed in to Windows when PC Checker runs as another administrator;
  every tweak is read back after a batch and shows *Applied*, *Already
  applied*, *Skipped* or *Failed* with the reason, plus whether it shows after
  a File Explorer restart, a sign-out or a Windows restart; a number stored as
  text no longer counts as applied; and a briefly locked undo file no longer
  fails the tweak. See DEBLOAT.md, *Troubleshooting*.
- **Drivers from more than Windows Update**: *Check for updates* now also
  asks NVIDIA's driver service (Game Ready or Studio, by the card's device
  id), reads the current AMD Adrenalin release from AMD's driver page, links
  Intel graphics, Wi-Fi and Bluetooth to their Intel download pages, and finds
  the PC maker's update tool (Dell, HP, Lenovo, ASUS, MSI, GIGABYTE) with
  *Open*, *Install with winget* or the support page for your exact model.
  Results are one list grouped by device with each source's newest version,
  release date and size, the newest marked, and the source chosen per device.
  NVIDIA and AMD packages download only from the makers' own HTTPS servers,
  run only when their signature is valid and signed by the maker, and install
  after a confirmation and a restore point. Each source can be switched off
  under *Update sources*.
- **Drive temperatures**: a cool SSD could show about 70 °C. The NVMe warning
  and critical limits the drive reports were read as temperatures, and the
  health check could take a hotter controller sensor ("Temperature 2") as the
  drive temperature. Every page, the tray, the overlay, reports and alerts now
  use the drive's own (composite) temperature. Extra sensors are listed as
  *Sensor N* or *Controller* with their own, higher limits; SMART 194 uses only
  its current-temperature byte and wins over 190; values of 0, 255 and 65535
  count as "not reported".
- **Fans page only showed the graphics card fans**: the CPU and case fans on
  the motherboard are read through the PawnIO driver, and without it the page
  listed the GPU fans with no word about the missing ones. The Fans and
  Temperatures pages and the Check-up now detect whether PawnIO is installed
  and running (by opening its device, not only from the uninstall entry, so
  an install during the session is seen) and offer *Install PawnIO*: winget
  when available, otherwise the official signed setup from GitHub, checked
  with WinVerifyTrust before it runs silently. Afterwards the sensors reload
  without restarting the app and the page says how many motherboard fans
  appeared. *Reload sensors* returns the fans to automatic first.
- Fan speed sensors without a matching control are listed as read-only with
  their RPM (and refresh every second even when no fan is controllable), a
  control is no longer paired with another header's speed sensor by position
  (it is shown as a control without RPM reading instead), and a numbered
  header is no longer guessed to be the CPU fan. Headers that read 0 RPM are
  hidden behind *Show empty headers*.
- New *Why is a fan missing?* panel on the Fans page with the PawnIO state,
  the motherboard and fan controller chip, every fan and control sensor,
  running fan and RGB software that fights over the chip, plain-English
  reasons a fan cannot be controlled, and *Copy diagnostic report*.
- The Temperatures page no longer claims the motherboard sensor chip was
  found when only the board name was read, and its *Retry* no longer leaves
  the Fans page driving fans through a closed hardware monitor.
- Cleanup now finds shader caches where current AMD (`Dx9Cache` and every
  other `*Cache` folder, Store and Game Pass games), NVIDIA
  (`PerDriverVersion`, `ComputeCache`) and Intel drivers write them; Windows
  temp on 24H2 (`SystemTemp`); GPU timeout dumps in `LiveKernelReports`;
  Chromium Service Worker and shader caches and beta channels; more Firefox
  caches and Windows logs. The font cache service is paused so its files can
  go, prefetch keeps entries used in the last 30 days, and the docs match
  the catalog.

## [0.4.0] - 2026-10-27

Six new tools: Disk space, Processes, History, Benchmark, Windows Update and quick search.

### Added
- **Disk space** (Clean up → Disk space): see what fills a drive or folder
  with a fast, cancellable scan, drill into folders sorted by size, and list
  the largest files, a breakdown by file type and big files unused for a
  year. A duplicate finder confirms identical files by SHA-256 and can tick
  every copy but the newest or oldest. Selected items go to the Recycle Bin
  after a confirmation with their size; Windows, program and profile folders
  are never offered, and every move is journaled.
- **Processes** (Monitor › Processes): a live list of running programs with
  CPU, memory, disk, GPU, publisher and signature, user and start time, a
  plain-English *what is this?* for more than 120 well-known processes,
  sorting, search and grouping by app, and informational flags for programs
  that may be unwanted. End task, end process tree and set priority are
  confirmed and journaled; critical Windows processes are never offered.
- **History** (Monitor → History): PC Checker records CPU and GPU
  temperature peaks, loads, power draw, memory use, free space per drive and
  health scores in the background, also while it sits in the tray. Pick the
  last hour, day, week, 30 or 90 days to see a chart per metric with the
  value under the mouse, notable events (temperature peaks above your limits,
  sudden drops in free space, falling health scores) and export the range as
  CSV. Older days are rolled up to 15-minute and hourly points and deleted
  after 90 days (Settings → History); nothing leaves the PC.
- **Benchmark** (Performance → Benchmark): processor single- and multi-core,
  memory bandwidth and latency, system drive and graphics compute tests scored
  against a mid-range 2024 desktop (1,000 per test) with an overall score.
  Warns before a run on battery, when hot or when the processor is busy,
  saves every run, compares two runs side by side and exports results as text.
- **Windows Update** tab under Apps & Drivers: status (last check and
  install, pending restart, pause, active hours, policies), pending software
  updates with KB, size, type and severity, installed one at a time behind a
  restore point with per-update results and plain-English error texts, the
  last 100 installs with failures explained, pause for 1–5 weeks and resume
  the way Settings does, active hours, and links to the Settings pages.
  Settings managed by Group Policy or device management are shown read-only.
- **Quick search**: Ctrl+K, or *Search* at the top of the navigation pane,
  finds any page, tab or setting and runs quick actions (full scan, clean
  temporary files, check drivers, optimize, export a report, toggle the
  overlay, latency check, disk benchmark and more). Fuzzy matching on words
  and initials, recent items first, and translated titles match too. Choosing
  a setting scrolls it into view.
- **Welcome**: a short introduction on the first start with the language
  choice, Advanced mode explained, and *Run a full scan* or *Look around
  first*. Bring it back from Settings → About → *Show welcome again*.

### Fixed
- Interface text containing special characters such as an ellipsis is now
  translated too; it was listed under a garbled key before.

## [0.3.0] - 2026-10-27

Six languages and a safety review of everything that changes your PC.

### Added
- **Languages**: German, Spanish, French, Italian and Brazilian Portuguese as
  well as English. Choose one in Settings → Appearance → Language, or follow
  the Windows display language; it applies after a restart. Pages, tabs,
  buttons, tooltips, dialogs and notifications are translated; scan results
  and log lines are still in English. See `docs/TRANSLATING.md`.

### Changed
- Sensors and fan control use LibreHardwareMonitor 0.9.6 with the signed
  PawnIO driver, because Windows blocks the older WinRing0 driver. The
  Temperatures and Fans pages explain how to install PawnIO when it is
  missing.

### Fixed
- A safety review of every action that changes the system fixed 62 problems
  that would only show on a real PC, among them:
  - driver *Roll back* now refuses protected or shared drivers and backs the
    package up first; driver update checks use the Windows Update service;
  - forgetting a Wi-Fi network with a wildcard-looking name can no longer
    delete every saved network; network tuning and Cloudflare DNS keep each
    adapter's previous settings (IPv6 too) so *Revert* restores them;
  - power plans, powercfg, chkdsk and winget output are read correctly on
    non-English Windows, with no duplicate power plans;
  - overclock and fan sessions survive failed resets and crashes until
    Windows restarts, partly applied Intel XTU profiles roll back, and
    ryzenadj is only taken from folders that only administrators control;
  - quiet uninstalls never restart the PC, temp files are re-checked when
    deleted, registry backups are verified before they are restored, and
    the page file is restored exactly on *Revert*;
  - updates are verified again just before they install, and scheduled
    tasks only run a PcChecker.exe that only administrators can change.
- The FAQ no longer says Windows on Arm is unsupported; it has its own build.
- Sensors and fan control use LibreHardwareMonitor 0.9.6, which reads the CPU
  and motherboard through the signed PawnIO driver instead of WinRing0, which
  Microsoft Defender quarantines. When PawnIO is not installed, the
  Temperatures and Fans pages say so and how to install it.
- After a crash while it controlled fans, PC Checker no longer reports
  motherboard fans as back on automatic: the board keeps them at the last
  manual speed until the PC restarts, so the app keeps driving them on the
  Balanced curve with its overheating protection on, and says to restart. If
  the fans cannot be found at startup, it says it could not confirm them and
  keeps the record for the next start instead of discarding it.
- The fan loop can no longer undo *Revert all to automatic* or a new manual
  speed that was set while a loop tick was running.
- Power plan names are no longer garbled on non-English Windows.
- With *Revert to automatic on exit* off, the app no longer claims fans were
  left under its control after it closed: the motherboard always takes the
  fans back when PC Checker closes, and the setting only re-applies the
  active profile at the next start. Its description in Settings says so.

## [0.2.0] - 2026-10-27

New tools and a calmer layout on every page.

### Added
- **Network** (its own section): Wi-Fi analysis with signal, band, channel
  congestion and link-rate checks; a speed test with download, upload,
  loaded latency (bufferbloat), jitter and packet loss; and Wi-Fi tuning
  (DNS choice, DNS over HTTPS, adapter power saving, roaming and band
  preferences, saved-network clean-up) with a *Quick improve* that measures
  before and after. Every change can be reverted.
- **Registry** (under Clean up): scans for broken file associations, missing
  shared DLLs, dead uninstall entries, orphaned startup and App Paths
  entries and similar faults. Fixes are backed up to a `.reg` file first and
  can be restored.
- **Debloat** (under Clean up): privacy, telemetry, ads and suggestions,
  Copilot and Recall, and built-in app removal, collected from the well-known
  debloating tools into one reviewed list with presets. Each tweak shows what
  it changes and can be reverted. See `docs/DEBLOAT.md`.
- **Old drivers** tab in Apps & Drivers: finds driver packages that a newer
  version replaced, packages no connected device uses, and drivers of devices
  that are no longer connected (`pnputil /enum-devices /disconnected`). Each
  finding is marked Safe to remove, Probably safe or Keep (in use, part of
  Windows, or a class Windows needs to start), with its size in the driver
  store and the total space you can free. Removal confirms first, runs through
  the change guard (restore point, Action Journal), exports every package to
  `%LOCALAPPDATA%\PcChecker\driver-backups\<time>` before deleting it, never
  forces a package off a device and reports each item.
- **Overlay FPS counter**: new metrics *FPS*, *Frame time* and *1% low* for
  the game in front, measured from the DirectX present events through a
  real-time event tracing session (the PresentMon approach), with a kernel
  fallback for OpenGL and Vulkan games.
- **Game profiles** on the Overlay page: per-game overlay preset and
  visibility, fan profile and power plan, applied automatically when the game
  is in front and put back when it closes.
- **Tray**: an icon next to the clock with CPU/GPU temperatures in the tooltip
  (optionally the hottest temperature drawn as the icon), a compact popup
  (temperatures, load, RAM, power, alerts, fan-profile switch, overlay toggle,
  quick clean, exit), a right-click menu, minimize and close to the tray,
  *Start with Windows* through a logon task with highest privileges (no UAC
  prompt), the `--tray` switch and temperature alerts as notifications.
- **Schedules**: health checks, Safe-only cleanup, driver update checks and
  HTML reports on a daily or weekly timetable through Windows Task Scheduler
  (`--scheduled <job>`), with notifications only when something needs
  attention, last results, *Run now* and *Fix tasks* after the exe moved.

### Changed
- The Drivers page header keeps *Scan drivers* and *Check for updates*; the
  other actions (and the new *Old and unused drivers…*) moved to a *More* menu.
- **Every page tidied**: each page has at most one main and one secondary
  button with the rest under *More*, summary tiles in even rows, one-line
  help with details in tooltips or collapsed *About* sections, and options
  and advanced settings collapsed at the end. Settings is grouped into
  titled cards.
- The GPU stress test loads the graphics card itself with a built-in
  DirectX 11 compute workload, so it no longer needs a game running.
- Screenshots are named after their page (`fans.png`), so links survive
  pages moving.

### Fixed
- Closed status bars no longer leave a blank band under page headers.


## [0.1.1] - 2026-10-27

A tidier layout, based on feedback that the app felt cluttered.

### Changed
- **Navigation**: the side menu is down from 26 entries to 12. Pages are
  grouped into sections (Home, Monitor, Hardware, Devices, Cooling & RGB,
  Apps & Drivers, Clean up, Performance, Gaming, Ask Claude, Logs,
  Settings). A section with several pages shows them as tabs above the
  content.
- **Dashboard**: live readings lead in an even row, health and alerts sit side
  by side, and secondary actions moved into a *More* menu. The hardware
  support table is now a collapsed section.
- Stat tiles line up in equal columns at any window size.

### Fixed
- The side menu no longer runs under the *Advanced mode* switch on smaller
  windows.

## [0.1.0] - 2026-10-26

Initial release.

### Added
- **Dashboard**: health score, live CPU/GPU temperature, memory and power
  tiles, alerts strip, quick actions (full scan, clean up, check drivers,
  optimize, export report), controls row (fans, lighting, displays, audio),
  hardware-support summary and recent activity from the Action Journal.
- **Temperatures**: live readings for every sensor LibreHardwareMonitor can
  see (CPU, GPU, drives, motherboard/VRM, memory, PSU, AIO), gauges,
  min/max/average, sparklines, thresholds and alerts, pause and reset.
- **Power**: per-component watts, estimated total, battery discharge,
  session energy and cost, power plan switching.
- **Overlay**: click-through in-game overlay with a metric picker, position,
  opacity, scale, fonts, colours, presets and a global hotkey.
- **Hardware**: full inventory with typed fields and the raw property bag for
  every device, search, JSON export.
- **Health**: disk SMART, battery wear, device problems, event-log errors,
  reliability history, crash dumps, memory-diagnostic results, Windows Update
  state, free space, thermal and security basics, combined into one score.
- **Storage**: read-only surface scan (quick or full) with a live block map,
  disk benchmark (sequential and random 4K), `chkdsk` scan or scheduled repair,
  `Repair-Volume`, TRIM/defragment, disk error events, vendor tool links.
- **Memory**: overview (usage, commit, standby, compression, SysMain, page
  files, modules, XMP/EXPO state, channel layout), in-app memory test, Windows
  Memory Diagnostic scheduling, standby-list clearing, memory profiles and a
  manual page-file editor.
- **Displays**: arrangement preview, identify, resolution, refresh rate,
  orientation, primary display, HDR, brightness and contrast (DDC/CI or laptop
  panel), display profiles, links to Windows and GPU control panels.
- **Audio**: output and input devices with volume, mute, live level meters,
  default and communications device selection, per-app volume mixer, audio
  service restart and troubleshooter links.
- **Keyboard & Mouse**: pointer speed, Enhance pointer precision, double-click,
  scroll, trails, keyboard repeat and blink, accessibility toggles, profiles,
  a mouse polling-rate meter and vendor software links.
- **Fans**: fan detection through LibreHardwareMonitor, manual speed, custom
  curves with hysteresis and ramp limiting, Silent/Balanced/Performance
  profiles, revert-to-automatic on exit and after a crash, overheat failsafe.
- **Lighting**: RGB control through OpenRGB (motherboards, RAM, GPUs,
  keyboards, mice, coolers, strips) with per-device modes, colours, zones,
  brightness and speed, sync-all, profiles and vendor software links.
- **Drivers**: installed-driver scan with age, signature and problem flags,
  Windows Update driver catalog check, download and install, driver backup,
  `.inf` install, rollback guidance and vendor links.
- **Software**: installed programs and Store apps, sizes, locations,
  "installed alongside" bundle detection, protected components, quiet or
  normal uninstall.
- **Startup**: startup apps, logon tasks and auto-start services with
  enable/disable/remove/add, boot options (fast startup, boot menu timeout,
  safe mode next boot, restart to UEFI or advanced startup).
- **Windows Features**: optional features, capabilities and inbox apps with
  risk labels and one-click enable, disable or remove.
- **Cleanup**: junk-file scanner by category with dry run and strict safety
  rules.
- **Performance**: slowdown scan with prioritised findings, startup programs,
  services, top processes and a system snapshot.
- **Optimize**: reversible tweaks grouped by risk, including actions
  contributed by the Latency, Memory, Startup and Input pages.
- **Latency**: DPC/ISR sampling, timer resolution, HPET and dynamic tick,
  HAGS, power-plan and USB/PCIe power-saving checks, mouse polling rate,
  ping and DNS tests, scored findings with fixes.
- **Overclock**: NVIDIA tuning through NVAPI, experimental AMD (ADL/Overdrive8)
  and Intel Arc (IGCL) tuning, Intel XTU and AMD Ryzen Master integration,
  XMP/EXPO detection, a support matrix, profiles, a stress test with a
  temperature watchdog, automatic revert after an unclean shutdown and a
  consent gate.
- **Ask Claude**: optional assistant that answers questions about this PC
  using the scan results, explains findings and drafts a staged overclocking
  plan. Bring your own API key.
- **Logs**: live log viewer, logging settings, diagnostics export and the
  Action Journal with undo hints.
- **Settings**: theme, advanced mode, updates, restore points, monitoring
  thresholds, overclock limits, fans, lighting, latency, memory, startup and
  the Claude connection (overlay, logging and cleanup options live on their
  own pages).
- **Safety**: every change confirmed first, restore point before changes,
  Action Journal with undo hints, overclock and fan watchdogs.
- **Demo mode** (`--demo`) with realistic sample data and a screenshot runner
  (`--screenshots <dir>`) that CI uses to capture every page.
- Released as one self-contained `PcChecker.exe` (no installer). It updates
  itself from GitHub Releases with Stable and Beta channels and verifies every
  download against its published SHA-256.

[Unreleased]: https://github.com/Connor-Moyle/Pc-Checker/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/Connor-Moyle/Pc-Checker/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Connor-Moyle/Pc-Checker/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Connor-Moyle/Pc-Checker/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/Connor-Moyle/Pc-Checker/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Connor-Moyle/Pc-Checker/releases/tag/v0.1.0
