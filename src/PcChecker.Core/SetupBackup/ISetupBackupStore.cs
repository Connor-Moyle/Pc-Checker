namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Where setup files are read and written: the file the user picked, and the automatic backups made before every
/// import or reset (<c>%LOCALAPPDATA%\PcChecker\backups\setup-&lt;time&gt;.pcchecker</c>).
/// </summary>
public interface ISetupBackupStore
{
    /// <summary>The folder that holds the automatic backups (a description in demo mode).</summary>
    string BackupDirectory { get; }

    /// <summary>True when <see cref="BackupDirectory"/> is a real folder that can be opened.</summary>
    bool CanOpenBackupDirectory { get; }

    /// <summary>Saves an automatic backup made at <paramref name="time"/> and returns where it went.</summary>
    Task<string> SaveAutomaticAsync(string text, DateTime time, CancellationToken cancellationToken);

    /// <summary>The automatic backups, newest first.</summary>
    IReadOnlyList<SetupBackupFile> ListAutomatic();

    /// <summary>Reads a setup file. Throws <see cref="IOException"/> with a plain message when it cannot.</summary>
    Task<string> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes an exported setup to the file the user chose (replacing it). Throws <see cref="IOException"/> when it cannot.</summary>
    Task WriteAsync(string path, string text, CancellationToken cancellationToken);
}
