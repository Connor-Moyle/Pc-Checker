using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Dialogs;
using PcChecker.Core.Localization;
using PcChecker.Core.SetupBackup;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Settings → General → Your setup: back up the setup to a .pcchecker file, restore it with a preview of what changes,
/// reset single parts, and undo the last import or reset. The work is done by <see cref="ISetupBackupService"/>.
/// </summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoSetupChange))]
    private string? _setupUndoText;

    [ObservableProperty]
    private string _setupBackupsText = string.Empty;

    /// <summary>True while the last import or reset of this session can be undone.</summary>
    public bool CanUndoSetupChange => !string.IsNullOrEmpty(SetupUndoText);

    public bool CanOpenSetupBackups => _setupBackup.CanOpenBackupDirectory;

    private void HookSetupBackup()
    {
        _setupBackup.LastChangeChanged += (_, _) => DispatcherHelper.PostToUi(RefreshSetupState);
        RefreshSetupState();
    }

    private void RefreshSetupState()
    {
        var last = _setupBackup.LastChange;
        var at = last?.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        SetupUndoText = last is null ? null
            : last.Source == SetupChangeSource.Import ? Loc.F("Imported at {0}: {1}", at, SetupSectionText.List(last.Sections))
            : Loc.F("Reset at {0}: {1}", at, SetupSectionText.List(last.Sections));

        var backups = _setupBackup.ListBackups();
        SetupBackupsText = backups.Count switch
        {
            0 => Loc.T("A backup of your current setup is saved automatically before every import or reset."),
            1 => Loc.F("1 automatic backup, made {0}.", backups[0].Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
            _ => Loc.F("{0} automatic backups, the newest made {1}.", backups.Count, backups[0].Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
        };
    }

    /// <summary>Back up: explains what goes in (and what never does), then asks where to save.</summary>
    [RelayCommand]
    private async Task ExportSetupAsync()
    {
        int runs = 0;
        try
        {
            runs = await _setupBackup.CountBenchmarkRunsAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // The checkbox is simply disabled; the rest of the setup can still be saved.
        }

        var choices = new SetupExportChoices(runs);
        if (!await _dialogs.ShowContentAsync(Loc.T("Back up your setup"), new SetupExportContent { DataContext = choices }, Loc.T("Choose where to save")))
        {
            return;
        }

        var path = await _dialogs.SaveFileAsync(Loc.T("Save your setup"), SetupFileFormat.ExportName(DateTime.Now), "PC Checker setup (*.pcchecker)|*.pcchecker");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _setupBackup.ExportAsync(path, new SetupExportOptions(choices.IncludeBenchmarkHistory), ct);
            if (result.Success)
            {
                _notifications.Success(Loc.T("Setup saved"), Loc.F("Your setup was saved to {0}.", path));
            }
            else
            {
                ErrorMessage = result.Message;
            }
        }, Loc.T("Saving your setup…"));
    }

    /// <summary>Restore: pick a file, check it, show what each part changes, then import the ticked parts.</summary>
    [RelayCommand]
    private async Task ImportSetupAsync()
    {
        var path = await _dialogs.OpenFileAsync(Loc.T("Choose a setup backup"), SetupFileFormat.FileFilter);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SetupImportPreview? preview = null;
        await RunBusyAsync(async ct =>
        {
            var result = await _setupBackup.PreviewAsync(path, ct);
            if (result.Success)
            {
                preview = result.Value;
            }
            else
            {
                ErrorMessage = result.Message;
            }
        }, Loc.T("Reading the file…"));
        if (preview is null)
        {
            return;
        }

        var name = System.IO.Path.GetFileName(path);
        if (!preview.Sections.Any(s => s.CanImport))
        {
            await _dialogs.ShowMessageAsync(Loc.T("Nothing to import"),
                Loc.T("None of the parts in this file can be used by this version of PC Checker.") + "\n\n"
                + string.Join("\n", preview.Sections.Select(s => $"{s.Title}: {s.Summary}")));
            return;
        }

        var choices = new SetupImportChoices(preview, name);
        if (!await _dialogs.ShowContentAsync(Loc.T("Restore your setup"), new SetupImportContent { DataContext = choices }, Loc.T("Import selected")))
        {
            return;
        }

        var chosen = choices.SelectedIds;
        if (chosen.Count == 0)
        {
            _notifications.Info(Loc.T("Nothing imported"), Loc.T("No part was ticked, so nothing changed."));
            return;
        }

        if (chosen.Contains(SetupSectionIds.Schedules) && CanScheduleTasks
            && SetupScheduleSync.PlanImport(Settings.Current, preview.Document, DateTimeOffset.Now) is { Count: > 0 } steps)
        {
            bool ok = await _dialogs.ConfirmAsync(Loc.T("Change scheduled tasks?"),
                Loc.T("Importing \"Schedules and tray\" changes PC Checker's tasks in Windows Task Scheduler:") + "\n\n"
                + string.Join("\n", SetupScheduleSync.Describe(steps)) + "\n\n"
                + Loc.T("Scheduled jobs start PC Checker with administrator rights (no UAC prompt), even while the app is closed. Turn a schedule off in Settings at any time to remove its task."),
                Loc.T("Continue"));
            if (!ok)
            {
                return;
            }
        }

        await RunSetupChangeAsync(ct => _setupBackup.ImportAsync(preview.Document, chosen, name, ct), Loc.T("Importing your setup…"),
            Loc.T("Setup imported"));
    }

    /// <summary>Reset a part: tick the parts to put back to their defaults, confirm, and it is done (with a backup first).</summary>
    [RelayCommand]
    private async Task ResetSetupSectionsAsync()
    {
        var choices = new SetupResetChoices();
        if (!await _dialogs.ShowContentAsync(Loc.T("Reset parts of your setup"), new SetupResetContent { DataContext = choices }, Loc.T("Reset ticked parts"), isDanger: true))
        {
            return;
        }

        var chosen = choices.SelectedIds;
        if (chosen.Count == 0)
        {
            _notifications.Info(Loc.T("Nothing reset"), Loc.T("No part was ticked, so nothing changed."));
            return;
        }

        await RunSetupChangeAsync(ct => _setupBackup.ResetAsync(chosen, ct), Loc.T("Resetting…"), Loc.T("Back to defaults"));
    }

    /// <summary>Undo: restores the parts the last import or reset changed, from the backup made just before it.</summary>
    [RelayCommand]
    private async Task UndoSetupChangeAsync()
    {
        var last = _setupBackup.LastChange;
        if (last is null)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync(Loc.T("Undo the last change?"),
            Loc.F("These parts go back to how they were before: {0}. Anything you changed in them since then is replaced too.", SetupSectionText.List(last.Sections)),
            Loc.T("Undo"));
        if (!ok)
        {
            return;
        }

        await RunSetupChangeAsync(ct => _setupBackup.UndoAsync(ct), Loc.T("Undoing…"), Loc.T("Change undone"));
    }

    [RelayCommand]
    private void OpenSetupBackupsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_setupBackup.BackupDirectory);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Could not create the backups folder: {0}", ex.Message);
            return;
        }

        Open(_setupBackup.BackupDirectory);
    }

    /// <summary>
    /// Runs an import, reset or undo (it cannot be cancelled half-way), then refreshes this page, applies the theme and
    /// says what happened, including anything that was skipped.
    /// </summary>
    private async Task RunSetupChangeAsync(Func<CancellationToken, Task<Core.Common.OperationResult<SetupChangeResult>>> change, string busyText, string doneTitle)
    {
        Core.Common.OperationResult<SetupChangeResult>? result = null;
        await RunBusyAsync(async ct => result = await change(ct), busyText, cancellable: false);
        if (result is null)
        {
            return;
        }

        if (!result.Success || result.Value is null)
        {
            ErrorMessage = result.Message;
            return;
        }

        LoadFromSettings();
        ApplyTheme(Settings.Current.General.Theme);
        var language = Settings.Current.General.Language;
        LanguageRestartNeeded = LanguageCatalog.Resolve(language, CultureInfo.CurrentUICulture)
            != LanguageCatalog.Resolve(_languageAtStart, CultureInfo.CurrentUICulture);
        await RefreshScheduleStatusAsync(CancellationToken.None);
        RefreshSetupState();

        var value = result.Value;
        var message = value.BackupPath is null
            ? Loc.F("Changed: {0}.", SetupSectionText.List(value.Sections))
            : Loc.F("Changed: {0}. Your previous setup was backed up first; use Undo under Your setup to go back.", SetupSectionText.List(value.Sections));
        if (value.Notes.Count == 0)
        {
            _notifications.Success(doneTitle, message);
        }
        else
        {
            await _dialogs.ShowMessageAsync(doneTitle, message + "\n\n" + string.Join("\n", value.Notes));
        }
    }
}
