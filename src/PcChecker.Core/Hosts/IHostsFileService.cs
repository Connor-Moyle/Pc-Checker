using PcChecker.Core.Common;

namespace PcChecker.Core.Hosts;

/// <summary>
/// Reads and saves the Windows hosts file. Every save is checked against the file as it was loaded, keeps a
/// timestamped backup in PC Checker's data folder and goes through the change guard.
/// </summary>
public interface IHostsFileService
{
    /// <summary>The hosts file Windows uses (normally %SystemRoot%\System32\drivers\etc\hosts).</summary>
    string HostsPath { get; }

    /// <summary>Where the backups are kept.</summary>
    string BackupFolder { get; }

    Task<HostsSnapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="text"/> after backing up the current file. Fails without writing when the file changed
    /// since <paramref name="loaded"/> was read. Returns the backup that was made (null when there was no file).
    /// </summary>
    Task<OperationResult<HostsBackup?>> SaveAsync(HostsSnapshot loaded, string text, string description, CancellationToken cancellationToken);

    /// <summary>The backups, newest first.</summary>
    Task<IReadOnlyList<HostsBackup>> GetBackupsAsync(CancellationToken cancellationToken);

    /// <summary>The text of a backup, decoded the way the hosts file is.</summary>
    Task<OperationResult<string>> ReadBackupAsync(HostsBackup backup, CancellationToken cancellationToken);
}
