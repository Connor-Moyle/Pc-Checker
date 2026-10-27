# Building PC Checker

## Prerequisites

- **Windows**: Visual Studio 2022 17.8 or later with the *.NET desktop
  development* workload, or JetBrains Rider, or just the
  [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) (the
  version pinned in `global.json`, or any later 8.0.x feature band).
- **Linux / macOS**: the .NET SDK 8.0 is enough to build the whole solution
  and run the Core tests, because every project sets
  `EnableWindowsTargeting`. Only running the app itself needs Windows. CI
  builds on both.
- Optional, for the icon script: Python 3 with Pillow.
- Optional, for packaging: the Velopack CLI (`dotnet tool install -g vpk`).

## Build

```powershell
git clone https://github.com/Connor-Moyle/test-pcchecker.git
cd test-pcchecker
dotnet build PcChecker.sln -c Release
```

XAML is compiled during the build, so a XAML typo is a build error, not a
runtime one.

## Test

```powershell
dotnet test PcChecker.sln -c Release
```

The test project only covers pure logic (rules, parsers, validators,
catalogs, formatters, settings round-trips, the change guard with fakes).
Nothing in it touches WMI, the registry, PowerShell, drivers or the network,
which is why it runs anywhere.

## Run

```powershell
dotnet run --project src/PcChecker.App
dotnet run --project src/PcChecker.App -- --demo
```

The app asks for administrator rights on start (its manifest sets
`requireAdministrator`) because hardware scans, fan control, driver installs,
cleanup and most optimisations need them. `--demo` fills every page with
sample data without touching the machine; see [DEMO-MODE.md](DEMO-MODE.md).

## Publish

Framework-dependent (small, needs the .NET 8 Desktop Runtime installed):

```powershell
dotnet publish src/PcChecker.App/PcChecker.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

Self-contained single file (what the releases ship):

```powershell
dotnet publish src/PcChecker.App/PcChecker.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

## Installer and updates (Velopack)

Releases are packaged with [Velopack](https://velopack.io), which gives us a
setup exe, delta updates and in-app self-update from GitHub Releases.

```powershell
dotnet tool install -g vpk
dotnet publish src/PcChecker.App/PcChecker.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
vpk pack --packId PcChecker --packVersion 0.1.0 --packDir publish --mainExe PcChecker.exe `
  --packTitle "PC Checker" --packAuthors "PC Checker contributors" `
  --icon src/PcChecker.App/Assets/icon.ico --outputDir Releases
```

`build/publish.ps1` does all of that in one go and also zips a portable build
and writes `SHA256SUMS.txt`:

```powershell
.\build\publish.ps1 -Version 0.1.0            # publish + vpk pack + zip + checksums
.\build\publish.ps1 -Version 0.1.0 -SkipPack  # publish + zip only (no Velopack)
.\build\publish.ps1 -Version 0.1.0 -Screenshots  # also capture demo screenshots
```

See [`build/README.md`](../build/README.md).

## Releasing

1. Update `CHANGELOG.md`: move the *Unreleased* items under a new version
   heading with today's date.
2. Commit, then tag and push:

   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```

   Use `v0.2.0-beta.1` for a pre-release; the in-app updater on the Beta
   channel will pick it up and the Stable channel will ignore it.
3. `.github/workflows/release.yml` builds and tests, publishes a
   self-contained build, packs it with Velopack, zips the portable build,
   computes `SHA256SUMS.txt` and creates the GitHub release with generated
   notes and every file attached.

## Versioning

`VersionPrefix` in `Directory.Build.props` is the development version. The
release workflow passes `-p:Version=<tag without v>` so the tag is the single
source of truth for a release. CI preview builds carry a `ci.<run>` suffix.

## Continuous integration

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | every push, pull requests to `main` from forks (same-repository PRs are covered by the push run) | Build and test on Windows and Linux, publish a self-contained win-x64 build as the `PcChecker-win-x64` artifact, then run the published app with `--screenshots --demo` on Windows and upload the `screenshots` artifact. |
| `screenshots.yml` | manual, or app code changes on `main` | Same capture, then commits the PNGs into `docs/screenshots/` when they changed. |
| `release.yml` | tags `v*` | Package and publish a release. |
| `codeql.yml` | pushes and pull requests to `main`, weekly | CodeQL security analysis of the C# code. |

Dependabot opens weekly pull requests for NuGet packages and GitHub Actions.

## Project structure

See [ARCHITECTURE.md](ARCHITECTURE.md). In short: `src/PcChecker.Core` holds
contracts, models, pure logic and both the Windows and Demo implementations;
`src/PcChecker.Claude` is the assistant on the official Anthropic SDK;
`src/PcChecker.App` is the WPF app; `tests/PcChecker.Core.Tests` is xUnit.

## Package versions

All NuGet versions are pinned centrally in `Directory.Packages.props`.
Project files reference packages without a version. Do not add the `NAudio`
meta-package; the solution references `NAudio.Core` and `NAudio.Wasapi`
directly so the tests keep running on Linux.

## Icon

`build/make-icon.py` renders `src/PcChecker.App/Assets/icon.ico` and
`logo.png` from `build/icon.svg`. It needs Python 3 and Pillow.

## Troubleshooting

- **XAML errors on Linux**: XAML is compiled by the build, so a typo is a
  build error, not a runtime one. Read the error line and column.
- **`NETSDK1100` or "Windows targeting"**: make sure you are building the
  solution (or a project under it) so `Directory.Build.props` applies.
- **CA1416 warnings**: suppressed on purpose; every project is Windows-only.
- **The tests fail to run on Linux with a Windows Forms error**: something
  pulled in the `NAudio` meta-package. Reference `NAudio.Wasapi` instead.
- **`vpk` is not found**: `dotnet tool install -g vpk` and make sure
  `%USERPROFILE%\.dotnet\tools` is on your `PATH`.
