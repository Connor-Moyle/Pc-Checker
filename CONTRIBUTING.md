# Contributing to PC Checker

Thanks for taking the time to help. Bug reports, hardware reports, feature
ideas, documentation fixes and code are all welcome.

## Before you start

- Read the [architecture overview](docs/ARCHITECTURE.md) so you know where
  things live.
- Read [SAFETY.md](docs/SAFETY.md). PC Checker changes real systems; every
  destructive action has rules it must follow.
- Search the [issue tracker](https://github.com/Connor-Moyle/test-pcchecker/issues)
  and [Discussions](https://github.com/Connor-Moyle/test-pcchecker/discussions)
  before opening a new issue.

## Reporting a bug

Open a **Bug report** issue and include:

- Windows version and build (`winver`)
- PC Checker version (Settings → About, or the title bar)
- Which page, what you did, what you expected and what happened instead
- The diagnostics export (Logs page → **Export diagnostics**). Serial numbers
  are masked when *Redact serial numbers* is on in Settings → Logging.

## Reporting a hardware or driver problem

A sensor, fan, GPU, RGB device, monitor or input device that is missing or
misbehaving is the most useful kind of report, because support for new
hardware is mostly a matter of knowing what people have. Open a
**Driver or hardware issue** and include the model names, which page is
affected, what the page's support level or status text says, and the
diagnostics export. The Hardware page's **Export JSON** button is also
helpful.

## Suggesting a feature

Open a **Feature request** issue. Describe the problem you are trying to solve
first, then the solution you have in mind. If the feature changes the system,
say what the confirmation dialog should explain.

## Building from source

See [docs/BUILDING.md](docs/BUILDING.md). The short version:

```powershell
dotnet build PcChecker.sln -c Release
dotnet test PcChecker.sln -c Release
dotnet run --project src/PcChecker.App -- --demo
```

The Core test project runs on Windows, Linux and macOS. The app itself runs on
Windows only. `--demo` runs the app with sample data so you can work on a page
without hardware, and `--screenshots <dir>` captures every page to PNG (see
[docs/DEMO-MODE.md](docs/DEMO-MODE.md)).

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep each pull request focused on one change.
3. Add or update tests for any pure logic you touch (parsers, rules, validators,
   catalogs, formatters). Tests must not depend on WMI, the registry,
   PowerShell, drivers or the network.
4. Make sure `dotnet build` and `dotnet test` pass with no new warnings. XAML
   is compiled during the build, so a XAML typo is a build error.
5. If you add or change a page, make sure its view model implements
   `InitializeAsync` so demo mode and the screenshot runner show it populated,
   and that its `Demo*` service returns realistic sample data.
6. Update `CHANGELOG.md` under *Unreleased*.
7. Fill in the pull request template, including the safety checklist.

Commit messages are plain and descriptive, written in the imperative mood
("Add SMART temperature threshold", not "Added..."). No trailers.

## Code style

- C# 12, file-scoped namespaces, nullable reference types on, implicit usings.
- Async all the way down with a `CancellationToken` on every long operation and
  `IProgress<ProgressUpdate>` for scans.
- MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).
  No logic in code-behind beyond `InitializeComponent()` and `DataContext`.
- Anything that touches Windows APIs must degrade gracefully: catch, log,
  return partial data with a note. Never crash the UI because a WMI class is
  missing, a driver is absent or a vendor DLL is not installed.
- Pure logic goes in `static` classes with no Windows dependency so it can be
  unit-tested anywhere.
- Every destructive or system-changing operation goes through
  `IChangeGuard.RunGuardedAsync` and is confirmed with the user first.
- Every hardware feature shows its support level ("Full", "Experimental",
  "Monitor only", "Not supported on this device") with a one-line reason and
  never silently does nothing.
- Each module ships both a `Windows*` and a `Demo*` implementation of its
  service and registers them on one line in
  `src/PcChecker.App/Services/ServiceRegistration.cs`.
- UI text is plain English. Explain technical terms with a tooltip or a
  subtitle.
- Formatting follows `.editorconfig`; run `dotnet format` before committing.

## Adding a NuGet package

Package versions are managed centrally in `Directory.Packages.props`. Add the
version there and reference the package without a version in the project file.
Say why the package is needed in the pull request. Do not reference the
`NAudio` meta-package; use `NAudio.Core` and `NAudio.Wasapi` (the meta-package
drags in Windows Forms and breaks the Linux test run).

## License

By contributing you agree that your contributions are licensed under the
[MIT License](LICENSE).
