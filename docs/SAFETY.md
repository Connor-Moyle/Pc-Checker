# Safety

PC Checker runs as administrator and can change real things on your PC:
settings, services, drivers, software, the page file, boot configuration,
fan speeds and GPU clocks. These are the rules every change in the app
follows, so you know what to expect and contributors know what to build.

## The five guarantees

1. **You always confirm first.** No uninstall, delete, service change,
   registry edit, feature toggle, driver install, page-file change, `bcdedit`
   call, fan override, lighting profile or overclock happens without a dialog
   that says what will change and asks you to confirm.
2. **A restore point comes first.** By default a System Restore point is
   created before any system change (Settings → Restore points). You can also
   create one after, or turn either off. If System Protection is disabled the
   app tells you and offers to enable it.
3. **Everything is journaled.** Each change is written to the Action Journal
   (Logs page) with the time, the module, the target, the value before and
   after when known, the result, the restore point sequence number and how to
   undo it.
4. **Reversible where possible.** Optimisations have a *Revert*. Service,
   startup and input changes record the previous value. Fans and overclocks
   reset to defaults on demand and automatically after a crash.
5. **Watchdogs for anything that can hurt hardware.** Fan overrides and
   overclocks leave a session marker while active; an unclean shutdown
   triggers a reset on the next start, and overheating forces fans to 100 %.

## Debloat rules

- Each batch on the Debloat page is one guarded change that always starts with
  a restore point, whatever the global setting; app and feature removals in the
  batch share it, and a batch with one-way changes does not start without it.
  The values about to change are saved to `debloat-capture.json` first. *Revert* restores them; without a saved value it
  restores the documented Windows default and says so.
- Only documented registry values, policies, service start types, scheduled
  tasks and Store app removal are used. The single script (OneDrive uninstall)
  runs Microsoft's own `OneDriveSetup.exe /uninstall`; nothing is downloaded.
- Services are never deleted, only set to Manual or Disabled; the Print
  Spooler and Fax service are left on while a physical printer is installed.
- Never offered: disabling Defender, Windows Update, UAC or SmartScreen,
  removing Edge or the Store, stripping Windows components, hosts-file blocking
  or turning off CPU mitigations.
- Service, Windows Update, Xbox service and OneDrive removal tweaks only appear
  in Advanced mode.

## Cleanup rules

- Files are only ever deleted inside the roots of the categories you chose.
  A path outside those roots is refused even if it is listed.
- Reparse points (junctions, symbolic links) are never followed.
- Files modified in the last 60 minutes (configurable) are skipped; they may
  belong to a running program.
- `desktop.ini`, locked files and the app's own log folder are skipped.
- Browser caches are skipped while that browser is running.
- **Dry run** shows exactly what would be removed before anything is.
- Categories are labelled Safe, Moderate or Advanced. Moderate and Advanced
  ones (prefetch, crash dumps, Windows logs, Windows.old) explain the
  trade-off. Windows.old is only reported; removing it goes through Windows'
  own Storage settings or Disk Cleanup.

## Registry rules

- Only entries that provably point at something missing are reported: the
  target must be an absolute path on a local fixed disk. Network paths,
  removable or unplugged drives, unexpanded variables and bare program names
  are never flagged, and Program Files / System32 paths are checked in both
  their 64-bit and 32-bit folders.
- Microsoft and Windows components are skipped for program registrations,
  and services are report only.
- System hives (SYSTEM, SAM, SECURITY), policies, Winlogon, LSA, boot and
  driver settings, Windows Defender and the container keys themselves are
  never changed.
- Every fix creates a restore point, writes a `.reg` backup first (an entry
  that cannot be backed up is skipped), re-checks the entry right before
  removing it and is recorded in the Action Journal. Backups are restored
  from the Registry page.

## Software rules

- Edge, WebView2, Defender, the .NET and Visual C++ runtimes and driver
  packages are protected. The app tells you why. Advanced mode can override
  the protection for non-critical items, with a warning.
- Programs are uninstalled with their own uninstaller (MSI, Inno Setup, NSIS,
  InstallShield, Squirrel, Store), never by deleting folders.
- Bundle detection is a heuristic. It groups things that were installed
  together; it does not uninstall anything automatically.

## Startup and boot rules

