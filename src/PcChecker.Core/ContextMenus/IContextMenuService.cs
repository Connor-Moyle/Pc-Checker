using PcChecker.Core.Common;

namespace PcChecker.Core.ContextMenus;

/// <summary>
/// File Explorer's right-click menu: lists the entries programs added and turns them off or on without deleting them.
/// Windows' own entries are listed read-only. Every change goes through the change guard.
/// </summary>
public interface IContextMenuService
{
    /// <summary>Reads every entry (including Windows' own; the page decides what to show).</summary>
    Task<MenuScanResult> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Turns an entry off (LegacyDisable, Blocked list or Send to backup) or back on.</summary>
    Task<OperationResult> SetEnabledAsync(MenuEntry entry, bool enabled, CancellationToken cancellationToken);

    /// <summary>Switches the Windows 11 full (classic) right-click menu on or off for the current user.</summary>
    Task<OperationResult> SetClassicMenuAsync(bool classic, CancellationToken cancellationToken);

    /// <summary>Restarts File Explorer so menu changes show.</summary>
    Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken);

    /// <summary>The current user's Send to folder.</summary>
    string SendToFolder { get; }
}
