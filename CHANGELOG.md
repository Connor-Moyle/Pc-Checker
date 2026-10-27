# Changelog

All notable changes to PC Checker are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Nothing yet.

### Changed
- Nothing yet.

### Fixed
- Nothing yet.

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
- Self-update from GitHub Releases (Velopack) with Stable and Beta channels,
  an installer and a portable build.

[Unreleased]: https://github.com/Connor-Moyle/test-pcchecker/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Connor-Moyle/test-pcchecker/releases/tag/v0.1.0
