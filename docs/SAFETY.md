# Safety

PC Checker runs as administrator and can change real things on your PC. These
are the rules that every change in the app follows, so you know what to expect
and contributors know what to build.

## The four guarantees

1. **You always confirm first.** No uninstall, delete, service change,
   registry edit, feature toggle, driver install or overclock happens without a
   dialog that says what will change and asks you to confirm.
2. **A restore point comes first.** By default a System Restore point is
   created before any system change (Settings → Restore points). You can also
   create one after, or turn either off.
3. **Everything is journaled.** Each change is written to the Action Journal
   (Logs page) with the time, the target, the result, the restore point
   sequence number and how to undo it.
4. **Reversible where possible.** Optimisations have a *Revert*. Service and
   startup changes record the previous value. Overclocks reset to defaults on
   demand and automatically after an unclean shutdown.

## Cleanup rules

- Files are only ever deleted inside the roots of the categories you chose.
  A path outside those roots is refused even if it is listed.
- Reparse points (junctions, symbolic links) are never followed.
- Files modified in the last 60 minutes (configurable) are skipped; they may
  belong to a running program.
- `desktop.ini` and files that are locked are skipped.
- **Dry run** shows exactly what would be removed before anything is.
- Categories are labelled Safe, Moderate or Advanced. Moderate and Advanced
  ones (prefetch, crash dumps, Windows logs, Windows.old) explain the trade-off.

## Software rules

- Edge, WebView2, Defender, the .NET and Visual C++ runtimes and driver
  packages are protected. The app tells you why.
- Programs are uninstalled with their own uninstaller (MSI, Inno Setup, NSIS,
  InstallShield, Store), never by deleting folders.
- Bundle detection is a heuristic. It groups things that were installed
  together; it does not uninstall anything automatically.

## Optimisation rules

- Actions are rated Safe, Moderate or Advanced. Advanced actions are hidden
  until Advanced mode is on and show a stronger warning.
- Anything that needs a restart says so before you apply it.
- `DISM`, `sfc` and network reset are repair tools; they are not run as part
  of a normal scan.

## Overclocking rules

Overclocking can crash the system, corrupt data and, if limits are ignored,
damage hardware. PC Checker reduces the risk; it cannot remove it.

- A consent screen explains this and must be accepted once.
- Every profile is validated and **clamped** to the limits in Settings
  (core/memory offset, power limit, temperature limit, CPU temperature).
- The stress test watches temperatures and aborts above your thresholds.
- At apply time the app writes a session marker. If the PC was not shut down
  cleanly the next start resets the GPU to defaults and tells you.
- The Claude advisor produces a plan with small steps and a stress test after
  each. The app never applies a whole plan at once.
- CPU tuning only goes through vendor tools (Intel XTU, AMD Ryzen Master)
  when they are installed; the app does not write MSRs or BIOS settings.

## Driver rules

- Updates come from the Windows Update driver catalog through the Windows
  Update Agent, the same source Windows itself uses.
- Vendor links take you to the vendor site; the app does not download from
  third-party driver sites.
- You can back up all current drivers before updating and roll back a device
  from the Drivers page.

## What the app never does

- Run with elevated rights silently in the background
- Delete user documents, pictures, downloads or anything outside its category
  roots
- Change settings it did not show you first
- Send data anywhere without your knowledge (see [PRIVACY.md](PRIVACY.md))

## If something goes wrong

1. Open the Logs page and check the Action Journal for the last change.
2. Use *Revert* on the action if it has one.
3. Otherwise open Windows **System Restore** and pick the restore point PC
   Checker created ("PC Checker: before ...").
4. Export the logs and open an issue so it can be fixed.
