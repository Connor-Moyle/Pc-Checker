using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>Demo Windows features, capabilities and inbox apps with in-memory state changes.</summary>
public sealed class DemoWindowsFeatureService : IWindowsFeatureService
{
    private readonly object _gate = new();
    private readonly List<WindowsFeature> _features =
    [
        new("Microsoft-Hyper-V-All", "Hyper-V", "Run virtual machines.", FeatureKind.OptionalFeature, FeatureState.Disabled, RiskLevel.Moderate, false, "Enable only if you use virtual machines.", null, false),
        new("Microsoft-Windows-Subsystem-Linux", "Windows Subsystem for Linux", "Run Linux tools on Windows.", FeatureKind.OptionalFeature, FeatureState.Enabled, RiskLevel.Safe, false, null, null, false),
        new("NetFx3", ".NET Framework 3.5", "Needed by some older programs.", FeatureKind.OptionalFeature, FeatureState.Disabled, RiskLevel.Safe, false, null, null, false),
        new("Printing-PrintToPDFServices-Features", "Microsoft Print to PDF", "Save any document as a PDF.", FeatureKind.OptionalFeature, FeatureState.Enabled, RiskLevel.Safe, false, null, null, false),
        new("OpenSSH.Client~~~~0.0.1.0", "OpenSSH Client", "Connect to remote machines with ssh.", FeatureKind.Capability, FeatureState.Enabled, RiskLevel.Safe, false, null, null, false),
        new("Microsoft.BingWeather", "Weather", "Microsoft weather app.", FeatureKind.InboxApp, FeatureState.Enabled, RiskLevel.Safe, true, "Safe to remove; reinstall from the Store any time.", "Microsoft.BingWeather_4.53.52762.0_x64__8wekyb3d8bbwe", false),
        new("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection", "Card games.", FeatureKind.InboxApp, FeatureState.Enabled, RiskLevel.Safe, true, "Safe to remove.", "Microsoft.MicrosoftSolitaireCollection_4.19.4080.0_x64__8wekyb3d8bbwe", false),
        new("Microsoft.XboxApp", "Xbox", "Xbox app and Game Bar companion.", FeatureKind.InboxApp, FeatureState.Enabled, RiskLevel.Moderate, true, "Removing it breaks Game Bar and Game Pass.", "Microsoft.GamingApp_2409.1001.9.0_x64__8wekyb3d8bbwe", false),
        new("Microsoft.WindowsStore", "Microsoft Store", "App store.", FeatureKind.InboxApp, FeatureState.Enabled, RiskLevel.Advanced, true, "Do not remove: many apps update through the Store.", "Microsoft.WindowsStore_22410.1401.6.0_x64__8wekyb3d8bbwe", true),
    ];

    public async Task<IReadOnlyList<WindowsFeature>> ListAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading features", 30));
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _features.ToList();
        }
    }

    public Task<OperationResult> EnableAsync(WindowsFeature feature, CancellationToken cancellationToken) =>
        Change(feature, FeatureState.Enabled, $"{feature.DisplayName} enabled (demo).", cancellationToken);

    public Task<OperationResult> DisableAsync(WindowsFeature feature, CancellationToken cancellationToken) =>
        Change(feature, FeatureState.Disabled, $"{feature.DisplayName} disabled (demo).", cancellationToken);

    public Task<OperationResult> RemoveInboxAppAsync(WindowsFeature feature, bool allUsers, CancellationToken cancellationToken) =>
        Change(feature, FeatureState.NotPresent, $"{feature.DisplayName} removed{(allUsers ? " for all users" : string.Empty)} (demo).", cancellationToken);

    private async Task<OperationResult> Change(WindowsFeature feature, FeatureState state, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feature);
        await DemoData.DelayAsync(cancellationToken, 500, 1000).ConfigureAwait(false);
        lock (_gate)
        {
            int i = _features.FindIndex(f => f.Name == feature.Name);
            if (i >= 0)
            {
                _features[i] = _features[i] with { State = state };
            }
        }

        return OperationResult.Ok(message, rebootRequired: feature.Kind == FeatureKind.OptionalFeature);
    }
}
