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

    /// <summary>States of the given tweaks and of any tweak that changes the same settings (a quick re-check after a run).</summary>
    Task<IReadOnlyDictionary<string, TweakState>> GetStatesAsync(IEnumerable<string> tweakIds, CancellationToken cancellationToken);

    /// <summary>Whose per-user settings the page reads and changes, when that is not plain "your account"; otherwise null.</summary>
    string? UserScopeNote { get; }

    /// <summary>The system drive System Protection is turned on for (for example "C:").</summary>
    string SystemDrive { get; }

    Task<DebloatRunResult> ApplyAsync(IReadOnlyList<string> tweakIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<DebloatRunResult> RevertAsync(IReadOnlyList<string> tweakIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<DebloatRunResult> ApplyAsync(IReadOnlyList<string> tweakIds, DebloatRunOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<DebloatRunResult> RevertAsync(IReadOnlyList<string> tweakIds, DebloatRunOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Checks before a batch whether it needs a restore point that Windows is going to refuse (System Protection off,
    /// or the one-per-day limit), so the page can ask first instead of the batch being stopped.
    /// </summary>
    Task<DebloatRestorePointCheck> CheckRestorePointAsync(IReadOnlyList<string> tweakIds, bool revert, CancellationToken cancellationToken);

    /// <summary>Turns on System Protection for <see cref="SystemDrive"/> through the restore point service.</summary>
    Task<OperationResult> TurnOnSystemProtectionAsync(CancellationToken cancellationToken);

    Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken);

    /// <summary>Opens a Windows Settings page (ms-settings: URI) for a report-only note.</summary>
    OperationResult OpenSettings(string settingsUri);

    string ExportSelection(IEnumerable<string> ids);

    (IReadOnlyList<string> Ids, IReadOnlyList<string> Unmapped) ImportSelection(string json);
}