- Removing a startup shortcut moves it to
  `%LOCALAPPDATA%\PcChecker\backup\startup\` instead of deleting it; removing
  a registry value records the value in the journal.
- Critical Windows services (RPC, DCOM, WMI, Event Log, DHCP, DNS client,
  Plug and Play, Power, the firewall, Defender, Task Scheduler and others)
  cannot be disabled from the app.
- *Safe mode next boot* and *Restart to UEFI* are confirmed, explain the way
  back, and the restart has a countdown you can cancel.

## Memory rules

- The in-app memory test only uses memory that is currently free and never
  more than the percentage you choose. It is a user-mode test; the page says
  that a boot-time test (Windows Memory Diagnostic) is more thorough.
- Page-file and `bcdedit` changes are confirmed, journaled with the previous
  values, and tell you when a restart is needed.
- Clearing the standby list is harmless; Windows refills it.

## Storage rules

- The surface scan opens the drive **read-only** and never writes.
- The benchmark writes one temporary file in a folder you can see, with a
  name starting `PcChecker-bench-`, and deletes it even if the test is
  cancelled or fails.
- `chkdsk /f /r` on the system drive cannot run while Windows is using the
  drive; the app schedules it for the next restart only after a dialog that
  explains the restart and the time it can take.

## Fan rules

- Manual speeds are clamped to the minimum in Settings (default 20 %) unless
  you explicitly allow lower. Stopping a fan is never the default.
- If any temperature crosses its critical threshold while a manual or curve
  profile is active and the fan is below 100 %, the app forces 100 % and
  raises an alert.
- Fans revert to automatic control when the app exits (configurable) and,
  if the app crashed while controlling fans, on the next start.
- The app only touches fans that LibreHardwareMonitor exposes a control for;
  it never guesses at embedded-controller registers.

## Optimisation and latency rules

- Actions are rated Safe, Moderate or Advanced. Advanced actions are hidden
  until Advanced mode is on and show a stronger warning.
- Anything that needs a restart says so before you apply it.
- `DISM`, `sfc` and network reset are repair tools; they are not run as part
  of a normal scan.
- Latency fixes that change boot configuration (HPET, dynamic tick), TCP
  behaviour (Nagle) or interrupt mode (MSI) are Advanced, reversible, and
  each explains the cases where it helps and where it does not.

## Overclocking rules

Overclocking can crash the system, corrupt data and, if limits are ignored,
damage hardware. PC Checker reduces the risk; it cannot remove it.

- A consent screen explains this and must be accepted once.
- Every profile is validated and **clamped** to the limits in Settings → Overclock limits
  (core and memory offset, power limit, temperature limit, CPU temperature)
  and to the limits the driver itself reports.
- The app refuses to apply a profile while the GPU or CPU is already above
  the warning temperature.
- The stress test watches temperatures and aborts above your thresholds,
  resetting the GPU if a profile is active.
- At apply time the app writes a session marker. If the PC was not shut down
  cleanly the next start resets the GPU to defaults and tells you.
- AMD (ADL/Overdrive8) and Intel Arc (IGCL) tuning are marked experimental.
  Every value is checked against the driver's own minimum and maximum; any
  SDK failure drops the feature to monitor-only and offers the vendor's
  software instead.
- CPU tuning only goes through vendor tools (Intel XTU, AMD Ryzen Master,
  ryzenadj) when they are installed; the app does not write MSRs or BIOS
  settings. If a tool's output does not match what the app expects, it
  refuses rather than guessing.
- The Claude advisor produces a plan with small steps and a stress test after
  each. Every step is clamped before you see it, and the app never applies a
  whole plan at once.

## Driver rules

- Updates come from the Windows Update driver catalog through the Windows
  Update Agent, the same source Windows itself uses.
- Vendor links take you to the vendor site; the app does not download from
  third-party driver sites.
- A `.inf` install only happens for a file you picked explicitly (Advanced).
- You can back up all current drivers before updating. Roll back works from
  the app when it can map the device to its driver package; otherwise it
  opens Device Manager and explains *Roll Back Driver*.

## Lighting rules

Lighting changes are cosmetic and cannot damage hardware, so they are
journaled but do not create restore points. The app never installs or
modifies OpenRGB; it only launches it and talks to its SDK server on the
local machine.

## Windows features and uninstall cautions

- Removing the Xbox app breaks Game Bar; the Store is marked *do not
  remove*; SMB 1.0 carries a security note. Each inbox app has a risk label.
- Disabling Hyper-V, Virtual Machine Platform or WSL stops virtual machines,
  Docker Desktop and Windows Sandbox from working.
- Store apps can be reinstalled from the Microsoft Store; the journal says so.

## What the app never does

- Run with elevated rights silently in the background, or install a service
- Delete user documents, pictures, downloads or anything outside its category
  roots
- Change settings it did not show you first
- Apply a hardware value outside the limits in Settings or the driver's range
- Send data anywhere without your knowledge (see [PRIVACY.md](PRIVACY.md))

## If something goes wrong

1. Open the Logs page and check the Action Journal for the last change. Every
   entry has an undo hint.
2. Use *Revert* on the action if it has one, or apply the matching default
   profile (Fans → *Revert all to auto*, Overclock → *Reset to defaults*,
   Keyboard & Mouse → *Windows default*, Memory → *Balanced*).
3. Otherwise open Windows **System Restore** (`rstrui.exe`, also linked from
   the Logs and Settings pages) and pick the restore point PC Checker created
   ("PC Checker: before …").
4. If Windows will not start after a boot-configuration change, use the
   recovery environment: *Troubleshoot → Advanced options → Startup Settings*
   or *Command Prompt*, then `bcdedit /deletevalue {current} safeboot` (safe
   mode) or `bcdedit /deletevalue useplatformclock` (HPET).
5. Export the logs and open an issue so it can be fixed.
