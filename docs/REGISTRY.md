# Registry checker

The **Registry** page (Software group) finds registry entries that point at
programs, files and folders that are no longer on the PC, and removes them
safely.

## What it can and cannot do

Registry "errors" rarely slow Windows down. A modern Windows install reads
only the keys it needs, and a few hundred stale entries make no measurable
difference to boot time or game performance. What leftovers *do* cause is
clutter and small annoyances:

- programs that are still listed in **Installed apps** but cannot be
  uninstalled because their uninstaller is gone;
- **Open with** menus offering programs that no longer exist;
- startup entries that Windows tries (and fails) to start at every sign-in;
- right-click menu entries of removed programs.

So the page is deliberately calm: it says how many leftovers it found and
what they are, never "1,000 problems". It is conservative (it never touches
anything it cannot prove is broken), it backs up every entry before changing
it, and every fix can be undone from the page.

## What it checks

| Category | Where it looks | Risk | Fix |
|---|---|---|---|
| Leftover uninstall entries | `…\CurrentVersion\Uninstall` (64-bit, 32-bit, per-user) | Safe | removes the key |
| Program shortcuts (App Paths) | `…\CurrentVersion\App Paths` | Safe | removes the key |
| Shared file counters | `…\CurrentVersion\SharedDLLs` | Moderate | removes the value |
| Broken file associations | `Software\Classes` ProgIDs and `Applications`, `Explorer\FileExts\…\OpenWithList` | Moderate (HKLM: advanced mode) | removes the key / value |
| Startup entries for missing programs | `…\CurrentVersion\Run`, `RunOnce` | Safe | removes the value |
| Orphaned COM components | `Classes\CLSID\…\InprocServer32 / LocalServer32` | Advanced, advanced mode only | removes the key |
| Program name cache (MUI cache) | `Classes\Local Settings\…\Shell\MuiCache` | Safe | removes the value |
| Missing font files | `Windows NT\CurrentVersion\Fonts` | Moderate | removes the value |
| Missing help files | `Windows\Help`, `Windows\HTML Help` | Safe | removes the value |
| Installer folder records | `…\Installer\Folders` | Moderate, advanced mode only | removes the value |
| Services with a missing program | `SYSTEM\CurrentControlSet\Services` | — | **report only** |
| Broken right-click menu handlers | `Classes\*`, `Directory`, `Folder`, `Drive` `\shellex\ContextMenuHandlers` | Advanced, advanced mode only | removes the handler key |

Details that keep the checks honest:

- **Uninstall entries** need a display name, are skipped when marked as a
  system component or published by Microsoft, and are only reported when both
  the uninstaller *and* the install folder are gone. When an entry does not
  record its install folder, the uninstaller's own folder proves nothing
  (installers often keep it in a cache folder that cleaners remove), so the
  entry is only fixed when its icon file elsewhere is gone too; otherwise it is
  listed as report only. Windows Installer (MSI) entries are only reported when
  the install folder is gone *and* Windows Installer has no cached package for
  the product.
- **File types** (ProgIDs) are only reported when no file extension uses them
  any more; Open with list entries only when the program's own registration
  points at a missing file.
- **COM components and menu handlers** are only reported for third-party
  files, never for anything under the Windows folder or Microsoft program
  folders, and only when every server file the component lists is missing.
- **Services** are never changed. A service whose program is gone usually
  means a program was deleted instead of uninstalled; reinstalling and
  uninstalling it, or the vendor's own removal tool, is the right fix.

## When is a file "missing"?

A target only counts as missing when it is an absolute path on a **local
fixed disk** that does not exist. These are never reported:

- network paths (`\\server\share\…`) and mapped network drives;
- removable drives (USB sticks, SD cards, optical drives) and drive letters
  that are not present right now;
- paths with environment variables that cannot be expanded;
- bare program names (`notepad.exe`) — they are resolved through System32,
  SysWOW64, the Windows folder and `PATH`, and are never reported when they
  cannot be found, because Windows may resolve them another way.

Paths under `Program Files` are also checked in `Program Files (x86)` (and
the other way round), and `System32` paths in `SysWOW64`, so 32-bit programs
registered with a 64-bit path (or vice versa) are not mistaken for missing.
Command lines are parsed with quotes, arguments, `%1` placeholders,
`rundll32 library.dll,Entry`, `@shell32.dll,-123` resource strings, icon
indexes (`app.exe,0`) and kernel-style paths (`\SystemRoot\…`, `\??\C:\…`).

## Locations that are never changed

Whatever a scan finds, PC Checker never changes:

- `HKLM\SYSTEM`, `HKLM\SAM`, `HKLM\SECURITY`, `HKLM\HARDWARE` and the boot
  configuration;
- anything under a `Policies` key, Winlogon, LSA, Session Manager
  (BootExecute), SafeBoot, Image File Execution Options and the AppInit
  settings;
- driver registrations (`Drivers`, `Drivers32`) and Windows Defender;
- the container keys the checks enumerate (for example `…\Uninstall` itself,
  `Classes\CLSID`, `Run`), and any key less than three levels deep.

## How a fix works

1. You tick entries (only fixable ones have a checkbox) and choose **Fix
   selected**. A confirmation explains what happens.
2. A **restore point** is created first (always on for this page, whatever
   the global restore-point setting), and the change is recorded in the
   Action Journal.
3. Every entry is **checked again**; anything that changed since the scan is
   skipped.
4. Every entry is **backed up**: whole keys with `reg export`, single values
   value by value. If an entry cannot be backed up it is skipped, never
   removed. All entries of one fix go into one file:
   `%LOCALAPPDATA%\PcChecker\registry-backups\registry-backup-<date>-<time>.reg`,
   with a `.json` file next to it that lists what was removed and why. A
   hash of both files is kept under
   `HKEY_LOCAL_MACHINE\SOFTWARE\PcChecker\RegistryBackups`, where only
   administrators can write. If PC Checker cannot record it, nothing is
   removed.
5. Each entry is checked once more and then removed (the key, or just the
   value). The result lists what was removed, skipped or failed.

## Undo

The **Backups** section lists every fix with its date, number of entries and
a summary. **Restore** imports the `.reg` file again with `reg import`
(after a confirmation and a new restore point); restored entries show up
again in the next scan. Because the backup folder is in your profile, where
any program you run can change it, PC Checker first checks both files against
the recorded hash and checks that the `.reg` file only touches the entries the
backup was made for. A backup that fails either check, or was made by an older
version without a hash, is not imported. **Delete** removes a backup, and backups older than
90 days are offered for cleanup — they are never deleted automatically. You
can also double-click a backup file in Explorer to import it, or use the
restore point from System Restore.

## Notes

- PC Checker runs as administrator. The `HKEY_CURRENT_USER` entries it checks
  are those of the account that started it.
- Keys Windows does not allow reading are skipped and counted in the scan
  notes.
- Demo mode (`--demo`) shows a sample machine with 23 leftovers; fixes and
  restores there only change the in-memory sample data.
