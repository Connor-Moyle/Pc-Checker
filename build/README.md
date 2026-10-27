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
2. `dotnet publish` one self-contained, compressed, single-file
   `PcChecker.exe` into `artifacts\publish`.
3. Write its SHA-256 to `artifacts\SHA256.txt`.
4. With `-Screenshots`, run the published exe with `--screenshots --demo` and
   leave the PNGs in `artifacts\screenshots`.

Parameters:

| Parameter | Default | Meaning |
|---|---|---|
| `-Version` | `VersionPrefix` from `Directory.Build.props` | Version baked into the exe |
| `-Configuration` | `Release` | Build configuration |
| `-Runtime` | `win-x64` | Runtime identifier |
| `-OutputRoot` | `artifacts` | Where everything lands |
| `-SkipTests` | off | Skip `dotnet test` |
| `-Screenshots` | off | Capture demo-mode screenshots after publishing |

## Packaging notes

- A release is exactly one file, `PcChecker.exe`, self-contained so users do
  not need a .NET install. The single-file build extracts native libraries to a
  temp folder on first run (`IncludeNativeLibrariesForSelfExtract`), which
  LibreHardwareMonitor, NvAPIWrapper and OpenRGB.NET need. Licence texts are
  embedded resources (Settings > About > Show licence texts).
- The app updates itself: it downloads the new `PcChecker.exe` from the latest
  release, verifies the SHA-256, swaps the file and restarts. Keep the asset
  name `PcChecker.exe` stable; the updater looks for it.
- Nothing is code-signed yet. When a certificate is available, sign
  `PcChecker.exe` in the release workflow (for example with `signtool` or Azure
  Trusted Signing) before computing the SHA-256, and the SmartScreen note in the
  README can go.
- The app manifest requires administrator rights, so Windows shows a UAC
  prompt on every start.
