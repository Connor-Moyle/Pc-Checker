# build/

Tooling that is not part of the app itself.

## Icon

- `icon.svg` is the source of the app mark: a rounded dark-blue square, a
  cyan pulse line and a green check badge.
- `make-icon.py` renders it to `src/PcChecker.App/Assets/logo.png` (512 px)
  and a multi-size `src/PcChecker.App/Assets/icon.ico` (256, 128, 64, 48, 32
  and 16 px). It needs Python 3 and Pillow:

  ```powershell
  pip install pillow
  python build/make-icon.py
  ```

  Re-run it after changing the SVG and commit both outputs. The README banner
  (`docs/assets/banner.svg`) reuses the same mark and is hand-written SVG; it
  does not need rendering.

## Local publish and packaging

`publish.ps1` reproduces what the release workflow does, on your own machine:

```powershell
.\build\publish.ps1 -Version 0.1.0
```

It will:

1. `dotnet build` and `dotnet test` the solution in Release.
2. `dotnet publish` a self-contained, single-file win-x64 build into
   `artifacts\publish`.
3. `vpk pack` it into `artifacts\Releases` (installer, update package,
   `releases.win.json`) unless `-SkipPack` is given or `vpk` is not installed.
4. Zip the publish folder as
   `artifacts\PcChecker-<version>-portable-win-x64.zip`.
5. Write `artifacts\SHA256SUMS.txt`.
6. With `-Screenshots`, run the published exe with `--screenshots --demo` and
   leave the PNGs in `artifacts\screenshots`.

Parameters:

| Parameter | Default | Meaning |
|---|---|---|
| `-Version` | `VersionPrefix` from `Directory.Build.props` | Version baked into the binaries and the package |
| `-Configuration` | `Release` | Build configuration |
| `-Runtime` | `win-x64` | Runtime identifier |
| `-OutputRoot` | `artifacts` | Where everything lands |
| `-SkipTests` | off | Skip `dotnet test` |
| `-SkipPack` | off | Skip Velopack packaging |
| `-Screenshots` | off | Capture demo-mode screenshots after publishing |

Install the Velopack CLI once with `dotnet tool install -g vpk`.

## Packaging notes

- Releases are self-contained so users do not need a .NET install. The
  single-file build extracts native libraries to a temp folder on first run
  (`IncludeNativeLibrariesForSelfExtract`), which LibreHardwareMonitor,
  NvAPIWrapper and OpenRGB.NET need.
- Velopack installs per user under `%LOCALAPPDATA%\PcChecker` and the app's
  in-app updater reads `releases.win.json` from GitHub Releases. Keep the
  `--packId` (`PcChecker`) stable forever; changing it orphans existing
  installs.
- The portable zip is the same single `PcChecker.exe`; it cannot self-update
  because Velopack is not managing it, and the app detects that and only
  links the release page.
- Nothing is code-signed yet. When a certificate is available, add
  `--signParams` to `vpk pack` (it signs the exe, the installer and the
  update package in one go) and the SmartScreen note in the README can go.
- The app manifest requires administrator rights; the installer itself does
  not.
