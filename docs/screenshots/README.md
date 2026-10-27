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

One file per page, named after the page (for example `dashboard.png`,
`fans.png`, `overclock.png`), plus `overlay.png` for the in-game overlay. The
names stay the same when pages move, so links to them keep working. The list
of pages comes from `src/PcChecker.App/Services/NavigationCatalog.cs`.

`index.json` maps each section and page to its file and records the capture time
and window size (1440 × 900).

See [DEMO-MODE.md](../DEMO-MODE.md) for how to capture screenshots yourself.
