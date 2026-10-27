using PcChecker.Core.Common;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Backs up and restores the user's setup (settings, profiles, schedules and, optionally, the benchmark history) as
/// one <c>.pcchecker</c> file. Secrets are never exported. Every import and reset first saves an automatic backup of
/// the current setup, which <see cref="UndoAsync"/> restores.
/// </summary>
public interface ISetupBackupService
{
    /// <summary>Where automatic backups are kept.</summary>
    string BackupDirectory { get; }

    bool CanOpenBackupDirectory { get; }

    /// <summary>The last import or reset of this session, which can be undone; null when there is none.</summary>
    SetupUndoPoint? LastChange { get; }

    /// <summary>Raised when <see cref="LastChange"/> changes.</summary>
    event EventHandler? LastChangeChanged;

    /// <summary>Number of saved benchmark runs, for the export dialog.</summary>
    Task<int> CountBenchmarkRunsAsync(CancellationToken cancellationToken);

    /// <summary>The automatic backups, newest first.</summary>
    IReadOnlyList<SetupBackupFile> ListBackups();

    /// <summary>Writes the current setup to <paramref name="path"/>.</summary>
    Task<OperationResult<string>> ExportAsync(string path, SetupExportOptions options, CancellationToken cancellationToken);

    /// <summary>Reads and checks <paramref name="path"/> and works out what importing it would change. Changes nothing.</summary>
    Task<OperationResult<SetupImportPreview>> PreviewAsync(string path, CancellationToken cancellationToken);

    /// <summary>Backs up the current setup, then imports the chosen sections of <paramref name="document"/>.</summary>
    Task<OperationResult<SetupChangeResult>> ImportAsync(SetupDocument document, IReadOnlyCollection<string> sections, string sourceName,
        CancellationToken cancellationToken);

    /// <summary>Backs up the current setup, then puts the chosen sections back to their defaults.</summary>
    Task<OperationResult<SetupChangeResult>> ResetAsync(IReadOnlyCollection<string> sections, CancellationToken cancellationToken);

    /// <summary>Restores the sections changed by <see cref="LastChange"/> from the automatic backup taken before it.</summary>
    Task<OperationResult<SetupChangeResult>> UndoAsync(CancellationToken cancellationToken);
}
