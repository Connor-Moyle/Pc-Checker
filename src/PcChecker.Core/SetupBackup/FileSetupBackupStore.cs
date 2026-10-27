using System.Text;
using PcChecker.Core.Settings;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Keeps automatic backups in a <c>backups</c> folder next to settings.json (so demo mode never mixes with the real
/// ones). Files are written to a temporary name first and then moved into place, so a crash never leaves half a file.
/// Old backups are never deleted by the app, so the folder grows by one full backup per import or reset; the folder can
/// be opened from Settings and old files deleted by hand (docs/FEATURES.md says so).
/// </summary>
public sealed class FileSetupBackupStore : ISetupBackupStore
{
    public const string FolderName = "backups";

    public FileSetupBackupStore(ISettingsService settings)
        : this(Path.Combine(settings.SettingsDirectory, FolderName))
    {
    }

    public FileSetupBackupStore(string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        BackupDirectory = backupDirectory;
    }

    public string BackupDirectory { get; }

    public bool CanOpenBackupDirectory => true;

    public async Task<string> SaveAutomaticAsync(string text, DateTime time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            Directory.CreateDirectory(BackupDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The backups folder {BackupDirectory} could not be created: {ex.Message}", ex);
        }

        var name = SetupFileFormat.AutomaticBackupName(time);
        var path = Path.Combine(BackupDirectory, name);
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(BackupDirectory, Path.GetFileNameWithoutExtension(name) + "-" + n + SetupFileFormat.FileExtension);
        }

        await WriteFileAsync(path, text, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public IReadOnlyList<SetupBackupFile> ListAutomatic()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory))
            {
                return [];
            }

            return new DirectoryInfo(BackupDirectory)
                .EnumerateFiles("setup-*" + SetupFileFormat.FileExtension)
                .Select(f => new SetupBackupFile(f.FullName, f.Name, new DateTimeOffset(f.LastWriteTime), f.Length))
                .OrderByDescending(f => f.Created)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public Task<string> ReadAsync(string path, CancellationToken cancellationToken) => ReadFileAsync(path, cancellationToken);

    public Task WriteAsync(string path, string text, CancellationToken cancellationToken) => WriteFileAsync(path, text, cancellationToken);

    /// <summary>Reads a setup file the user picked, refusing anything far larger than a setup could be.</summary>
    public static async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new IOException($"{path} was not found.");
            }

            if (info.Length > SetupFileFormat.MaxFileBytes)
            {
                throw new IOException("This file is too large to be a PC Checker setup backup.");
            }

            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"{path} could not be opened: {ex.Message}", ex);
        }
    }

    /// <summary>Writes through a temporary file next to <paramref name="path"/>, then moves it into place.</summary>
    public static async Task WriteFileAsync(string path, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryRemoveTemp(temp);
            throw new IOException($"{path} could not be written: {ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            TryRemoveTemp(temp);
            throw;
        }
    }

    /// <summary>Removes the half-written temporary file of a failed write (never the user's file itself).</summary>
    private static void TryRemoveTemp(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do; the next write replaces it.
        }
    }
}
