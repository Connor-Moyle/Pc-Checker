using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Demo debloat service for the sample gaming desktop (Windows 11 24H2, build 26100). Runs the real engine against an
/// <see cref="InMemoryDebloatSystem"/> seeded with a realistic mix: a few tweaks already applied, some partial, consumer
/// apps installed, a physical printer present (so the Print Spooler tweak explains why it is held back) and Windows
/// 10-only tweaks shown as not available. Apply and revert really change the simulated state. Pure .NET.
/// </summary>
public sealed class DemoDebloatService : DebloatServiceBase
{
    private readonly IOptimizationService _optimization;

    public DemoDebloatService()
        : this(new DemoOptimizationService([]))
    {
    }

    public DemoDebloatService(IOptimizationService optimization)
        : base(new DebloatEngine(CreateSampleSystem(), new DebloatCaptureBook()))
    {
        _optimization = optimization;
        if (Engine.System is InMemoryDebloatSystem system)
        {
            system.DelayMs = 120;
        }
    }

    /// <summary>The simulated PC (exposed for tests).</summary>
    public InMemoryDebloatSystem SampleSystem => (InMemoryDebloatSystem)Engine.System;

    public override async Task<IReadOnlyDictionary<string, TweakState>> GetStatesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 600).ConfigureAwait(false);
        return await base.GetStatesAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 400, 800).ConfigureAwait(false);
        return OperationResult.Ok("File Explorer restarted (demo). Taskbar, Start and context menu changes are now visible.");
    }

    public override OperationResult OpenSettings(string settingsUri) =>
        OperationResult.Ok($"Demo mode: on a real PC this opens {settingsUri}.");

    protected override async Task<OperationResult<IReadOnlyList<TweakRunResult>>> RunGuardedAsync(string description, IReadOnlyList<DebloatTweak> tweaks, bool revert,
        Func<CancellationToken, Task<IReadOnlyList<TweakRunResult>>> work, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Creating a restore point", null, "Demo: PC Checker: before " + description));
        await DemoData.DelayAsync(cancellationToken, 400, 700).ConfigureAwait(false);
        var results = await work(cancellationToken).ConfigureAwait(false);
        return OperationResult<IReadOnlyList<TweakRunResult>>.Ok(results, Finish(results, revert, null).Summary);
    }

    protected override Task<OperationResult> RunDelegatedAsync(string optimizationId, bool revert, CancellationToken cancellationToken) =>
        revert ? _optimization.RevertAsync(optimizationId, cancellationToken) : _optimization.ApplyAsync(optimizationId, cancellationToken);

    protected override Task<ActionState> GetDelegatedStateAsync(string optimizationId, CancellationToken cancellationToken) =>
        _optimization.GetStateAsync(optimizationId, cancellationToken);

    /// <summary>Builds the sample machine's state.</summary>
    public static InMemoryDebloatSystem CreateSampleSystem()
    {
        var system = new InMemoryDebloatSystem(DebloatCatalog.Windows11_24H2);
        var reg = system.Memory;

        // Windows defaults that exist as real values on a clean install.
        reg.SetDword(DebloatHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1);
        reg.SetDword(DebloatHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 1);
        reg.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "Hidden", 2);
        reg.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "TaskbarAl", 1);
        reg.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "TaskbarDa", 1);
        reg.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "ShowCopilotButton", 1);
        reg.SetDword(DebloatHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 2);
        reg.SetDword(DebloatHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 0);
        reg.Write(DebloatHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", new RegistryData(DebloatValueKind.String, "Allow"));
        reg.Write(DebloatHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\appDiagnostics", "Value", new RegistryData(DebloatValueKind.String, "Allow"));
        foreach (var name in new[] { "SystemPaneSuggestionsEnabled", "SubscribedContent-338388Enabled", "SubscribedContent-338387Enabled", "RotatingLockScreenOverlayEnabled",
                     "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled", "SilentInstalledAppsEnabled",
                     "OemPreInstalledAppsEnabled", "PreInstalledAppsEnabled", "PreInstalledAppsEverEnabled" })
        {
            reg.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ContentDelivery, name, 1);
        }

        // What the owner already changed by hand: extensions shown, Task View hidden, Start suggestions half off.
        reg.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt", 0);
        reg.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "ShowTaskViewButton", 0);
        reg.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "TaskbarMn", 0);
        reg.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ContentDelivery, "SystemPaneSuggestionsEnabled", 0);
        reg.SetDword(DebloatHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0);
        reg.SetDword(DebloatHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0);
        reg.SetDword(DebloatHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0);

        // Services with their Windows 11 defaults.
        system.SetService("dmwappushservice", DebloatStartMode.Manual);
        system.SetService("XblAuthManager", DebloatStartMode.Manual);
        system.SetService("XblGameSave", DebloatStartMode.Manual);
        system.SetService("XboxNetApiSvc", DebloatStartMode.Manual);
        system.SetService("XboxGipSvc", DebloatStartMode.Manual);
        system.SetService("Fax", DebloatStartMode.Manual);
        system.SetService("WerSvc", DebloatStartMode.Manual);
        system.SetService("Spooler", DebloatStartMode.Automatic);
        system.SetService("RemoteRegistry", DebloatStartMode.Disabled);
        system.SetService("MapsBroker", DebloatStartMode.AutomaticDelayed);
        system.SetService("RetailDemo", DebloatStartMode.Manual);
        system.SetService("wisvc", DebloatStartMode.Manual);

        // Telemetry tasks (KernelCeipTask no longer ships with Windows 11).
        foreach (var task in DebloatCatalog.All.SelectMany(t => t.Operations).OfType<ScheduledTaskOp>())
        {
            if (!task.Path.EndsWith("KernelCeipTask", StringComparison.OrdinalIgnoreCase))
            {
                system.Tasks[task.Path] = true;
            }
        }

        // Store apps on a typical new Windows 11 24H2 desktop.
        foreach (var app in new[]
                 {
                     "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.GetHelp",
                     "Microsoft.Getstarted", "Microsoft.WindowsFeedbackHub", "Microsoft.Todos", "Microsoft.WindowsMaps", "MSTeams", "Clipchamp.Clipchamp",
                     "Microsoft.MicrosoftOfficeHub", "Microsoft.PowerAutomateDesktop", "Microsoft.Copilot", "Microsoft.OutlookForWindows", "Microsoft.YourPhone",
                     "SpotifyAB.SpotifyMusic", "7EE7776C.LinkedInforWindows", "Microsoft.WindowsCamera", "Microsoft.WindowsCalculator", "Microsoft.Windows.Photos",
                     "Microsoft.Paint", "Microsoft.WindowsNotepad", "Microsoft.ScreenSketch", "Microsoft.WindowsTerminal", "Microsoft.GamingApp",
                     "Microsoft.XboxGamingOverlay", "Microsoft.WindowsStore",
                 })
        {
            system.Apps.Add(app);
        }

        system.SetService("lfsvc", DebloatStartMode.Manual);
        system.SetService("CscService", DebloatStartMode.Manual);
        system.SetService("diagnosticshub.standardcollector.service", DebloatStartMode.Manual);

        // Software the other-apps tweaks look for: Chrome, OneDrive for this account and the NVIDIA driver (the sample
        // PC has a GeForce card); Office, Brave and Firefox are not installed, so those rows show "Not available".
        reg.CreateKey(DebloatHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
        reg.CreateKey(DebloatHive.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global");
        reg.SetDword(DebloatHive.CurrentUser, @"Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}", "System.IsPinnedToNameSpaceTree", 1);
        foreach (var root in new[] { @"SOFTWARE\", @"SOFTWARE\WOW6432Node\" })
        {
            reg.Write(DebloatHive.LocalMachine, root + @"Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\DelegateFolders\{F5FB2C77-0E2F-4A16-A381-3E560C68BC83}",
                string.Empty, new RegistryData(DebloatValueKind.String, "Removable Drives"));
        }

        system.Apps.Add("MicrosoftWindows.Client.WebExperience");
        system.Apps.Add("Microsoft.ZuneMusic");
        system.Apps.Add("Microsoft.WindowsAlarms");
        system.Apps.Add("Flipboard.Flipboard");
        system.Features["Recall"] = FeatureState.Enabled;
        system.Detections["ps:onedrive.uninstall"] = false;
        system.Detections["ps:fax.queue"] = false;
        system.Detections["ps:reservedstorage"] = false;
        system.Printers.AddRange(["Microsoft Print to PDF", "Microsoft XPS Document Writer", "HP OfficeJet Pro 9010 series"]);
        return system;
    }
}
