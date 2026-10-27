using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Views.Dialogs;
using PcChecker.Core.Common;
using PcChecker.Core.Hosts;

namespace PcChecker.App.ViewModels;

/// <summary>Editing, saving, backups and the other actions of the Hosts file page.</summary>
public sealed partial class HostsViewModel
{
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (IsDirty && !await _dialogs.ConfirmAsync(Loc.T("Discard your changes?"),
                Loc.T("Reading the file again throws away the edits you have not saved."), Loc.T("Discard and reload"), isDanger: true))
        {
            return;
        }

        await LoadCoreAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task DiscardAsync() => ReloadAsync();

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private async Task SaveAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        ApplyRawText();
        var (added, removed, toggled) = HostsFileRules.Compare(_snapshot.Document, _document);
        var message = new StringBuilder();
        message.Append(Loc.F("{0} added, {1} removed, {2} turned on or off.", added, removed, toggled)).Append("\n\n");
        message.Append(Loc.F("A copy of the current file is kept in {0}, a restore point is created first per your settings, and the DNS cache is cleared afterwards so the change works straight away.", _hosts.BackupFolder));
        int invalid = _document.Lines.Count(l => l.Kind == HostsLineKind.Invalid);
        if (invalid > 0)
        {
            message.Append("\n\n").Append(Loc.F("{0} lines are not valid entries; Windows ignores them. They are kept as they are.", invalid));
        }

        if (UnwantedCount > 0)
        {
            message.Append("\n\n").Append(Loc.F("{0} entries still look unwanted.", UnwantedCount));
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Save the hosts file?"), message.ToString(), Loc.T("Save")))
        {
            return;
        }

        await WriteAsync(HostsParser.Serialize(_document), Loc.T("Save the hosts file"), Loc.T("Saving the hosts file…"));
    }

