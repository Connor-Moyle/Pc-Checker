# Demo mode and screenshots

PC Checker can run without touching the real machine. Demo mode swaps every
service for one that returns realistic sample data, which is useful for
exploring the UI, writing documentation, reproducing a layout problem and
capturing screenshots on a build server that has no sensors, fans or GPU.

## Running in demo mode

```powershell
PcChecker.exe --demo
```

or from a source checkout:

```powershell
dotnet run --project src/PcChecker.App -- --demo
```

What you get:

- Every page is populated with data for a fictional gaming desktop: Intel
  Core i7-13700K, 32 GB DDR5-6000 (2 × 16 GB G.Skill), NVIDIA GeForce RTX
  4070 12 GB, Samsung 990 PRO 2 TB NVMe plus a WD 4 TB hard disk, ASUS ROG
  STRIX Z790-E, Windows 11 24H2.
- Sensors move (a gentle sine wave with noise), scans take a few hundred
  milliseconds so busy states are visible, and actions "succeed" without
  doing anything.
- Demo mode uses its own settings file
  (`%LOCALAPPDATA%\PcChecker\demo\settings.json`), so nothing you change in it
  affects your real settings.
- The Settings page shows a *demo mode* note so you cannot forget which mode
  you are in.
- The administrator prompt still appears, because the manifest asks for it
  before the command line is read.
- The tray icon and its popup work as normal. Settings → Schedules shows sample
  results for every job, but nothing is added to Task Scheduler ("Demo mode:
  schedules are not created"). *Run now* runs the job on sample data; a demo
  report is written to `%LOCALAPPDATA%\PcChecker\demo\reports`.

Nothing in demo mode reads WMI, the registry, drivers, OpenRGB or the
network, and nothing writes outside the demo settings folder.

## Capturing screenshots

```powershell
PcChecker.exe --screenshots screenshots --demo
```

`--screenshots <dir>` implies `--demo` unless you also pass `--real`. The app:

1. Sizes the main window to 1440 × 900.
2. Visits every page in navigation order, waits for the page to finish
   loading plus 400 ms, and renders the window to
   `<dir>\<nn>-<page>.png`.
3. Shows the overlay window in demo mode and captures it over a dark gradient
   as `<dir>\<nn>-overlay.png`.
4. Opens the tray popup off-screen and captures it above a desktop backdrop
   with a taskbar strip as `<dir>\<nn>-tray.png`.
5. Writes `<dir>\index.json` mapping page titles to file names, with the
   capture time and window size.
6. Exits with code 0, or 1 if anything failed (the reason is on stderr and in
   the log).

The file names are:

```
01-dashboard.png        02-temperatures.png     03-power.png
04-overlay-settings.png 05-hardware.png         06-health.png
07-storage.png          08-memory.png           09-displays.png
10-audio.png            11-input.png            12-fans.png
13-lighting.png         14-drivers.png          15-software.png
16-startup.png          17-windows-features.png 18-cleanup.png
19-performance.png      20-optimize.png         21-latency.png
22-overclock.png        23-assistant.png        24-logs.png
25-settings.png         26-overlay.png          27-tray.png
index.json
```

A screenshot run skips the single-instance check and creates no tray icon, so
it can run next to a normal instance, and never shows an error dialog that would block an
unattended run.

### In CI

`.github/workflows/ci.yml` has a `screenshots` job that runs the published
build on a Windows runner with a 10-minute timeout and uploads the folder as
the `screenshots` artifact. `.github/workflows/screenshots.yml` does the same
on demand and whenever app code changes on `main`, and commits the PNGs to
`docs/screenshots/` with the message "Update screenshots" when they changed.
The README embeds those files.

### Capturing the real app

`PcChecker.exe --screenshots screenshots --real` captures your actual machine.
Review the images before sharing them: they show serial numbers, program
lists and your network layout exactly as the pages do.

## Other switches

| Switch | Effect |
|---|---|
| `--demo` | Sample data, separate settings file |
| `--real` | Force real services even when `--demo` or `--screenshots` is present |
| `--screenshots <dir>` | Capture every page (implies `--demo`); `<dir>` defaults to `screenshots` |
| `--minimized` | Start minimized (same as the setting) |
| `--tray` | Start hidden with only the tray icon (used by *Start with Windows*) |
| `--scheduled <job>` | Run one maintenance job without a window and exit: `health`, `cleanup`, `drivers` or `report` (used by Settings → Schedules) |

Switches are case-insensitive and accept `--key value` or `--key=value`.
Unknown switches are ignored with a warning in the log.

## For contributors

Each service interface in `src/PcChecker.Core/<Area>/` has a `Windows*`
implementation and a `Demo*` implementation; `Common/DemoData.cs` holds the
shared facts about the fictional PC. `ServiceRegistration.Register` in
`src/PcChecker.App/Services/ServiceRegistration.cs` picks one or the other
from the `AppMode`. If you add a page, give its view model an
`InitializeAsync` override that loads its default data so demo mode and the
screenshot runner show it populated.
