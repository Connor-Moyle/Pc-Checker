using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.RegistryHealth;

namespace PcChecker.App.ViewModels;

/// <summary>The Backups section: every fix leaves a .reg backup that can be restored or deleted here.</summary>
public sealed partial class RegistryViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOldBackups))]
    [NotifyPropertyChangedFor(nameof(OldBackupsText))]
    private int _oldBackupCount;

    public ObservableCollection<RegistryBackupItem> Backups { get; } = [];

    public bool HasBackups => Backups.Count > 0;

    public bool HasOldBackups => OldBackupCount > 0;

    public string OldBackupsText => OldBackupCount == 1
        ? $"1 backup is older than {RegistryBackupCatalog.OldBackupDays} days."
        : $"{OldBackupCount} backups are older than {RegistryBackupCatalog.OldBackupDays} days.";

    public string BackupFolder => RegistryBackupCatalog.DefaultDirectory(Environment.GetEnvironmentVariable);

    [RelayCommand]
    private Task RefreshBackupsAsync() => RunBusyAsync(LoadBackupsAsync, "Loading registry backups…");

    [RelayCommand]
    private async Task RestoreBackupAsync(RegistryBackupItem? item)
    {
        if (item is null)
        {
            return;
        }

        string message =
            $"The {item.IssueText} saved on {item.CreatedText} ({item.Summary}) are put back into the registry.\n\n" +
            "A restore point is created first. Restored entries show up again in the next scan.";
        if (!await _dialogs.ConfirmAsync("Restore this backup?", message, "Restore"))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _service.RestoreBackupAsync(item.Id, ct);
            if (!result.Success)
            {
                ErrorMessage = result.Message;
                return;
            }

            _notifications.Success("Backup restored", result.Message);
            _logger.LogInformation("Registry backup {Id} restored.", item.Id);
            if (_lastScannedCategories.Count > 0)
            {
                BusyText = "Checking again…";
                var rescan = await _service.ScanAsync(_lastScannedCategories, null, ct);
                ApplyScan(rescan, _lastScannedCategories);
            }
        }, "Restoring the backup…", cancellable: false);
    }

    [RelayCommand]
    private async Task DeleteBackupAsync(RegistryBackupItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Delete this backup?",
                $"The backup from {item.CreatedText} ({item.IssueText}) is deleted. You will no longer be able to undo that fix from this page.",
                "Delete backup", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _service.DeleteBackupAsync(item.Id, ct);
            if (!result.Success)
            {
                ErrorMessage = result.Message;
            }

            await LoadBackupsAsync(ct);
        }, "Deleting the backup…");
    }

    [RelayCommand]
    private async Task DeleteOldBackupsAsync()
    {
        var old = Backups.Where(b => b.IsOld).ToList();
        if (old.Count == 0)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync($"Delete {old.Count} old {(old.Count == 1 ? "backup" : "backups")}?",
                $"Backups older than {RegistryBackupCatalog.OldBackupDays} days are deleted. The fixes they belong to can then no longer be undone from this page. Newer backups are kept.",
                "Delete old backups", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            int failed = 0;
            foreach (var backup in old)
            {
                var result = await _service.DeleteBackupAsync(backup.Id, ct);
                failed += result.Success ? 0 : 1;
            }

            await LoadBackupsAsync(ct);
            if (failed > 0)
            {
                ErrorMessage = $"{failed} old {(failed == 1 ? "backup" : "backups")} could not be deleted.";
            }
            else
            {
                _notifications.Success("Old backups deleted", $"{old.Count} {(old.Count == 1 ? "backup was" : "backups were")} removed.");
            }
        }, "Deleting old backups…");
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Registry backup folder could not be created.");
        }

        var result = ShellLauncher.Open(BackupFolder);
        if (!result.Success)
        {
            _notifications.Warning("Could not open the backup folder", result.Message);
        }
    }

    private async Task LoadBackupsAsync(CancellationToken cancellationToken)
    {
        var backups = await _service.ListBackupsAsync(cancellationToken);
        var now = DateTimeOffset.Now;
        var old = new HashSet<string>(RegistryBackupCatalog.OlderThan(backups, now).Select(b => b.Id), StringComparer.OrdinalIgnoreCase);
        Backups.Clear();
        foreach (var backup in backups.OrderByDescending(b => b.Created))
        {
            Backups.Add(new RegistryBackupItem(backup, old.Contains(backup.Id)));
        }

        OldBackupCount = old.Count;
        OnPropertyChanged(nameof(HasBackups));
    }
}
