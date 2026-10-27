# Security policy

PC Checker runs with administrator rights. It can change system settings,
remove software, install drivers, change fan speeds, write to the page file
configuration and apply GPU clock offsets. A security problem in this app is
therefore a problem on your whole PC, and we treat reports accordingly.

## Supported versions

| Version | Supported |
|---|---|
| Latest release on the Stable channel | Yes |
| Latest pre-release on the Beta channel | Yes, until the next Stable release that contains the fix |
| Older releases | No. Please update first; the app can update itself from **Settings → Updates**. |

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on the repository page, or go straight to
<https://github.com/Connor-Moyle/Pc-Checker/security/advisories/new>.

Include:

- the version affected (Settings → About, or the title bar)
- Windows version and build (`winver`)
- steps to reproduce or a proof of concept
- what an attacker could achieve, and whether it needs local access, an
  existing administrator session or user interaction

You will get an acknowledgement within 7 days and a status update at least
every 14 days after that. When a fix is ready we publish a release, mention the
fix in `CHANGELOG.md` and credit you in the advisory unless you prefer
otherwise.

## What is in scope

- Making PC Checker delete, change, install or apply something the user did
  not confirm in a dialog
- Bypassing the `IChangeGuard` path (restore point, journal, confirmation) for
  a system-changing action
- Path traversal, junction or symbolic-link tricks that make Cleanup, Storage
  or Startup touch files outside their allowed roots
- Command or script injection through program names, uninstall strings, file
  names, service names, device names, OpenRGB device names or any other data
  read from the system and passed to `cmd`, PowerShell, `bcdedit`, `powercfg`,
  `chkdsk`, `sc`, `pnputil` or `shutdown`
- Overclocking: applying GPU or CPU values outside the limits in Settings,
  bypassing the consent gate, or defeating the stress-test watchdog and the
  unclean-shutdown revert
- Fan control: leaving fans at a dangerous manual speed after the app exits or
  crashes (the app reverts to automatic control on exit and on the next start)
- Driver installation: installing anything that did not come from the Windows
  Update driver catalog through the Windows Update Agent, or from a `.inf`
  the user picked explicitly
- Memory: page-file or `bcdedit` changes applied without confirmation
- Leaking the Claude API key or other secrets from settings, logs, reports or
  the diagnostics zip
- Update tampering: downloading or applying an update that was not published
  by this repository's GitHub Releases
- Privilege escalation through the single-instance mutex, the hotkey window,
  the overlay window or the OpenRGB socket

## What is out of scope

- Hardware damage caused by values the user explicitly chose inside the
  documented limits (that is a safety topic; see `docs/SAFETY.md`)
- Vulnerabilities in third-party software the app only launches or links to
  (OpenRGB, vendor tuning suites, Windows components)
- Issues that require the attacker to already have administrator rights on the
  machine and gain nothing beyond that

## Design notes

- Secrets are protected with Windows DPAPI scoped to the current user and are
  never written to logs, reports or the diagnostics zip.
- Updates are fetched only from this repository's GitHub Releases, and a
  downloaded `PcChecker.exe` is used only if its SHA-256 matches the one GitHub
  publishes for the release asset (or the one in the release notes).
- Every system change runs through `IChangeGuard`: a confirmation dialog, a
  restore point (per settings), an Action Journal entry and a log line.
- Cleanup never follows reparse points and refuses paths outside the category
  roots. Storage surface scans open drives read-only.
- External processes are started with explicit argument lists, never through
  a shell with concatenated strings.
- The only network calls are the update check, Windows Update driver
  downloads, the Latency page's ping and DNS tests, and (only when configured)
  the Claude API. See `docs/PRIVACY.md`.
