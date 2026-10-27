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
| `Drivers` | `IDriverService`, driver and update records, `DriverRules`, `IDriverCleanupService` and `DriverCleanupRules` (old and unused drivers); `Drivers/Sources`: `IDriverSourceService` and the `IDriverUpdateProvider`s (Windows Update, NVIDIA, AMD, Intel, PC maker tools), `DriverVersionMath`, `DriverDeviceMapper`, `NvidiaDriverLookup`, `AmdDriverLookup`, `VendorDownloadRules`, `OemToolCatalog`, `DriverOfferMerger` |
| `WindowsUpdate` | `IWindowsUpdateService` (software updates, history, pause, active hours), `UpdateClassifier`, `UpdatePauseRules`, `ActiveHoursRules`, `UpdateHistoryMapper`, `UpdatePolicyRules`; `IUpdateRollbackService` (uninstall, hide, go back to the previous Windows version) with `InstalledUpdateRules`, `HiddenUpdateRules`, `RollbackRules`, `DismOutputParser`, `UpdateToolExitCodes`; shares `WuaCom` and `WuaResultCodes` with `Drivers` |
| `Software` | `ISoftwareInventoryService`, `IUninstallService`, `BundleDetector`, `UninstallCommandBuilder`, `ProtectedSoftwareRules` |
| `Leftovers` | `ILeftoverService` (uninstall leftovers), `LeftoverNameMatcher`, `LeftoverProgramIndex`, `LeftoverAllowList`, `LeftoverPathRules`, `LeftoverScoring`, `LeftoverBackupNaming`, `LeftoverTaskXml`, `LeftoverScanQueue` (Apps → Leftovers hand-off); shares `RegistryBackupStore` with `Registry` |
| `Performance` | `IPerformanceAnalyzer`, `PerformanceFacts`, `SlowdownRules` |
| `Optimization` | `IOptimizationService`, `OptimizationCatalog`, `RiskLevel` |
| `Cleanup` | `ITempFileScanner`, `ITempFileCleaner`, `CleanupCatalog`, `CleanupSafety` |
| `DiskSpace` | `IDiskSpaceService`, `DiskTreeScanner` (parallel folder sizes), `DuplicateHasher`, `DiskSpaceAggregation`, `FileTypeClassifier`, `DiskSpaceProtection`, `DuplicateFinder` |
| `WindowsFeatures` | `IWindowsFeatureService`, `InboxAppCatalog` |
| `Restore` | `IRestorePointService`, `IChangeGuard`, `ChangeGuard`, `IChangeBatchRunner` and `ChangeBatch` (several guarded changes behind one restore point) |
| `Overclocking` | `IOverclockService`, profiles and plans, `OverclockSafetyValidator`, `OverclockPlanParser` |
| `Power` | `IPowerService`, power snapshots and plans |
| `History` | `IHistoryService` (background recorder), `HistoryFileStore` (one JSON Lines file per day), `HistoryLineCodec`, `HistoryRollup`, `HistoryRetention`, `HistoryEventDetector`, `HistoryCsvExporter`, `DemoHistoryGenerator` |
| `Updates` | `IAppUpdateService` (single-exe self-update from GitHub Releases, SHA-256 verified) |
| `Assistant` | `IPcAssistantService`, `AssistantContextBuilder` |
| `Reports` | `IReportService`, `ReportBundle` |
| `Fans` | `IFanControlService`, fan devices/curves/profiles, `FanCurveMath` |
| `Lighting` | `ILightingService` (OpenRGB), `RgbColor`, devices/zones/modes/profiles, `RgbColorMath` |
| `Storage` | `IStorageToolsService` (surface scan, benchmark, chkdsk, repair, optimize), `BlockMapBuilder`, `BenchmarkMath`, `StorageVendorRules` |
| `Benchmark` | `IBenchmarkService`, `IBenchmarkHistoryStore`, the CPU and memory workloads, `BenchmarkScoring`, `BenchmarkStatistics`, `BenchmarkComparer`, `BenchmarkFormatter` |
| `Memory` | `IMemoryService`, `MemoryOverview`, profiles, `MemoryTestPatterns`, `PageFileRules` |
| `Battery` | `IBatteryService`, `BatteryReportParser` (`powercfg /batteryreport /xml`), `IsoDuration`, `BatteryMath`, `BatteryChartBuilder`, `BatteryPageRules`, `BatterySources` |
| `Displays` | `IDisplayService`, `DisplayInfo`/`DisplayMode`, profiles, `DisplayModeMath` |
| `Audio` | `IAudioService` (NAudio.Wasapi Core Audio wrappers), devices and sessions |
| `Input` | `IInputService`, `IMousePollingMeter`, mouse/keyboard settings, profiles, vendor links |
| `Latency` | `ILatencyService`, `LatencyReport`, `LatencyRules`, `BcdEditParser`, `PowerCfgQueryParser`, `LatencyActionIds` |
| `Processes` | `IProcessService`, `ProcessCatalog`, `ProcessRateTracker`, `ProcessMath`, `ProcessSorting`, `CriticalProcessRules`, `ProcessSuspicionRules`, `SignatureCache`; the Task Manager parts: `ProcessGrouping` (Apps, Background, Windows processes), `ProcessStatusRules` (suspended, not responding, end-task plan, efficiency mode), `ProcessSafetyRules`, `PowerUsageRules`, `ProcessHeatMap`, `AffinityMath`, `ProcessColumns`, `CommandLineText`, `ProcessesSettings` |
| `LiveUsage` | `ILiveUsageService` (Task Manager's Performance tab: PDH counters added by English name through `PdhQuery`, `GetPerformanceInfo`, DXGI), `SampleRing`, `LiveUsageFormat`, `LiveUsageMath`, `CounterInstanceParser`, `CpuTopologyParser`, `LiveUsageText`, `LiveUsagePreferencesStore` |
| `GameRuntimes` | `IGameRuntimeService` (Missing DLLs page: DLL detection, game scans, official runtime installs), `RuntimeCatalog` (every DLL and the package, source, switches and signer behind it), `PeImportReader` (PE32/PE32+ import and delay-import tables), `RuntimeStatusRules`, `GameScanRules`, `RuntimeInstallRules` |
| `Startup` | `IStartupService`, `BootConfig`, `StartupEnumerator` (shared with Performance) |
| `Accessibility` | `ThemeColor` and `ContrastMath` (WCAG contrast), `AccentPalette` (the dark and light status colours, checked against AA by the tests), `AccessibleText` (screen-reader names, access keys) |
| `Shell` | Quick search (`FuzzyMatcher`, `QuickSearchIndex`, `QuickSearchRanker`, `RecentItems`, the settings, quick action and page keyword catalogs) and `WelcomeRules` for the first-start welcome |
| `Checkup` | `ICheckupService` and `CheckupService` (runs the other modules' scans and fixes through their interfaces, so one class serves both modes), `CheckupItemMapper`, `CheckupPrioritizer`, `CheckupSelection`, `CheckupBatchPlanner`, `CheckupProgress`, `DashboardGlance` |
| `Scheduling` | `IScheduledTaskService` (schtasks), `ScheduledJobExecutor`, `IScheduledJobStore`, the `PcChecker.Jobs` pipe, `SchtasksCommandBuilder`, `SchtasksCsvParser`, `ScheduleRules`, `JobResultSummarizer`, tray text and popup placement helpers |
| `ScheduledTasks` | `ITaskSchedulerService` (every Task Scheduler task through the COM API, `schtasks /Query /XML ONE` as fallback; run, end, turn off or on, delete with a sealed copy, restore), `TaskDefinitionParser`, `TaskTriggerText`, `TaskResultText`, `TaskActionText`, `TaskSuspicionRules`, `TaskPublisherRules`, `TaskSafetyRules`, `TaskBackupNaming`, `TaskAnalysis` |
| `Connections` | `IConnectionService` (GetExtendedTcpTable/GetExtendedUdpTable, reverse DNS, SetTcpEntry), `EndpointTableParser` (raw MIB_*_OWNER_PID rows), `ConnectionRules`, `ConnectionText`, `ConnectionGrouping`, `HostNameCache` |
| `SetupBackup` | `ISetupBackupService` (export, import, reset, undo), `ISetupBackupStore` (`FileSetupBackupStore`, `DemoSetupBackupStore`), `SetupSectionCatalog`, `SetupExporter`, `SetupFileReader`, `SetupMigrations`, `SetupPreviewBuilder`, `SetupApplier`, `SetupScheduleSync` |

Inside each folder you will find the same shapes:

* `Models/` — records and small classes. Records are immutable; settings-style objects are mutable classes so they serialize cleanly.
* `I<Thing>Service.cs` — the interface the rest of the app depends on.
* `Windows<Thing>Service.cs` — the implementation that talks to Windows (WMI, registry, PowerShell, COM, LibreHardwareMonitor, NvAPI, ADL, IGCL, OpenRGB, NAudio).
* `Demo<Thing>Service.cs` — a pure .NET implementation that returns realistic sample data for a gaming desktop (Intel Core i7-13700K, 32 GB DDR5-6000, GeForce RTX 4070, Samsung 990 PRO + WD 4 TB HDD, ASUS ROG STRIX Z790-E, Windows 11 24H2) with small artificial delays so busy states render. `Common/DemoData` holds the shared facts.
* `static class` helpers — pure logic with no Windows dependency: parsers, validators, catalogs, scoring. These are what the tests cover.

The split matters: anything that reads or changes the system goes behind an interface, and anything that *decides* something is a static function that takes plain data in and returns plain data out.

### Claude: the assistant

`PcChecker.Claude` implements `IPcAssistantService` using the Anthropic SDK. It is the only project that knows about the API. It reads the API key through `ISecretStore` (the key is stored DPAPI-protected in settings and never written in plain text) and builds its prompt context with `AssistantContextBuilder` from Core, which can redact serial numbers and MAC addresses before anything leaves the machine.

### App: the WPF shell

`PcChecker.App` is a WPF application using WPF-UI for the Fluent look and CommunityToolkit.Mvvm for view models.

* `Program.cs` starts the `App`; a self-updated copy is started with `--wait-pid` so it waits for the old process to close.
* `App.xaml.cs` enforces a single instance, parses the command line (`AppOptions`), loads settings, configures logging, builds the dependency-injection container through `ServiceRegistration.Register`, applies the theme, wires global exception handlers that log and show a plain-language dialog, and starts the `ScreenshotRunner` when asked.
* `Services/ServiceRegistration.cs` is the **only** place that maps interfaces to implementations. It takes the `AppMode` and registers `Windows*` or `Demo*` classes accordingly. Module action providers are registered on the lines marked `// providers:`.
* `Views/Windows/MainWindow.xaml` is the shell: title bar, section menu built from `NavigationCatalog`, section tabs, the quick search palette (`Controls/QuickSearchPalette`, `QuickSearchViewModel`, Ctrl+K), snackbar and dialog hosts. Its `Navigated` handler calls `EnsureInitializedAsync` on the target page's view model. Quick actions run through `Services/QuickActionRunner`, which opens the page and executes the page's own command; `Services/WelcomeService` shows the first-start welcome.
* `Tray/` — `TrayIconService` (H.NotifyIcon: tooltip, temperature icon, menu, notifications, minimize/close to tray) and the compact `TrayPopupWindow`. `Services/ScheduledRunner.cs` runs `--scheduled` jobs headless, answers the job pipe and finds tasks that point at a moved `PcChecker.exe`.
* `Views/Pages/*Page.xaml` — one page per feature. A page's code-behind only receives its view model, sets `DataContext` and calls `InitializeComponent()`.
* `ViewModels/*ViewModel.cs` — one per page, all deriving from `ViewModelBase`.
* `Controls/` — shared controls used by every page: `SectionHeader`, `StatCard`, `GaugeControl`, `SparklineControl`, `KeyValueList`, `EmptyState`, `BusyOverlay`.
* `Converters/` — value converters registered in `Resources/Styles.xaml`.
* `Services/` — UI-side services: `PageService` (resolves pages from DI), `DialogService`, `NotificationService`, `HotkeyService`, `OverlayService`, `DispatcherHelper`, `NavigationCatalog` (sections and pages), `StartupNotices` (messages for the Dashboard), `ScreenshotRunner`.

## Navigation

The side menu has one item per section; a section with several pages shows them as tabs above the content. Every page is always visible; Advanced mode (the toggle in the navigation footer, also in Settings) only reveals advanced sections *inside* pages. Pages that need a consent gate (Overclock) handle it themselves.

```
Home            Dashboard · Check-up
Monitor         Temperatures · Power · Processes
Monitor         Temperatures · Power · History
Hardware        Overview · Health · Storage · Memory · Battery
Devices         Displays · Audio · Keyboard & Mouse
Cooling & RGB   Fans · Lighting
Apps & Drivers  Apps · Leftovers · Drivers · Startup · Windows Features
Clean up        Cleanup
Performance     Speed check · Optimize · Benchmark · Overclock
Gaming          Overlay · Latency · Missing DLLs
Ask Claude      Ask Claude
Footer          Logs · Settings · Advanced mode toggle
```

`Services/NavigationCatalog.cs` is the only list of pages. `MainWindow` builds the menu from its sections and the tab strip from the current section, and the screenshot runner walks its pages. Add a page there and nowhere else. Layout rules for pages are in [UI-GUIDELINES.md](UI-GUIDELINES.md).

## App modes and the command line

`Core.Common.CommandLineOptions` parses the arguments (it is pure, so it is unit-tested):

| Switch | Effect |
|---|---|
| `--demo` | `AppMode.Demo`: every service is a `Demo*` implementation with sample data. Demo mode uses its own settings file (`%LOCALAPPDATA%\PcChecker\demo\settings.json`). |
| `--real` | Forces `AppMode.Normal` even when `--demo` or `--screenshots` is present. |
| `--screenshots <dir>` | Implies demo. After the window shows, `ScreenshotRunner` sizes it to 1440×900, visits every page in navigation order, waits for `EnsureInitializedAsync` and `IsBusy == false` plus 400 ms, renders the window with `RenderTargetBitmap` to `<page>.png`, captures the overlay over a dark gradient as `overlay.png`, writes `index.json` and exits with code 0 (1 on failure, with the reason on stderr and in the log). CI uses this on Windows to produce real screenshots. |
| `--minimized` | Starts minimized (same as the setting). |
| `--tray` | Starts hidden with only the tray icon (`TrayIconService`); the logon task behind *Start with Windows* passes it. |
| `--scheduled <job>` | Headless run of `health`, `cleanup`, `drivers` or `report` for Windows Task Scheduler. If PC Checker is already open the job is handed to it over the `PcChecker.Jobs.<session>.<user SID>` named pipe (current user and session only); otherwise `ScheduledRunner` builds the services without a window, runs `ScheduledJobExecutor` (10-minute limit), writes `%LOCALAPPDATA%\PcChecker\schedules\<job>-last.json`, shows a notification from a temporary tray icon for 10 s and exits with 0 or 1. Task commands come from `SchtasksCommandBuilder` and status from `SchtasksCsvParser` (both pure and tested in `SchedulingTests`). |

## View model lifecycle

`ViewModelBase` has three lifecycle members besides the busy/error state:

* `InitializeAsync(ct)` — override to load the last results or run the page's default scan. Called once.
* `EnsureInitializedAsync(ct)` — runs `InitializeAsync` exactly once even when called from several places (navigation, page `Loaded`, the screenshot runner). Exceptions become `ErrorMessage`.
* `RefreshAsync(ct)` — override for the header "Refresh" action.

## Shared hardware monitor host

Only one LibreHardwareMonitor `Computer` may be open at a time. `LibreHardwareSensorService` implements both `ISensorService` and `IHardwareMonitorHost`, and `ServiceRegistration` registers the same instance for both. Fans, Health (SMART) and Overclock read through `IHardwareMonitorHost.WithComputer(...)` instead of opening their own instance. In demo mode `DemoHardwareMonitorHost` reports `IsOpen == false` so callers take their "not available" paths.

## Optimization action providers

`IOptimizationService` owns the actions in `OptimizationCatalog` and aggregates every `IOptimizationActionProvider` registered in DI. The Latency, Memory, Startup and Input modules contribute their own actions (for example `latency.hags.on`, `memory.standby.clear`, `startup.faststartup.off`, `input.pointerprecision.off`) through a provider class in their own folder; the service dispatches by `Handles(actionId)`. Providers run their changes through `IChangeGuard` themselves. Registering a provider is a one-line change on the `// providers:` lines in `ServiceRegistration.cs`.

## Vendor breadth and honesty

Overclocking detects the GPU vendor from the PNP vendor id (NVIDIA via NvAPI, AMD via ADL/Overdrive8, Intel Arc via IGCL) and the CPU tool that is installed (Intel XTU CLI, AMD Ryzen Master, ryzenadj). `OverclockCapabilities.SupportMatrix` lists, per feature, whether support is Full, Partial, Experimental, Monitor only or Unsupported with a one-line reason, and the page shows it. Any feature that cannot be verified on the user's hardware must say so and fall back to launching the vendor's own software rather than silently doing nothing.

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

`ChangeGuard` also implements `IChangeBatchRunner`. `RunBatchAsync` creates one restore point and runs a piece of work;
every guarded change made from that work (through any service, found through an `AsyncLocal` scope) skips its own
restore points and is journaled with the batch's. If the batch's restore point cannot be created, the work runs
without the batch and each change follows its own options again. The Check-up page uses it for *Fix selected*.

The UI must also ask for confirmation before invoking a guarded change and explain what will happen.

A reversible change can also say how to take it back straight away (`ChangeOptions.Undo`, or a `ChangeUndo.Offer`
scope opened by the caller, usually through the app's `UndoOffers.RunAsync`). The guard then registers it with
`IChangeUndoTracker` (not inside a batch, never for a change made by an undo) together with its undo flow
(`ChangeUndo.BeginFlow`, an `AsyncLocal` started in the method that makes the change). The app's `NotificationService`
adds an **Undo** button only to a success notification shown from that same flow, for 10 seconds from when it appears,
and asks before running it. `UndoOfferRules` holds the decisions and is tested.

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
* NuGet versions are pinned centrally in `Directory.Packages.props`. Add a package there before referencing it. Never reference the `NAudio` meta-package: it pulls in Windows Forms through `NAudio.WinForms`, which stops the tests from running on Linux. Use `NAudio.Wasapi` (Core Audio) and `NAudio.Core`.

## Module boundaries

Each feature area owns:

* its Core folder (models, interface, implementation, helpers),
* its view model and page in the app,
* its tests file in `tests/PcChecker.Core.Tests`.

Shared files — `App.xaml.cs`, `MainWindow.xaml`, `ViewModelBase`, `ServiceRegistration.cs`, the shared controls and converters, `Directory.Packages.props` — should change rarely and deliberately. If a feature needs a new service, add the interface, the `Windows*` implementation and a `Demo*` implementation to its own folder and register them on one line in `ServiceRegistration.cs`. If it needs a shared control, prefer extending the existing control with a new property over creating a near-duplicate.

Settings sections for the new modules (`Fans`, `Lighting`, `Displays`, `Input`, `Memory`, `Latency`, `Startup`) live on `AppSettings`; `JsonSettingsService.Repair` makes sure none of them is null after loading an older file.

Cross-feature communication goes through the interfaces, never through another feature's view model. For example, the Dashboard reads `IHealthService.Last` and `ISensorService.Latest`; it does not reach into `HealthViewModel`.

## Testing

Tests live in `tests/PcChecker.Core.Tests`, one file per area, and cover only pure logic so they run anywhere. If you add a rule, a parser, a validator or a catalog entry, add a test for it. Run everything with:

```
dotnet build PcChecker.sln -c Release
dotnet test PcChecker.sln -c Release
```
