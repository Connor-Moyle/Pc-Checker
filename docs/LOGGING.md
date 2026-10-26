# Logging and the Action Journal

PC Checker logs what it does so you can see what happened, send a useful bug
report and undo changes.

## Where logs live

`%LOCALAPPDATA%\PcChecker\logs` by default, as `pcchecker-<date>.log` (or
`pcchecker-<date>.json` when JSON format is on). Change the folder in
**Logs → Logging settings** or **Settings → Logging**. The Action Journal is
`%LOCALAPPDATA%\PcChecker\actions.jsonl` next to the settings file.

## Settings

| Setting | Default | Notes |
|---|---|---|
| Minimum level | Information | Verbose, Debug, Information, Warning, Error, Fatal |
| Per-category levels | (none) | Override the level for one area, for example Sensors → Debug |
| Write to file | On | Turn off to keep logs in memory only |
| Rolling | Day | New file every hour, day, or never |
| File size limit | 10 MB | A new file starts when this is reached |
| Files kept | 14 | Older files are deleted |
| JSON format | Off | One JSON object per line instead of text, for log tools |
| Output template | Serilog default | Customise the text layout |
| In-app buffer | 2,000 entries | How much the live viewer keeps |
| Redact serial numbers | Off | Mask serial numbers and MAC addresses in logs, reports, exports and assistant context |
| Log sensor samples | Off | Very chatty; only for diagnosing sensor problems |

Changes apply immediately without a restart.

## Categories

Each area of the app logs under its own category so you can tune or filter
them: Hardware, Sensors, Health, Drivers, Software, Performance,
Optimization, Cleanup, Features, Restore, Overclock, Power, Updates,
Assistant, Overlay, UI and App. The newer modules log under the category that
matches their folder (Storage, Memory, Displays, Audio, Input, Fans,
Lighting, Latency, Startup); anything unmatched falls back to App.

## Live viewer

The **Logs** page shows entries as they happen. Filter by level, category or
text, pause the stream, clear the buffer, copy selected lines, open the log
folder, and **Export diagnostics** to produce a zip to attach to a bug
report. The zip contains:

- every log file in the log folder
- the Action Journal
- your settings with the Claude API key removed
- `system-summary.txt` with the OS version and app version

The same redaction setting applies to the zip.

## Action Journal

Every system change made through the app is recorded with:

- when it happened and which module made it
- the action and its target (program name, service, setting, file category,
  fan, display, device)
- the value before and after when known
- whether it is reversible and a hint on how to undo it
- the outcome (succeeded, failed with reason, cancelled)
- the System Restore point sequence number created before the change

The journal is shown on the Logs page under **Actions** with the detail for
each entry, *Open journal file* and *Open System Restore*. The Dashboard shows
the last five entries. The file is append-only JSON Lines and rotates at 5 MB.

## For developers

Services get an `ILogger<T>` through dependency injection. The log category is
derived from the namespace folder, so `PcChecker.Core.Sensors.*` logs under
*Sensors*. The pipeline is Serilog with a file sink, a debug sink and an
in-memory sink that feeds the live viewer; it is bridged to
`Microsoft.Extensions.Logging` so nothing outside `ILogService` depends on
Serilog. Reconfiguring swaps the logger atomically; existing `ILogger<T>`
instances pick up the new configuration.

Do not log secrets, full command lines containing tokens, or user file
contents. Log at Information when a guarded change starts and finishes (with
elapsed time), at Warning when a Windows API is unavailable and the feature
degrades, and at Error with the exception when something unexpected happens.
