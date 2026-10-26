# Logging and the Action Journal

PC Checker logs what it does so you can see what happened, send a useful bug
report and undo changes.

## Where logs live

`%LOCALAPPDATA%\PcChecker\logs` by default. Change the folder in
**Settings → Logging**. The Action Journal is `actions.jsonl` next to the
settings file.

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
| Redact serial numbers | Off | Mask serial numbers and MAC addresses in logs, reports and assistant context |
| Log sensor samples | Off | Very chatty; only for diagnosing sensor problems |

Changes apply immediately without a restart.

## Categories

Each area of the app logs under its own category so you can tune or filter
them: Hardware, Sensors, Health, Drivers, Software, Performance, Optimization,
Cleanup, Features, Restore, Overclock, Power, Updates, Assistant, Overlay, UI
and App.

## Live viewer

The **Logs** page shows entries as they happen. Filter by level, category or
text, pause the stream, clear the buffer and **Export** a zip of the log
folder to attach to a bug report.

## Action Journal

Every system change made through the app is recorded with:

- when it happened and which module made it
- the action and its target (program name, service, setting, file category)
- the value before and after when known
- whether it is reversible and a hint on how to undo it
- the outcome (succeeded, failed with reason, cancelled)
- the System Restore point sequence number created before the change

The journal is shown on the Logs page under **Actions** with a *Revert*
button where the app can undo the change itself.

## For developers

Services get an `ILogger<T>` through dependency injection. The log category is
derived from the namespace folder, so `PcChecker.Core.Sensors.*` logs under
*Sensors*. The pipeline is Serilog with a file sink, a debug sink and an
in-memory sink that feeds the live viewer; it is bridged to
`Microsoft.Extensions.Logging` so nothing outside `ILogService` depends on
Serilog. Do not log secrets, full command lines containing tokens, or user
file contents.
