# Contributing to PC Checker

Thanks for taking the time to help. Bug reports, feature ideas, documentation
fixes and code are all welcome.

## Before you start

- Read the [architecture overview](docs/ARCHITECTURE.md) so you know where
  things live.
- Read [SAFETY.md](docs/SAFETY.md). PC Checker changes real systems; every
  destructive action has rules it must follow.
- Search the [issue tracker](https://github.com/Connor-Moyle/test-pcchecker/issues)
  before opening a new issue.

## Reporting a bug

Open a **Bug report** issue and include:

- Windows version and build (`winver`)
- PC Checker version (Settings → About)
- Steps to reproduce and what you expected instead
- The log export (Logs page → **Export**). Logs do not contain serial numbers
  when *Redact serial numbers* is on in Settings → Logging.

## Suggesting a feature

Open a **Feature request** issue. Describe the problem you are trying to solve
first, then the solution you have in mind.

## Building from source

See [docs/BUILDING.md](docs/BUILDING.md). The short version:

```powershell
dotnet build PcChecker.sln -c Release
dotnet test PcChecker.sln
```

The Core test project runs on Windows, Linux and macOS. The app itself runs on
Windows only.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep each pull request focused on one change.
3. Add or update tests for any pure logic you touch (parsers, rules, validators,
   formatters). Tests must not depend on WMI, the registry or PowerShell.
4. Make sure `dotnet build` and `dotnet test` pass and there are no new
   warnings.
5. Update `CHANGELOG.md` under *Unreleased*.
6. Fill in the pull request template.

Commit messages are plain and descriptive, written in the imperative mood
("Add SMART temperature threshold", not "Added..."). No trailers.

## Code style

- C# 12, file-scoped namespaces, nullable reference types on, implicit usings.
- Async all the way down with a `CancellationToken` on every long operation and
  `IProgress<ProgressUpdate>` for scans.
- MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).
  No logic in code-behind beyond `InitializeComponent()` and `DataContext`.
- Anything that touches Windows APIs must degrade gracefully: catch, log,
  return partial data with a note. Never crash the UI.
- Pure logic goes in `static` classes with no Windows dependency so it can be
  unit-tested anywhere.
- Every destructive or system-changing operation goes through
  `IChangeGuard.RunGuardedAsync` and is confirmed with the user first.
- UI text is plain English. Explain technical terms with a tooltip.
- Formatting follows `.editorconfig`; run `dotnet format` before committing.

## Adding a NuGet package

Package versions are managed centrally in `Directory.Packages.props`. Add the
version there and reference the package without a version in the project file.
Say why the package is needed in the pull request.

## License

By contributing you agree that your contributions are licensed under the
[MIT License](LICENSE).
