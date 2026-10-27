# Frequently asked questions

### Is it free?

Yes. PC Checker is open source under the MIT licence. The optional Claude
assistant uses your own Anthropic API key, which Anthropic bills separately.

### Why does it ask for administrator rights?

Reading SMART data, controlling fans, installing drivers, cleaning system
folders, changing services, boot configuration and the page file, scheduling
`chkdsk` and tuning the GPU all need them. The app does not install a
background service. Nothing runs while it is closed unless you turn on a
schedule or *Start with Windows* in Settings; both use Windows Task Scheduler
with highest privileges, which is why they start without a UAC prompt. For
the same reason they can only be turned on when PcChecker.exe sits in a folder
only administrators can change, such as `C:\Program Files\PC Checker`.

### Can PC Checker keep running in the tray or run checks on a schedule?

Yes. Settings → Tray lets the minimize or close button hide PC Checker to the
tray, where its icon shows temperatures and opens a compact popup. Settings →
Schedules runs health checks, Safe-only cleanup, driver checks and reports
daily or weekly, even while the app is closed, and only notifies you when
something needs attention. See [FEATURES.md](FEATURES.md#schedules).

### Windows SmartScreen says "Windows protected your PC"

Releases are not yet signed with a certificate SmartScreen trusts. Click
**More info → Run anyway**. You can verify the download came from this
repository's Releases page by comparing its SHA-256 with the one printed in
the release notes (`Get-FileHash .\PcChecker.exe` in PowerShell).

### Does it send my data anywhere?

No, unless you turn on the Claude assistant and send a message. The other
network calls are the update check (GitHub), driver downloads through Windows
Update, and the Latency page's ping and DNS tests when you run them with
*include network* on. See [PRIVACY.md](PRIVACY.md).

### Which Windows versions work?

Windows 11 and Windows 10 22H2, 64-bit. Windows on Arm laptops (Snapdragon)
use `PcChecker-arm64.exe`; sensors, fan control and GPU tuning are limited
there because the hardware drivers they rely on are x64-only. Older Windows
versions are not supported.

### Will it break my PC?

Every change is confirmed first, creates a restore point and is journaled with
an undo hint. Fans and overclocks have watchdogs that reset them after a
crash. Read [SAFETY.md](SAFETY.md) for the full rules. If something goes
wrong, the restore point the app made is the way back.

### No temperatures are showing

- CPU, motherboard and fan sensors need the free, signed **PawnIO** driver
  (the sensor library moved to it because Windows blocks the older WinRing0
  driver). If it is missing, the Temperatures and Fans pages say so and link
  to [pawnio.eu](https://pawnio.eu); install it once and restart PC Checker.
  GPU and drive temperatures work without it.
- The sensor driver needs administrator rights.
- CPU temperatures and the motherboard sensor chip (case and CPU fan headers)
  are read through the signed **PawnIO** driver, which is installed
  separately. If it is missing, the Temperatures page says so; install it from
  [pawnio.eu](https://pawnio.eu) or with `winget install namazso.PawnIO`, then
  restart PC Checker. It works with core isolation (memory integrity) on.
- Another monitoring tool (one that ships with a motherboard, HWiNFO, an RGB
  suite) can hold the sensor interface. Close it and click *Retry*.
- Some laptops and OEM desktops expose very few sensors. Virtual machines
  report almost nothing.

### My SSD shows a high temperature

- PC Checker shows the drive's own temperature: the NVMe *composite*
  temperature, or SMART attribute 194 on SATA drives. That is the number the
  drive itself uses to decide when to slow down.
- Many NVMe drives have extra sensors. One is often the controller, which
  normally runs 10–25 °C hotter than the drive. The Temperatures page lists
  these as *Sensor 1*, *Sensor 2* or *Controller* with "extra sensor" under the
  name. They have their own limits (the drive limits plus 15 °C) and never
  count as the drive temperature.
- Drives also report a warning and a critical limit (often 70 °C and 80 °C).
  These are limits, not readings, and are not shown as temperatures.
- Earlier versions could show one of those limits or the controller sensor
  as the drive temperature. Update if a cool drive reads about 70 °C.
- Still high? Compare with the drive maker's tool. Drives without airflow, or
  under a graphics card, really can run at 60–70 °C while copying large files.

### The Fans page says no controllable fans were found

The app can only drive fans that LibreHardwareMonitor exposes a control for:
motherboard headers on supported Super I/O chips (most enthusiast desktop
boards), NVIDIA and AMD GPU fans and a few coolers. Laptops and most OEM
desktops keep the fans behind an embedded controller that software cannot
reach. AIOs and fan hubs that only speak a vendor USB protocol (iCUE, NZXT
CAM, Lian Li) need the vendor's software. Motherboard headers also need the
PawnIO driver (see *No temperatures are showing*). See
[SUPPORTED-HARDWARE.md](SUPPORTED-HARDWARE.md).

### Lighting says "OpenRGB not running" or "not installed"

RGB control goes through [OpenRGB](https://openrgb.org), a free, open-source
program that supports hundreds of motherboards, RAM kits, GPUs, keyboards,
mice and coolers. Install it, open it once, turn on **Settings → SDK Server**,
and then click *Connect* (or *Launch OpenRGB server*) on the Lighting page.
Devices OpenRGB does not support cannot be controlled from PC Checker; the
page links the vendor suites as an alternative. Close vendor RGB software
while OpenRGB runs, as they fight over the hardware.

### Can it overclock AMD or Intel graphics?

AMD GPUs (ADL/Overdrive8) and Intel Arc (IGCL) are supported as
**experimental**: implemented against the vendors' public SDKs, with every
value validated against the limits the driver reports, but not yet verified on
many cards. If the SDK is missing or any call fails, the page becomes monitor
only and offers to open AMD Software: Adrenalin Edition or Intel Graphics
Software instead. NVIDIA cards are tuned through NVAPI. Please report what
works on your card.

### Can it overclock my CPU?

Only through the vendor's own tools. On Intel it uses the Intel XTU
command-line client when XTU is installed; on AMD it launches Ryzen Master
and can use `ryzenadj` for power limits when that tool is present. The app
never writes MSRs or BIOS settings itself. Core ratios and voltages for most
desktops are set in BIOS; the page explains the steps.

### My RAM runs slower than it is rated for

That is XMP (Intel) or EXPO (AMD) being off. It can only be enabled in BIOS:
restart, enter BIOS (usually `Del` or `F2`), find *XMP*, *EXPO*, *D.O.C.P.* or
*A-XMP* under the overclocking or memory section, choose Profile 1, save and
restart. The Memory page shows the rated and configured speeds and the same
steps.

### The overlay does not show in my game

The game is probably in exclusive full-screen mode. Switch it to borderless
window. Some anti-cheat systems also block overlays. See
[OVERLAY.md](OVERLAY.md).

### How do I undo something?

Open the Logs page → **Action journal**. Selecting a change shows how to
undo it; the Logs page itself does not revert anything. Reverts are done on
the page that made the change: Fans have *Revert all to auto*, Overclock has
*Reset to defaults*, Keyboard & Mouse has the *Windows default* profile and
Memory has the *Balanced* profile. For anything else, open System Restore
(linked from the Logs and Settings pages) and pick the point PC Checker
created.

### What does the health score mean?

It starts at 100 and loses points for each issue found, weighted by how
serious it is: failing SMART, high battery wear, device problems, repeated
system errors, crash dumps, low free space, missing updates and high
temperatures. The page lists every deduction.

### Is the memory test as good as MemTest86?

No. The in-app test writes and verifies patterns in memory that is currently
free, from inside Windows, so it cannot test the memory Windows itself is
using. It catches obvious faults quickly. For a thorough test use *Schedule
Windows Memory Diagnostic* on the Memory page (it runs before Windows loads)
or a bootable tester.

### The surface scan is taking hours

A full read of a large hard disk takes hours by nature (4 TB at 200 MB/s is
about six hours). Use *Quick scan* first; it reads the start and end of the
disk plus random samples in a few minutes. Run a full scan overnight if the
quick scan or SMART data shows a problem.

### A program is marked "protected", how do I remove it?

Edge, WebView2, Defender, the .NET and Visual C++ runtimes and driver
packages are protected because other software depends on them. Advanced mode
lets you override the protection for non-critical items, with a warning.

### Can I remove old drivers?

Yes, from **Apps & Drivers → Old drivers**. A scan lists driver versions that a
newer one replaced, driver packages nothing connected uses, and drivers of
devices you unplugged long ago, with the space each one takes. Items marked
*Safe to remove* are ticked for you; *Probably safe* ones are up to you; *Keep*
items (in use, part of Windows, or drivers Windows needs to start, such as
storage, chipset, keyboard and mouse) can only be ticked in Advanced mode.
Before a package is removed it is copied to
`%LOCALAPPDATA%\PcChecker\driver-backups`, so you can put it back with
*Drivers → More → Install .inf*. Windows refuses to remove a driver a device
still uses, and PC Checker never forces it.

### What does the Latency page actually measure?

It samples the CPU's DPC and interrupt time over a few seconds, reads the
timer resolution and boot settings (HPET, dynamic tick), checks power-saving
options that add latency (USB selective suspend, PCIe link power management,
core parking, the power plan), checks HAGS, Game Mode and pointer precision,
measures your mouse polling rate, and pings your gateway and public DNS. It is
a snapshot and a checklist, not a real-time tracer; for that use LatencyMon
or Windows Performance Analyzer (linked on the page).

### Can I change my monitor's brightness from the app?

On external monitors, yes, if the monitor answers DDC/CI (most do; some need
it enabled in the monitor's own menu, and some ignore it). On laptops the app
uses the panel's own brightness control. If the sliders are hidden, the
tooltip says why.

### Can I use PC Checker in my language?

Yes, in German, Spanish, French, Italian and Brazilian Portuguese as well as
English. Choose it in **Settings → Appearance → Language** and restart the
app. Pages, buttons, tooltips and dialogs are translated; scan results and log
lines are still in English. See [TRANSLATING.md](TRANSLATING.md) to improve a
translation.

### Where are the settings and logs?

`%LOCALAPPDATA%\PcChecker\settings.json`,
`%LOCALAPPDATA%\PcChecker\logs` and `%LOCALAPPDATA%\PcChecker\actions.jsonl`.
Delete the folder to reset the app.

### How does PC Checker update itself?

It checks GitHub Releases when it opens and every 6 hours while it runs. When
there is a newer version it asks: **Update now** downloads the new
`PcChecker.exe`, checks it against the SHA-256 published with the release and
restarts into it; **Skip this version** stops it offering that version; **Later**
asks again next time. Change this in **Settings → Updates**, or turn the checks
off there. Nothing installs without your say-so unless you choose that.

### Can I run it without installing?

Yes, that is the only way it ships. Download `PcChecker.exe` from Releases and
run it from any folder; there is nothing to install. It updates itself in
place when a new version is out.

### Can I look around without changing anything?

Run `PcChecker.exe --demo`. Every page fills with sample data for a fictional
gaming PC and no action touches your machine. See [DEMO-MODE.md](DEMO-MODE.md).

### Can I use it on a work PC?

Check with your IT department. Some actions (features, services, drivers, boot
configuration) may be controlled by policy and will fail with an explanation.

### How do I report a bug or a device that is not detected?

Export diagnostics from the Logs page and open a
[bug report](https://github.com/Connor-Moyle/Pc-Checker/issues/new?template=bug_report.yml)
or a
[driver or hardware issue](https://github.com/Connor-Moyle/Pc-Checker/issues/new?template=driver_or_hardware_issue.yml).
