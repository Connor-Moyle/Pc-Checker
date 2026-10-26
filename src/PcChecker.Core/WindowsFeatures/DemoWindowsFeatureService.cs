using PcChecker.Core.Common;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>
/// Demo Windows features, capabilities and inbox apps for the sample gaming desktop (Windows 11 24H2), with in-memory
/// state changes so Enable/Disable/Remove visibly work. Pure .NET: built from the same catalogs the Windows service uses.
/// </summary>
public sealed class DemoWindowsFeatureService : IWindowsFeatureService
{
    private readonly object _gate = new();
    private readonly List<WindowsFeature> _features;

    public DemoWindowsFeatureService()
    {
        _features =
        [
            // Optional features (Get-WindowsOptionalFeature)
            Feature("NetFx3", FeatureState.Disabled),
            Feature("Microsoft-Hyper-V-All", FeatureState.Disabled),
            Feature("VirtualMachinePlatform", FeatureState.Enabled),
            Feature("HypervisorPlatform", FeatureState.Disabled),
            Feature("Microsoft-Windows-Subsystem-Linux", FeatureState.Enabled),
            Feature("Containers-DisposableClientVM", FeatureState.Disabled),
            Feature("Printing-PrintToPDFServices-Features", FeatureState.Enabled),
            Feature("Printing-XPSServices-Features", FeatureState.Disabled),
            Feature("WorkFolders-Client", FeatureState.Enabled),
            Feature("SMB1Protocol", FeatureState.Disabled),
            Feature("TelnetClient", FeatureState.Disabled),
            Feature("TFTP", FeatureState.Disabled),
            Feature("DirectPlay", FeatureState.Disabled),
            Feature("MediaPlayback", FeatureState.Enabled),
            Feature("WindowsMediaPlayer", FeatureState.Enabled),
            Feature("Internet-Explorer-Optional-amd64", FeatureState.Disabled),
            Feature("MSRDC-Infrastructure", FeatureState.Enabled),
            Feature("IIS-WebServerRole", FeatureState.Disabled),
            Feature("SearchEngine-Client-Package", FeatureState.Enabled),
            Feature("MicrosoftWindowsPowerShellV2Root", FeatureState.Enabled),
            Feature("Client-ProjFS", FeatureState.Disabled),
            Feature("Windows-Defender-ApplicationGuard", FeatureState.Disabled),
            Feature("Printing-Foundation-Features", FeatureState.Enabled),
            Feature("Printing-Foundation-InternetPrinting-Client", FeatureState.Enabled),
            Feature("SmbDirect", FeatureState.Enabled),
            Feature("WCF-Services45", FeatureState.Enabled),
            Feature("Microsoft-RemoteDesktopConnection", FeatureState.Enabled),
            Feature("ASUS-ArmouryCrate-Bridge", FeatureState.Enabled),

            // Capabilities (Get-WindowsCapability)
            Capability("OpenSSH.Client~~~~0.0.1.0", FeatureState.Enabled),
            Capability("OpenSSH.Server~~~~0.0.1.0", FeatureState.Disabled),
            Capability("Media.WindowsMediaPlayer~~~~0.0.12.0", FeatureState.Enabled),
            Capability("Print.Fax.Scan~~~~0.0.1.0", FeatureState.Enabled),
            Capability("Microsoft.Windows.Notepad~~~~0.0.1.0", FeatureState.Enabled),
            Capability("Microsoft.Windows.WordPad~~~~0.0.1.0", FeatureState.Enabled),
            Capability("Microsoft.Windows.MSPaint~~~~0.0.1.0", FeatureState.Enabled),
            Capability("Microsoft.Windows.PowerShell.ISE~~~~0.0.1.0", FeatureState.Enabled),
            Capability("Browser.InternetExplorer~~~~0.0.11.0", FeatureState.Disabled),
            Capability("Hello.Face.20134~~~~0.0.1.0", FeatureState.Disabled),
            Capability("App.StepsRecorder~~~~0.0.1.0", FeatureState.Enabled),
            Capability("MathRecognizer~~~~0.0.1.0", FeatureState.Enabled),
            Capability("XPS.Viewer~~~~0.0.1.0", FeatureState.Disabled),
            Capability("Microsoft.Windows.WirelessDisplay~~~~0.0.1.0", FeatureState.Enabled),
            Capability("Tools.Graphics.DirectX~~~~0.0.1.0", FeatureState.Disabled),
            Capability("Language.Basic~~~en-GB~0.0.1.0", FeatureState.Enabled),
            Capability("Language.Handwriting~~~en-GB~0.0.1.0", FeatureState.Enabled),
            Capability("Language.Speech~~~en-GB~0.0.1.0", FeatureState.Enabled),
            Capability("Language.TextToSpeech~~~en-GB~0.0.1.0", FeatureState.Enabled),
            Capability("Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0", FeatureState.Disabled),
            Capability("Rsat.Dns.Tools~~~~0.0.1.0", FeatureState.Disabled),
            Capability("Microsoft.Windows.Ethernet.Client.Intel.E1i68x64~~~~0.0.1.0", FeatureState.Enabled),

            // Inbox apps (Get-AppxPackage)
            App("Microsoft.BingWeather", "4.53.52762.0", false, true),
            App("Microsoft.BingNews", "4.55.62231.0", false, true),
            App("Microsoft.GetHelp", "10.2403.20861.0", false, true),
            App("Microsoft.Getstarted", "10.2312.1.0", false, true),
            App("Microsoft.MicrosoftOfficeHub", "18.2405.1252.0", false, true),
            App("Microsoft.MicrosoftSolitaireCollection", "4.19.4080.0", false, true),
            App("Microsoft.MicrosoftStickyNotes", "6.1.2.0", false, true),
            App("Microsoft.People", "10.2202.100.0", false, false),
            App("Microsoft.Todos", "2.126.4802.0", false, true),
            App("Microsoft.WindowsAlarms", "11.2403.4.0", false, true),
            App("Microsoft.WindowsFeedbackHub", "1.2404.13302.0", false, true),
            App("Microsoft.WindowsMaps", "11.2403.1.0", false, true),
            App("Microsoft.WindowsSoundRecorder", "11.2404.17.0", false, true),
            App("Microsoft.YourPhone", "1.24052.111.0", false, true),
            App("Microsoft.ZuneMusic", "11.2404.11.0", false, true),
            App("Microsoft.ZuneVideo", "10.22091.10061.0", false, true),
            App("Microsoft.PowerAutomateDesktop", "11.2404.9.0", false, true),
            App("Clipchamp.Clipchamp", "3.1.4.0", false, true),
            App("MSTeams", "24091.213.2846.3626", false, true),
            App("Microsoft.OutlookForWindows", "1.2024.515.100", false, true),
            App("Microsoft.Windows.DevHome", "0.1500.573.0", false, true),
            App("Microsoft.Copilot", "1.24061.78.0", false, true),
            App("MicrosoftWindows.Client.WebExperience", "524.21901.0.0", false, true),
            App("Microsoft.WindowsCamera", "2024.2404.5.0", false, true),
            App("Microsoft.WindowsCalculator", "11.2401.0.0", false, true),
            App("Microsoft.ScreenSketch", "11.2404.44.0", false, true),
            App("Microsoft.Paint", "11.2404.1020.0", false, true),
            App("Microsoft.WindowsNotepad", "11.2404.3.0", false, true),
            App("Microsoft.Windows.Photos", "2024.11050.3002.0", false, true),
            App("Microsoft.WindowsTerminal", "1.20.11381.0", false, true),
            App("Microsoft.GamingApp", "2409.1001.9.0", false, true),
            App("Microsoft.XboxGamingOverlay", "7.124.5142.0", false, true),
            App("Microsoft.XboxSpeechToTextOverlay", "1.97.17002.0", false, true),
            App("Microsoft.XboxIdentityProvider", "12.110.15002.0", false, true),
            App("Microsoft.Xbox.TCUI", "1.24.10001.0", false, true),
            App("Microsoft.StorePurchaseApp", "22405.1401.4.0", false, true),
            App("Microsoft.WindowsStore", "22410.1401.6.0", true, true),
            App("Microsoft.DesktopAppInstaller", "1.23.1911.0", false, true),
            App("Microsoft.SecHealthUI", "1000.26100.1.0", true, true),
            App("Microsoft.HEIFImageExtension", "1.1.2.0", false, true),
            App("Microsoft.WebpImageExtension", "1.1.1.0", false, true),
            App("Microsoft.VP9VideoExtensions", "1.1.1.0", false, true),
            App("Microsoft.WebMediaExtensions", "1.1.1.0", false, true),
            App("Microsoft.RawImageExtension", "2.3.1.0", false, false),
            App("Microsoft.549981C3F5F10", "4.2204.13303.0", false, false),
        ];
    }

