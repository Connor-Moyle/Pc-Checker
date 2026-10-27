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

## Build

```powershell
git clone https://github.com/Connor-Moyle/Pc-Checker.git
cd Pc-Checker
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

## Releases and self-update

A release is a self-contained, compressed, single-file `PcChecker.exe` (win-x64, .NET 8 runtime included) and the
same for Windows on Arm, `PcChecker-arm64.exe` (published with `-r win-arm64` and renamed). There is no installer and
no zip; *Install in Program Files* in Settings copies the exe to `C:\Program Files\PC Checker` when a protected
location is needed.

```powershell
dotnet publish src/PcChecker.App/PcChecker.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o publish
```

For the Arm build use `-r win-arm64`. `build/publish.ps1` does the same locally and writes the SHA-256 next to the
exe; `-Runtime win-arm64` publishes the Arm build.

The app updates itself from GitHub Releases: it reads the releases list, picks the newest release for the chosen
channel (Stable skips pre-releases), downloads the `PcChecker.exe` asset, verifies it against GitHub's asset
digest or the `SHA-256` line in the release notes, renames the running exe to `PcChecker.exe.old`, puts the new file
in its place and starts it with `--wait-pid <old pid>` so the new copy waits for the old one to close. The `.old` file
is deleted on the next start. A download that does not match the checksum is deleted and never run.

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
   single-file `PcChecker.exe`, writes its SHA-256 into the release notes and
   creates the GitHub release with that one file attached. An annotated tag's
   message becomes the release codename (`git tag -a v0.2.0 -m "Codename"`).

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
