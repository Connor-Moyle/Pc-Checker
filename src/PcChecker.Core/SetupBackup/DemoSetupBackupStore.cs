using System.Text;
using PcChecker.Core.Settings;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// Demo mode: automatic backups are kept in memory (two sample ones are listed from the start), so trying import,
/// reset and undo never writes to the backups folder. Exporting and importing a file the user picked work as usual,
/// because the user chose that file.
/// </summary>
public sealed class DemoSetupBackupStore : ISetupBackupStore
{
    private const string Prefix = "demo-backup:";
    private readonly object _lock = new();
    private readonly List<(SetupBackupFile File, string Text)> _backups = [];

    public DemoSetupBackupStore()
    {
        var now = DateTime.Now;
        var sample = SetupExporter.Export(AppSettings.CreateDefault(), [], new SetupExportOptions(false), "0.4.0", now, "DEMO-PC");
        foreach (var age in new[] { TimeSpan.FromDays(9), TimeSpan.FromDays(2) })
        {
            Add(now - age, sample);
        }
    }

    public string BackupDirectory => "Demo mode: kept in memory, not saved";

    public bool CanOpenBackupDirectory => false;

    public async Task<string> SaveAutomaticAsync(string text, DateTime time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        return Add(time, text);
    }

    public IReadOnlyList<SetupBackupFile> ListAutomatic()
    {
        lock (_lock)
        {
            return _backups.Select(b => b.File).OrderByDescending(f => f.Created).ToList();
        }
    }

    public Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            lock (_lock)
            {
                var match = _backups.FirstOrDefault(b => b.File.Path == path);
                return match.Text is null ? throw new IOException("This demo backup no longer exists.") : Task.FromResult(match.Text);
            }
        }

        return FileSetupBackupStore.ReadFileAsync(path, cancellationToken);
    }

    public Task WriteAsync(string path, string text, CancellationToken cancellationToken) =>
        FileSetupBackupStore.WriteFileAsync(path, text, cancellationToken);

    private string Add(DateTime time, string text)
    {
        var name = SetupFileFormat.AutomaticBackupName(time);
        var file = new SetupBackupFile(Prefix + name, name, new DateTimeOffset(time), Encoding.UTF8.GetByteCount(text));
        lock (_lock)
        {
            _backups.Add((file, text));
        }

        return file.Path;
    }
}