    public async Task<IReadOnlyList<WindowsFeature>> ListAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading optional features", 10));
        await DemoData.DelayAsync(cancellationToken, 150, 400).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Reading capabilities", 45));
        await DemoData.DelayAsync(cancellationToken, 100, 250).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Reading built-in apps", 75));
        await DemoData.DelayAsync(cancellationToken, 100, 250).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Done", 100));
        lock (_gate)
        {
            return _features.ToList();
        }
    }

    public Task<OperationResult> EnableAsync(WindowsFeature feature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (feature.Kind == FeatureKind.InboxApp)
        {
            return Task.FromResult(OperationResult.Fail("Built-in apps are reinstalled from the Microsoft Store, not from here."));
        }

        var verb = feature.Kind == FeatureKind.Capability ? "installed" : "enabled";
        return ChangeAsync(feature, FeatureState.Enabled, $"{feature.DisplayName} {verb}.", feature.Kind == FeatureKind.OptionalFeature, cancellationToken);
    }

    public Task<OperationResult> DisableAsync(WindowsFeature feature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (feature.Kind == FeatureKind.InboxApp)
        {
            return RemoveInboxAppAsync(feature, false, cancellationToken);
        }

        var verb = feature.Kind == FeatureKind.Capability ? "removed" : "disabled";
        return ChangeAsync(feature, FeatureState.Disabled, $"{feature.DisplayName} {verb}.", feature.Kind == FeatureKind.OptionalFeature, cancellationToken);
    }

    public Task<OperationResult> RemoveInboxAppAsync(WindowsFeature feature, bool allUsers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (feature.Kind != FeatureKind.InboxApp)
        {
            return Task.FromResult(OperationResult.Fail($"{feature.DisplayName} is not a built-in app."));
        }

        if (!feature.Removable)
        {
            return Task.FromResult(OperationResult.Fail($"{feature.DisplayName} is marked as not removable by Windows or by PC Checker's safety list."));
        }

        return ChangeAsync(feature, FeatureState.NotPresent, $"{feature.DisplayName} removed{(allUsers ? " for all users" : string.Empty)}. Reinstall it from the Microsoft Store any time.", false, cancellationToken);
    }

    private async Task<OperationResult> ChangeAsync(WindowsFeature feature, FeatureState state, string message, bool rebootRequired, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 500, 1100).ConfigureAwait(false);
        lock (_gate)
        {
            int i = _features.FindIndex(f => f.Name == feature.Name);
            if (i < 0)
            {
                return OperationResult.Fail($"{feature.DisplayName} was not found.");
            }

            _features[i] = _features[i] with { State = state };
        }

        return OperationResult.Ok(rebootRequired ? message + " Restart Windows to finish." : message, rebootRequired);
    }

    private static WindowsFeature Feature(string name, FeatureState state) => FeatureMapper.MapOptionalFeature(name, state);

    private static WindowsFeature Capability(string name, FeatureState state) => FeatureMapper.MapCapability(name, state);

    private static WindowsFeature App(string name, string version, bool nonRemovable, bool provisioned) =>
        FeatureMapper.MapInboxApp(name, $"{name}_{version}_x64__8wekyb3d8bbwe", nonRemovable, provisioned);
}
