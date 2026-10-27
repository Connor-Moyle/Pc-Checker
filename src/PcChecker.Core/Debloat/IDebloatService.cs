using PcChecker.Core.Common;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Privacy, ads, AI, telemetry and UI-clutter tweaks collected from the well-known Windows debloat tools, applied as one
/// guarded batch with a restore point and captured "before" values so every reversible tweak can be undone.
/// </summary>
public interface IDebloatService
{
    IReadOnlyList<DebloatTweak> Tweaks { get; }
    IReadOnlyList<DebloatPreset> Presets { get; }

    /// <summary>Things other tools do that PC Checker deliberately does not, with reasons.</summary>
    IReadOnlyList<DebloatUnsafeItem> NotOffered { get; }

    /// <summary>Settings PC Checker only reports on, with a link into Windows Settings.</summary>
    IReadOnlyList<DebloatNote> Notes { get; }

    /// <summary>When true, app removals also remove the app for other users and new accounts (provisioned package).</summary>
    bool RemoveAppsForAllUsers { get; set; }

    Task<IReadOnlyDictionary<string, TweakState>> GetStatesAsync(CancellationToken cancellationToken);

    Task<DebloatRunResult> ApplyAsync(IReadOnlyList<string> tweakIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<DebloatRunResult> RevertAsync(IReadOnlyList<string> tweakIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken);

    /// <summary>Opens a Windows Settings page (ms-settings: URI) for a report-only note.</summary>
    OperationResult OpenSettings(string settingsUri);

    string ExportSelection(IEnumerable<string> ids);

    (IReadOnlyList<string> Ids, IReadOnlyList<string> Unmapped) ImportSelection(string json);
}
