# Architecture

This document describes how PC Checker is put together so you can find your way around the code, add a feature, or fix a bug without stepping on other parts of the app.

## Solution layout

```
PcChecker.sln
Directory.Build.props          shared compiler settings for every project
Directory.Packages.props       central NuGet package versions
src/PcChecker.Core/            contracts, models, pure logic and the Windows implementations (no UI)
src/PcChecker.Claude/          the Claude assistant, built on the official Anthropic SDK
src/PcChecker.App/             the WPF application (Fluent UI, MVVM)
tests/PcChecker.Core.Tests/    xUnit tests for the pure logic in Core
build/                         icon source and the script that renders the app icon
docs/                          this folder
```

Every project targets `net8.0-windows10.0.19041.0`. `EnableWindowsTargeting` is on, so the solution builds (and the Core tests run) on Linux and macOS as well as Windows; only the app itself needs Windows to run.

## The three layers

### Core: contracts and logic

`PcChecker.Core` is split into one folder per feature area. The namespace matches the folder name:

| Folder | What lives there |
|---|---|
| `Common` | `OperationResult`, `ProgressUpdate`, `ByteSize`, `VersionText`, `PlatformGuard`, `PowerShellRunner`, shared enums |
| `Settings` | `AppSettings` and its nested sections, `ISettingsService` (JSON file), `ISecretStore` (DPAPI) |
| `Logging` | `ILogService` (Serilog pipeline + in-app buffer), `IActionJournal`, `LogCategories` |
| `Hardware` | `HardwareInventory`, typed device records, `IHardwareInventoryService`, `WmiPropertyMapper` |
| `Sensors` | `ISensorService`, `SensorReading`/`SensorSnapshot`, `SensorMath` |
| `Health` | `HealthReport`, `IHealthService`, `HealthScoring`, `SmartRules`, `DeviceProblemCodes` |
| `Drivers` | `IDriverService`, driver and update records, `DriverRules` |
| `Software` | `ISoftwareInventoryService`, `IUninstallService`, `BundleDetector`, `UninstallCommandBuilder`, `ProtectedSoftwareRules` |
| `Performance` | `IPerformanceAnalyzer`, `PerformanceFacts`, `SlowdownRules` |
| `Optimization` | `IOptimizationService`, `OptimizationCatalog`, `RiskLevel` |
| `Cleanup` | `ITempFileScanner`, `ITempFileCleaner`, `CleanupCatalog`, `CleanupSafety` |
| `WindowsFeatures` | `IWindowsFeatureService`, `InboxAppCatalog` |
| `Restore` | `IRestorePointService`, `IChangeGuard`, `ChangeGuard` |
| `Overclocking` | `IOverclockService`, profiles and plans, `OverclockSafetyValidator`, `OverclockPlanParser` |
| `Power` | `IPowerService`, power snapshots and plans |
| `Updates` | `IAppUpdateService` (Velopack / GitHub Releases) |
| `Assistant` | `IPcAssistantService`, `AssistantContextBuilder` |
| `Reports` | `IReportService`, `ReportBundle` |

Inside each folder you will find the same shapes:

* `Models/` — records and small classes. Records are immutable; settings-style objects are mutable classes so they serialize cleanly.
* `I<Thing>Service.cs` — the interface the rest of the app depends on.
* `Windows<Thing>Service.cs` — the implementation that talks to Windows (WMI, registry, PowerShell, COM, LibreHardwareMonitor, NvAPI).
* `static class` helpers — pure logic with no Windows dependency: parsers, validators, catalogs, scoring. These are what the tests cover.

The split matters: anything that reads or changes the system goes behind an interface, and anything that *decides* something is a static function that takes plain data in and returns plain data out.

### Claude: the assistant

`PcChecker.Claude` implements `IPcAssistantService` using the Anthropic SDK. It is the only project that knows about the API. It reads the API key through `ISecretStore` (the key is stored DPAPI-protected in settings and never written in plain text) and builds its prompt context with `AssistantContextBuilder` from Core, which can redact serial numbers and MAC addresses before anything leaves the machine.

### App: the WPF shell

`PcChecker.App` is a WPF application using WPF-UI for the Fluent look and CommunityToolkit.Mvvm for view models.

