# Building PC Checker

## Prerequisites

- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) (the
  version pinned in `global.json`, or any later 8.0.x feature band)
- Windows 10/11 to run the app. Building and running the Core tests also
  works on Linux and macOS because every project sets
  `EnableWindowsTargeting`.
- Optional: Visual Studio 2022 17.8+ or JetBrains Rider with the WPF workload.

## Build

```powershell
git clone https://github.com/Connor-Moyle/test-pcchecker.git
cd test-pcchecker
dotnet build PcChecker.sln -c Release
```

## Test

```powershell
dotnet test PcChecker.sln
```

The test project only covers pure logic (rules, parsers, validators,
formatters, settings round-trips). Nothing in it touches WMI, the registry or
PowerShell, which is why it runs anywhere.

## Run

```powershell
dotnet run --project src/PcChecker.App
```

The app asks for administrator rights on start (its manifest sets
`requireAdministrator`) because hardware scans, driver installs, cleanup and
most optimisations need them.

## Publish

Framework-dependent (small, needs the .NET 8 Desktop Runtime installed):

```powershell
dotnet publish src/PcChecker.App -c Release -r win-x64 --self-contained false -o publish
```

Self-contained single file (what the portable download is):

```powershell
dotnet publish src/PcChecker.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Installer and updates (Velopack)

Releases are packaged with [Velopack](https://velopack.io), which gives us a
setup exe, delta updates and in-app self-update from GitHub Releases.

```powershell
dotnet tool install -g vpk
dotnet publish src/PcChecker.App -c Release -r win-x64 --self-contained true -o publish
vpk pack --packId PcChecker --packVersion 0.1.0 --packDir publish --mainExe PcChecker.exe --icon src/PcChecker.App/Assets/icon.ico
```

The `.github/workflows/release.yml` workflow does this automatically when you
push a tag such as `v0.1.0` (or `v0.2.0-beta.1` for the Beta channel).

## Versioning

`VersionPrefix` lives in `Directory.Build.props`. The release workflow passes
`-p:Version=<tag>` so the tag is the single source of truth for a release.

## Project structure

See [ARCHITECTURE.md](ARCHITECTURE.md). In short: `src/PcChecker.Core` holds
contracts, models, pure logic and the Windows implementations;
`src/PcChecker.Claude` is the assistant on the official Anthropic SDK;
`src/PcChecker.App` is the WPF app; `tests/PcChecker.Core.Tests` is xUnit.

## Package versions

All NuGet versions are pinned centrally in `Directory.Packages.props`.
Project files reference packages without a version.

## Icon

`build/make-icon.py` renders `src/PcChecker.App/Assets/icon.ico` and
`logo.png` from `build/icon.svg`. It needs Python 3 and Pillow.

## Troubleshooting

- **XAML errors on Linux**: XAML is compiled by the build, so a typo is a
  build error, not a runtime one. Read the error line and column.
- **`NETSDK1100` or "Windows targeting"**: make sure you are building the
  solution (or a project under it) so `Directory.Build.props` applies.
- **CA1416 warnings**: suppressed on purpose; every project is Windows-only.
