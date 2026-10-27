using PcChecker.Core.Common;

namespace PcChecker.Core.RegistryHealth;

/// <summary>
/// Finds registry entries that point at programs, files or folders that no longer exist, and removes them with a
/// backup first. Scans are read-only; every fix goes through the change guard and can be undone from a backup.
/// </summary>
public interface IRegistryHealthService
{
    /// <summary>Categories this implementation can check, in display order.</summary>
    IReadOnlyList<RegistryIssueCategory> Categories { get; }

    Task<RegistryScanResult> ScanAsync(IEnumerable<RegistryIssueCategory> categories, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<RegistryFixResult> FixAsync(IReadOnlyList<RegistryIssue> issues, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<IReadOnlyList<RegistryBackup>> ListBackupsAsync(CancellationToken cancellationToken);

    Task<OperationResult> RestoreBackupAsync(string backupId, CancellationToken cancellationToken);

    Task<OperationResult> DeleteBackupAsync(string backupId, CancellationToken cancellationToken);
}
