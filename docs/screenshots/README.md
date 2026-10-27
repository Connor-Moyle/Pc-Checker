# Screenshots

This folder holds real screenshots of the app, captured automatically.

The PNGs are produced by the `Screenshots` workflow
(`.github/workflows/screenshots.yml`), which runs the published build on a
Windows runner with `PcChecker.exe --screenshots screenshots --demo`, and
commits the result here when the images changed. The `CI` workflow captures
the same set on every push and uploads it as the `screenshots` artifact
without committing.

They are taken from **demo mode**, which shows realistic sample data for a
fictional gaming desktop, so they contain nothing from any real machine. They
are refreshed whenever app code changes on `main` (and on demand); until the
first Windows run has completed this folder contains only this file. Screenshots are never added here by hand.

The file names come from `src/PcChecker.App/Services/ScreenshotRunner.cs`:

```
01-dashboard.png        02-temperatures.png     03-power.png
04-overlay-settings.png 05-hardware.png         06-health.png
07-storage.png          08-memory.png           09-displays.png
10-audio.png            11-input.png            12-fans.png
13-lighting.png         14-drivers.png          15-software.png
16-startup.png          17-windows-features.png 18-cleanup.png
19-performance.png      20-optimize.png         21-latency.png
22-overclock.png        23-assistant.png        24-logs.png
25-settings.png         26-overlay.png          index.json
```

`index.json` maps each page title to its file and records the capture time
and window size (1440 × 900).

Design mockups, which are *not* screenshots, live in
[`docs/mockups/`](../mockups/README.md). See [DEMO-MODE.md](../DEMO-MODE.md)
for how to capture screenshots yourself.