* `Program.cs` runs the Velopack bootstrap, then starts the `App`.
* `App.xaml.cs` enforces a single instance, loads settings, configures logging, builds the dependency-injection container, applies the theme and wires global exception handlers that log and show a plain-language dialog.
* `Views/Windows/MainWindow.xaml` is the shell: title bar, left navigation, snackbar and dialog hosts.
* `Views/Pages/*Page.xaml` — one page per feature. A page's code-behind only receives its view model and calls `InitializeComponent()`.
* `ViewModels/*ViewModel.cs` — one per page, all deriving from `ViewModelBase`.
* `Controls/` — shared controls used by every page: `SectionHeader`, `StatCard`, `GaugeControl`, `SparklineControl`, `KeyValueList`, `EmptyState`, `BusyOverlay`.
* `Converters/` — value converters registered in `Resources/Styles.xaml`.
* `Services/` — UI-side services: `PageService` (resolves pages from DI), `DialogService`, `NotificationService`, `HotkeyService`, `OverlayService`, `DispatcherHelper`.

## Key contracts

### `OperationResult`

Every operation that can fail returns `OperationResult` or `OperationResult<T>` instead of throwing. `Success`, `Message`, an optional `Error` and `RebootRequired` give the UI everything it needs to tell the user what happened.

### `IChangeGuard`

Any code that changes the system — uninstalling, deleting files, changing services, registry or features, installing drivers, applying an overclock — must go through `IChangeGuard.RunGuardedAsync`. The guard:

1. creates a restore point first (if the user's settings say so),
2. runs the change,
3. optionally creates a second restore point afterwards,
4. writes an `ActionRecord` to the action journal with undo information,
5. logs the outcome.

The UI must also ask for confirmation before invoking a guarded change and explain what will happen.

### `ISettingsService`

`AppSettings` is a plain object tree stored as indented JSON in `%LOCALAPPDATA%\PcChecker\settings.json`. Missing fields take their defaults, and an unreadable file is backed up and replaced with defaults rather than crashing the app. The `Changed` event lets view models react; `ViewModelBase` uses it to keep `IsAdvancedMode` current.

### `ILogService`

Services receive `ILogger<T>` through constructor injection. The log category shown in the Logs page is derived from the namespace folder (`PcChecker.Core.Sensors.*` → `Sensors`), so there is nothing to annotate. The service keeps a ring buffer for the in-app viewer, writes rolling files, and can be reconfigured at runtime when the user changes logging settings.

### Progress and cancellation

Long operations accept `IProgress<ProgressUpdate>?` and a `CancellationToken`. `ViewModelBase.RunBusyAsync` wires both to the busy overlay and its Cancel button.

## Conventions

* C# 12, file-scoped namespaces, nullable reference types on, implicit usings on.
* Async all the way; never block the UI thread. Results are marshalled back with `DispatcherHelper`.
* Code that touches Windows APIs is wrapped so failures degrade gracefully: catch, log, return partial data with a note in `Warnings`. The UI must never crash because a WMI class is missing or a driver is not installed.
* Keep files focused (roughly under 500 lines). Prefer small, readable functions and a short XML doc comment on public types.
* UI text is plain English for non-technical users. Explain technical terms in a tooltip or a subtitle.
* Destructive actions use `Appearance="Danger"` buttons and always confirm through `IDialogService.ConfirmAsync`.
* Advanced-only pages and controls bind their visibility to `IsAdvancedMode`.
* NuGet versions are pinned centrally in `Directory.Packages.props`. Add a package there before referencing it.

## Module boundaries

Each feature area owns:

* its Core folder (models, interface, implementation, helpers),
* its view model and page in the app,
* its tests file in `tests/PcChecker.Core.Tests`.

Shared files — `App.xaml.cs`, `MainWindow.xaml`, `ViewModelBase`, the shared controls and converters, `Directory.Packages.props` — should change rarely and deliberately. If a feature needs a new service, add the interface and implementation to its own folder and register it in `App.xaml.cs` in the existing block for that area. If it needs a shared control, prefer extending the existing control with a new property over creating a near-duplicate.

Cross-feature communication goes through the interfaces, never through another feature's view model. For example, the Dashboard reads `IHealthService.Last` and `ISensorService.Latest`; it does not reach into `HealthViewModel`.

## Testing

Tests live in `tests/PcChecker.Core.Tests`, one file per area, and cover only pure logic so they run anywhere. If you add a rule, a parser, a validator or a catalog entry, add a test for it. Run everything with:

```
dotnet build PcChecker.sln -c Release
dotnet test PcChecker.sln -c Release
```
