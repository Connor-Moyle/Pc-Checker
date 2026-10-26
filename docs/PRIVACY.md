# Privacy

PC Checker is a local tool. Scans run on your PC and the results stay on your
PC unless you choose to share them. There is no telemetry, no analytics, no
crash reporting service and no account.

## What stays local

- Hardware, health, sensor, driver, software, performance, storage, memory,
  display, audio, input, fan, lighting and latency data
- Settings: `%LOCALAPPDATA%\PcChecker\settings.json`
  (demo mode uses `%LOCALAPPDATA%\PcChecker\demo\settings.json`)
- Logs: `%LOCALAPPDATA%\PcChecker\logs\pcchecker-*.log` (or `.json`)
- The Action Journal: `%LOCALAPPDATA%\PcChecker\actions.jsonl`
- Watchdog markers: `%LOCALAPPDATA%\PcChecker\oc-session.json` and
  `fan-session.json` (only while an overclock or fan override is active)
- Startup-item backups: `%LOCALAPPDATA%\PcChecker\backup\startup\`
- Profiles (fans, lighting, displays, input, overclock, overlay presets):
  inside `settings.json`
- Exported reports, driver backups, JSON exports and diagnostics zips:
  wherever you save them

Delete the `%LOCALAPPDATA%\PcChecker` folder to reset the app completely.

## When the app connects to the internet

| Purpose | Where | When |
|---|---|---|
| Update check | GitHub Releases for this repository (through Velopack when installed, or the GitHub REST API for the portable build) | On start if *Check for updates automatically* is on, or when you click *Check now* |
| Driver updates | Windows Update, through the Windows Update Agent | Only when you click *Check for updates* on the Drivers page, and *Download* / *Install* when you choose them |
| Latency check | ICMP ping to your default gateway, `1.1.1.1`, `8.8.8.8` and any targets you add in Settings; one DNS lookup of `www.microsoft.com` to time your resolver | Only when you run a latency check with *include network* on |
| Claude assistant | Anthropic API | Only when you have entered an API key and send a message, ask for an explanation or request an overclock plan |

Nothing else. In particular:

- Vendor links (driver pages, storage tools, peripheral software, OpenRGB
  download) open your browser; the app does not fetch those pages itself.
- The Lighting page talks to OpenRGB over a socket on your own PC
  (`127.0.0.1:6742` by default). You can point it at another machine in
  Settings, in which case that traffic goes where you sent it.
- Demo mode makes no network calls at all.

## The Claude assistant

The assistant is optional and off until you add an API key.

- When *Share scan context* is on, a text summary of your scan results is
  sent with your message so the answers are specific to your PC. The *Context
  being shared* expander on the Ask Claude page shows the exact text before
  you send; the size limit is in Settings.
- *Redact serial numbers* (Settings → Logging) also strips serial numbers and
  MAC addresses from what is sent.
- The hardware profile sent to the overclock advisor never includes serial
  numbers.
- Your API key is stored protected with Windows DPAPI for your user account
  and is never written to logs, reports or the diagnostics zip.
- The app also honours the `ANTHROPIC_API_KEY` environment variable when no
  key is stored.

Anthropic's handling of API traffic is covered by their
[privacy policy](https://www.anthropic.com/privacy). Full detail in
[CLAUDE-INTEGRATION.md](CLAUDE-INTEGRATION.md).

## Reports, exports and logs

Reports and the Hardware page's JSON export contain the same data the app
shows, including serial numbers unless *Redact serial numbers* is on. Review a
report before sharing it. The diagnostics zip contains the log files, the
Action Journal, your settings with the API key removed and a short system
summary; the same redaction applies. Screenshots taken with
`--screenshots --real` show whatever the pages show.

## Permissions

The app asks for administrator rights because reading SMART data, controlling
fans, installing drivers, cleaning system folders, changing services and
boot configuration and applying GPU clocks require them. It does not install
a service or anything that runs when the app is closed. The global overlay
hotkey is registered only while the app is running.