    [RelayCommand]
    private async Task AddEntryAsync()
    {
        var editor = new HostsEntryEditor();
        if (await EditAsync(Loc.T("Add an entry"), editor, Loc.T("Add")) is not { } line)
        {
            return;
        }

        ApplyRawText();
        ReplaceLine(_document.Lines.Count, line);
        SelectedRow = Rows.FirstOrDefault(r => r.LineIndex == _document.Lines.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditEntryAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var editor = new HostsEntryEditor
        {
            Address = row.Address,
            Names = row.NamesText,
            Comment = row.Comment,
            IsEnabled = row.Line.Enabled,
        };
        if (await EditAsync(Loc.T("Edit the entry"), editor, Loc.T("Done")) is { } line)
        {
            ReplaceLine(row.LineIndex, line);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleEntry()
    {
        if (SelectedRow is { } row)
        {
            ReplaceLine(row.LineIndex, row.Line with { Raw = null, Enabled = !row.Line.Enabled });
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveEntry()
    {
        if (SelectedRow is { } row)
        {
            ReplaceLine(row.LineIndex, null);
            _notifications.Info(Loc.T("Entry removed"), Loc.T("Save to apply it, or Discard to bring it back."));
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyLine()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(HostsParser.Format(row.Line));
            _notifications.Success(Loc.T("Copied"), Loc.T("The line is on the clipboard."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            _notifications.Warning(Loc.T("Could not copy"), ex.Message);
        }
    }

    [RelayCommand]
    private void ShowUnwanted()
    {
        SelectedViewIndex = 0;
        SearchText = null;
        SelectedFilterIndex = 4;
    }

    [RelayCommand]
    private async Task ResetToDefaultAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Reset the hosts file to the Windows default?"),
                Loc.F("Every entry is removed and the file gets Microsoft's default text back. Use this when websites go to the wrong place or malware changed the file.\n\nA copy of the current file is kept in {0}, so Undo last save brings it back.", _hosts.BackupFolder),
                Loc.T("Reset"), isDanger: true))
        {
            return;
        }

        await WriteAsync(HostsFileRules.WindowsDefault, Loc.T("Reset the hosts file to the Windows default"), Loc.T("Resetting the hosts file…"));
    }

    [RelayCommand(CanExecute = nameof(CanUndoSave))]
    private async Task UndoLastSaveAsync()
    {
        if (_lastBackup is { } backup)
        {
            await RestoreAsync(backup, Loc.T("Undo the last save?"));
        }
    }

    [RelayCommand]
    private Task RestoreBackupAsync(HostsBackupRow? row) =>
        row is null ? Task.CompletedTask : RestoreAsync(row.Backup, Loc.T("Restore this backup?"));

    private async Task RestoreAsync(HostsBackup backup, string title)
    {
        string message = Loc.F("The hosts file goes back to the copy from {0}.", backup.Created.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        if (IsDirty)
        {
            message += "\n\n" + Loc.T("Your unsaved edits on this page are thrown away.");
        }

        message += "\n\n" + Loc.T("The current file is backed up first, so this can be undone too.");
        if (!await _dialogs.ConfirmAsync(title, message, Loc.T("Restore")))
        {
            return;
        }

        var text = await _hosts.ReadBackupAsync(backup, CancellationToken.None);
        if (!text.Success || text.Value is null)
        {
            _notifications.Error(Loc.T("Backup not restored"), text.Message);
            return;
        }

        await WriteAsync(text.Value, Loc.F("Restore the hosts file from the backup of {0}", backup.Created.ToString("g", System.Globalization.CultureInfo.InvariantCulture)),
            Loc.T("Restoring the hosts file…"));
    }

    /// <summary>Saves through the service, clears the DNS cache and reads the file again. Edits stay when it fails.</summary>
    private async Task WriteAsync(string text, string description, string busyText)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        OperationResult<HostsBackup?>? result = null;
        OperationResult? flush = null;
        await RunBusyAsync(async ct =>
        {
            result = await _hosts.SaveAsync(snapshot, text, description, ct);
            if (result.Success)
            {
                flush = await _network.FlushDnsAsync(ct);
                await LoadAsync(ct);
            }
        }, busyText, cancellable: false);

        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            SaveError = result.Message;
            _notifications.Error(Loc.T("The hosts file was not saved"), result.Message.Split('.')[0] + ".");
            return;
        }

        if (result.Value is { } backup)
        {
            _lastBackup = backup;
            CanUndoSave = true;
        }

        string note = flush is { Success: false } ? " " + Loc.T("The DNS cache could not be cleared; restart the PC if a site still goes to the old place.") : string.Empty;
        _notifications.Success(Loc.T("Hosts file saved"), result.Message + note);
    }

    /// <summary>Shows the add and edit box until the input is valid or the user cancels.</summary>
    private async Task<HostsLine?> EditAsync(string title, HostsEntryEditor editor, string confirmText)
    {
        while (true)
        {
            if (!await _dialogs.ShowContentAsync(title, new HostsEntryContent { DataContext = editor }, confirmText))
            {
                return null;
            }

            editor.Error = HostsParser.Validate(editor.Address, editor.Names);
            if (editor.Error is null)
            {
                var names = HostsParser.SplitNames(editor.Names);
                return HostsLine.CreateEntry(editor.Address.Trim(), names, editor.Comment, editor.IsEnabled);
            }
        }
    }

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        OperationResult? result = null;
        await RunBusyAsync(async ct => result = await _network.FlushDnsAsync(ct), Loc.T("Clearing the DNS cache…"), cancellable: false);
        if (result is { Success: true })
        {
            _notifications.Success(Loc.T("DNS cache cleared"), result.Message);
        }
        else if (result is not null)
        {
            _notifications.Warning(Loc.T("DNS cache not cleared"), result.Message);
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(_hosts.BackupFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.Warning(Loc.T("Could not open the folder"), ex.Message);
            return;
        }

        Open(_hosts.BackupFolder);
    }

    [RelayCommand]
    private void OpenHostsFolder()
    {
        string path = _snapshot?.Path ?? _hosts.HostsPath;
        Open(Path.GetDirectoryName(path) ?? path);
    }

    [RelayCommand]
    private void DismissSaveError() => SaveError = null;

    private void Open(string target)
    {
        var result = ShellLauncher.Open(target);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open the folder"), result.Message);
        }
    }
}
