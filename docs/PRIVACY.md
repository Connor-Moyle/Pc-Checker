# Privacy

PC Checker is a local tool. Scans run on your PC and the results stay on your
PC unless you choose to share them.

## What stays local

- Hardware, health, sensor, driver, software, performance and cleanup data
- Settings (`%LOCALAPPDATA%\PcChecker\settings.json`)
- Logs and the Action Journal (`%LOCALAPPDATA%\PcChecker\logs`)
- Exported reports (wherever you save them)

There is no telemetry, no analytics and no account.

## When the app connects to the internet

| Purpose | Where | When |
|---|---|---|
| Update check | GitHub Releases for this repository | On start if *Check for updates automatically* is on, or when you click *Check now* |
| Driver updates | Windows Update (through the Windows Update Agent) | Only when you click *Check for updates* on the Drivers page |
| Claude assistant | Anthropic API | Only when you have entered an API key and send a message or ask for a plan |

Vendor links on the Drivers page open your browser; the app does not fetch
those pages itself.

## The Claude assistant

The assistant is optional and off until you add an API key.

- When *Share scan context* is on, a summary of your scan results is sent with
  your message so the answers are specific to your PC. You can see the summary
  size limit in Settings and turn sharing off.
- *Redact serial numbers* (Settings → Logging) also strips serial numbers and
  MAC addresses from what is sent.
- The hardware profile sent to the overclock advisor never includes serial
  numbers.
- Your API key is stored protected with Windows DPAPI for your user account
  and is never written to logs or reports.

Anthropic's handling of API traffic is covered by their
[privacy policy](https://www.anthropic.com/privacy).

## Reports and logs

Reports contain the same data the app shows, including serial numbers unless
*Redact serial numbers* is on. Review a report before sharing it. Log exports
are zip files of plain text; the same redaction applies.

## Permissions

The app asks for administrator rights because reading SMART data, installing
drivers, cleaning system folders and changing services require them. It does
not install a service or anything that runs when the app is closed.
