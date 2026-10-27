using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>Debloat page commands: presets, selection, apply/revert with confirmation, Explorer restart, import/export.</summary>
public sealed partial class DebloatViewModel
{
    [RelayCommand]
    private void SelectPreset(DebloatPresetCard? card)
    {
        if (card is null)
        {
            return;
        }

        var ids = new HashSet<string>(card.Preset.TweakIds, StringComparer.OrdinalIgnoreCase);
        _suppressCounts = true;
        try
        {
            foreach (var row in _rows.Values)
            {
                row.IsSelected = ids.Contains(row.Id) && row.CanSelect && (IsAdvancedMode || !row.AdvancedOnly);
            }
        }
        finally
        {
            _suppressCounts = false;
        }

        ActivePresetName = card.Preset.IsCustom ? null : card.Name;
        foreach (var group in Groups)
        {
            if (group.Rows.Any(r => r.IsSelected))
            {
                group.IsExpanded = true;
            }
        }

        UpdateCounts();
        if (!card.Preset.IsCustom)
        {
            int skipped = ids.Count(id => _rows.TryGetValue(id, out var r) && !r.IsSelected);
            _notifications.Info($"{card.Name} selected",
                $"{SelectedCount} tweaks ticked{(skipped > 0 ? $" ({skipped} don't apply to this Windows or are already handled)" : string.Empty)}. Review them, then choose Apply selected.");
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _suppressCounts = true;
        try
        {
            foreach (var row in _rows.Values)
            {
                row.IsSelected = false;
            }
        }
        finally
        {
            _suppressCounts = false;
        }

        ActivePresetName = null;
        UpdateCounts();
    }

    [RelayCommand]
    private Task ApplySelectedAsync() =>
        RunAsync(_rows.Values.Where(r => r.IsSelected && IsAllowed(r) && r.CanApply).ToList(), revert: false);

    [RelayCommand]
    private Task RevertSelectedAsync() =>
        RunAsync(_rows.Values.Where(r => r.IsSelected && IsAllowed(r) && r.CanRevert).ToList(), revert: true);

    [RelayCommand]
    private Task ApplyOneAsync(DebloatTweakRow? row) => row is null ? Task.CompletedTask : RunAsync([row], revert: false);

    [RelayCommand]
    private Task RevertOneAsync(DebloatTweakRow? row) => row is null ? Task.CompletedTask : RunAsync([row], revert: true);

    private async Task RunAsync(IReadOnlyList<DebloatTweakRow> rows, bool revert)
    {
        if (IsBusy)
        {
            _notifications.Info(Loc.T("Please wait"), Loc.T("PC Checker is still busy on this page. Try again when it has finished."));
            return;
        }

        if (rows.Count == 0)
        {
            _notifications.Info(Loc.T("Nothing to do"), revert
                ? Loc.T("None of the selected tweaks is applied, or they cannot be undone here.")
                : Loc.T("The selected tweaks are already applied or not available on this Windows."));
            return;
        }

        var tweaks = rows.Select(r => r.Tweak).ToList();
        if (!revert && DebloatRules.ConflictingPairs(tweaks) is { Count: > 0 } conflicts)
        {
            // Two tweaks that write the same setting with different values: the result would depend on the order, and the
            // second one would save the first one's value as the "original" to revert to.
            var (a, b) = conflicts[0];
            _notifications.Warning("Two selected tweaks conflict",
                $"\"{a.Title}\" and \"{b.Title}\" change the same setting in different ways. Untick one of them, then choose Apply again.");
            return;
        }

        string verb = revert ? "Revert" : "Apply";
        string title = rows.Count == 1
            ? $"{verb} \"{rows[0].Title}\"?"
            : $"{verb} {rows.Count} debloat tweaks?";
        if (!await _dialogs.ConfirmAsync(title, DebloatConfirmationText.Build(tweaks, revert, RemoveAppsForAllUsers, PcChecker.Core.Hardware.LocalCpu.Name),
                rows.Count == 1 ? (rows[0].IsAppRemoval && !revert ? "Remove" : verb) : $"{verb} {rows.Count}",
                DebloatConfirmationText.IsDanger(tweaks, revert)))
        {
            return;
        }

        var ids = tweaks.Select(t => t.Id).ToList();

        // A batch with one-way changes or Windows feature changes only runs with a restore point. Ask first when Windows
        // is going to refuse it, instead of letting the batch stop with nothing changed.
        DebloatRestorePointCheck? check = null;
        if (!await RunBusyAsync(async ct => check = await _debloat.CheckRestorePointAsync(ids, revert, ct), Loc.T("Checking System Protection…")))
        {
            // Cancelled (or the check threw, which ErrorMessage shows): nothing has been changed, so stop here.
            if (!HasError)
            {
                _notifications.Info(Loc.T("Cancelled"), Loc.T("The batch was cancelled before it started. Nothing was changed."));
            }

            return;
        }

        var options = DebloatRunOptions.Default;
        bool? protectionOn = check?.ProtectionOn;
        if (check is { WouldBlock: true })
        {
            if (await AskWithoutRestorePointAsync(tweaks, revert, check.Problem, null, protectionOn) is not { } chosen)
            {
                return;
            }

            options = chosen;
            if (!chosen.WithoutRestorePoint)
            {
                // Only a successful "Turn on System Protection" comes back without WithoutRestorePoint.
                protectionOn = true;
            }
        }

        var run = await ExecuteAsync(rows, ids, revert, options);
        if (run.Result is { BlockedByRestorePoint: true } && !options.WithoutRestorePoint)
        {
            // The check could not foresee it (or Windows refused for another reason). Nothing was changed, so ask now,
            // offering only the fixes that can help with the reason Windows gave.
            var problem = DebloatRestorePointRules.ProblemAfterFailure(run.Result.RestorePointBlockedReason, check?.Problem ?? RestorePointProblem.None, protectionOn);
            if (await AskWithoutRestorePointAsync(tweaks, revert, problem, run.Result.RestorePointBlockedReason, protectionOn) is { } retry)
            {
                run = await ExecuteAsync(rows, ids, revert, retry);
            }
        }

        if (run.Result is null)
        {
            if (run.Error is null && !HasError)
            {
                _notifications.Info(Loc.T("Cancelled"), run.Changed > 0
                    ? Loc.F("The batch was stopped. {0} tweak(s) had already been changed and stay changed; the list shows each tweak's current state.", run.Changed)
                    : Loc.T("The batch was cancelled before anything was changed."));
            }

            return;
        }

        ShowResults(run.Result, run.States, revert);
    }

    /// <summary>What <see cref="ExecuteAsync"/> ran into: the result (null when it threw or was cancelled), the re-read
    /// states, the batch's own error message, and how many tweaks of the batch changed state.</summary>
    private sealed record BatchRun(DebloatRunResult? Result, IReadOnlyDictionary<string, TweakState>? States, string? Error, int Changed);

    /// <summary>Runs the batch, then reads the state of every tweak in it (and of those sharing a setting) again.</summary>
    private async Task<BatchRun> ExecuteAsync(IReadOnlyList<DebloatTweakRow> rows, IReadOnlyList<string> ids, bool revert, DebloatRunOptions options)
    {
        var statusBefore = rows.ToDictionary(r => r.Id, r => r.Status, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            row.IsBusy = true;
        }

        DebloatRunResult? result = null;
        IReadOnlyDictionary<string, TweakState>? states = null;
        string? batchError = null;
        try
        {
            await RunBusyAsync(async ct =>
            {
                var progress = new Progress<ProgressUpdate>(p => BusyText = p.Detail is null ? p.Stage : $"{p.Stage}: {p.Detail}");
                result = revert
                    ? await _debloat.RevertAsync(ids, options, progress, ct)
                    : await _debloat.ApplyAsync(ids, options, progress, ct);
            }, revert ? Loc.T("Reverting tweaks…") : Loc.T("Applying tweaks…"));
            batchError = ErrorMessage;

            // Cancel stops the batch between tweaks, so the states are re-read even then. Only the tweaks in the batch
            // (and those sharing a setting with them) are checked, and this check can be cancelled on its own. Each
            // result is judged against this re-read, not only against what the change reported.
            await RunBusyAsync(async ct =>
            {
                states = await _debloat.GetStatesAsync(ids, ct);
                ApplyStates(states, partial: true);
            }, Loc.T("Checking the result…"));
        }
        finally
        {
            foreach (var row in rows)
            {
                row.IsBusy = false;
            }
        }

        UserScopeNote = _debloat.UserScopeNote;
        if (batchError is not null)
        {
            // The re-read clears ErrorMessage when it starts; the batch's own error is the one to show.
            ErrorMessage = batchError;
        }

        int changed = states is null ? 0 : rows.Count(r => states.ContainsKey(r.Id) && statusBefore.TryGetValue(r.Id, out var was) && was != r.Status);
        return new BatchRun(result, states, batchError, changed);
    }

    /// <summary>
    /// Explains why the restore point cannot be made and offers: turn on System Protection, continue without a restore
    /// point (only the tweaks that can be reverted from this page run), or cancel. Returns the options to run with, or
    /// null for Cancel.
    /// </summary>
    private async Task<DebloatRunOptions?> AskWithoutRestorePointAsync(IReadOnlyList<DebloatTweak> tweaks, bool revert, RestorePointProblem problem, string? reason,
        bool? protectionOn = null)
    {
        const string title = "No restore point possible";
        bool canTurnOn = DebloatRestorePointRules.CanTurnOnHelp(problem, protectionOn);
        int needRestorePoint = tweaks.Count(t => DebloatRules.NeedsRestorePoint(t, revert));
        bool canContinue = DebloatRestorePointRules.CanContinueWithout(needRestorePoint, tweaks.Count);
        while (true)
        {
            string text = DebloatRestorePointRules.DialogText(problem, _debloat.SystemDrive, reason, needRestorePoint, tweaks.Count, canTurnOn);
            string? primary = canTurnOn ? "Turn on System Protection" : canContinue ? "Continue without" : null;
            string? secondary = canTurnOn && canContinue ? "Continue without" : null;
            if (primary is null)
            {
                await _dialogs.ShowMessageAsync(title, text);
                return null;
            }

            var choice = await _dialogs.ChooseAsync(title, text, primary, secondary);
            if (choice == DialogChoice.Cancel)
            {
                return null;
            }

            if (!(canTurnOn && choice == DialogChoice.Primary))
            {
                _logger.LogInformation("Debloat: continuing without a restore point ({Problem}).", problem);
                return new DebloatRunOptions(WithoutRestorePoint: true);
            }

            OperationResult? enabled = null;
            await RunBusyAsync(async ct => enabled = await _debloat.TurnOnSystemProtectionAsync(ct), Loc.T("Turning on System Protection…"), cancellable: false);
            if (enabled is { Success: true })
            {
                _notifications.Success("System Protection", enabled.Message);
                return DebloatRunOptions.Default;
            }

            // It could not be turned on: say why and offer what is left.
            reason = enabled?.Message ?? ErrorMessage;
            problem = RestorePointProblem.Failed;
            canTurnOn = false;
        }
    }

    private void ShowResults(DebloatRunResult result, IReadOnlyDictionary<string, TweakState>? states, bool revert)
    {
        var views = DebloatResultClassifier.ClassifyAll(result.Results, states, revert);
        Results.Clear();
        foreach (var view in views)
        {
            Results.Add(new DebloatResultRow(view));
        }

        HasResults = Results.Count > 0;
        IsResultsExpanded = HasResults;
        if (result.BlockedByRestorePoint)
        {
            ResultsTitle = Loc.T("Nothing was changed");
            ResultsMessage = DebloatRestorePointRules.Describe(RestorePointProblem.Failed, _debloat.SystemDrive, result.RestorePointBlockedReason);
            ResultsSeverity = InfoBarSeverity.Error;
            _logger.LogWarning("Debloat {Verb} not started: {Reason}", revert ? "revert" : "apply", result.RestorePointBlockedReason);
            _notifications.Show(ResultsTitle, ResultsMessage, NotificationKind.Warning);
            return;
        }

        var summary = DebloatResultClassifier.Summarize(views, revert, result.Note);
        ResultsTitle = summary.Title;
        ResultsMessage = summary.Message;
        ResultsSeverity = summary.Failed > 0
            ? (summary.Done > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error)
            : InfoBarSeverity.Success;
        IsRestartExplorerSuggested |= summary.NeedsExplorerRestart;
        IsSignOutSuggested |= summary.NeedsSignOut;
        IsRebootRequired |= summary.NeedsWindowsRestart;
        foreach (var failed in views.Where(v => v.Kind == DebloatResultKind.Failed))
        {
            _logger.LogWarning("Debloat {Tweak} not applied: {Message}", failed.TweakId, failed.Message);
        }

        _logger.LogInformation("Debloat {Verb}: {Summary}", revert ? "revert" : "apply", summary.Message);
        _notifications.Show(ResultsTitle, summary.Message, summary.Failed > 0 ? NotificationKind.Warning : NotificationKind.Success);
    }

    [RelayCommand]
    private void DismissResults()
    {
        HasResults = false;
        Results.Clear();
    }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        if (!await _dialogs.ConfirmAsync("Restart File Explorer?",
                "The taskbar and open File Explorer windows close for a moment and come back with the new settings. Save anything you were renaming or moving first.",
                "Restart Explorer"))
        {
            return;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct => result = await _debloat.RestartExplorerAsync(ct), "Restarting File Explorer…", cancellable: false);
        if (result is null)
        {
            return;
        }

        if (result.Success)
        {
            IsRestartExplorerSuggested = false;
            _notifications.Success("File Explorer restarted", result.Message);
        }
        else
        {
            _notifications.Warning("File Explorer", result.Message);
        }
    }

    [RelayCommand]
    private void DismissRestartExplorer() => IsRestartExplorerSuggested = false;

    [RelayCommand]
    private void DismissSignOut() => IsSignOutSuggested = false;

    [RelayCommand]
    private void OpenNote(DebloatNote? note)
    {
        if (note is null)
        {
            return;
        }

        var result = _debloat.OpenSettings(note.SettingsUri);
        if (!result.Success)
        {
            _notifications.Warning(note.Title, result.Message);
        }
        else if (result.Message.StartsWith("Demo", StringComparison.OrdinalIgnoreCase))
        {
            _notifications.Info(note.Title, result.Message);
        }
    }

    [RelayCommand]
    private async Task ExportSelectionAsync()
    {
        var ids = _rows.Values.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            _notifications.Info("Nothing to export", "Tick the tweaks you want to save first, or pick a preset.");
            return;
        }

        var path = await _dialogs.SaveFileAsync("Export debloat selection", "pc-checker-debloat.json", "Debloat selection (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, _debloat.ExportSelection(ids));
            _notifications.Success("Selection exported", $"{ids.Count} tweaks saved to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not export the debloat selection.");
            ErrorMessage = $"Could not save the file: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportSelectionAsync()
    {
        string? path;
        try
        {
            path = DriverFilePicker.PickFile("Import a debloat selection (PC Checker or WinUtil .json)", "Selection files (*.json)|*.json|All files (*.*)|*.*");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The file picker could not be shown.");
            return;
        }

        if (path is null)
        {
            return;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not read the file: {ex.Message}";
            return;
        }

        var (ids, unmapped) = _debloat.ImportSelection(json);
        _suppressCounts = true;
        int hidden = 0, unavailable = 0;
        try
        {
            foreach (var row in _rows.Values)
            {
                bool wanted = ids.Contains(row.Id, StringComparer.OrdinalIgnoreCase);
                bool allowed = IsAdvancedMode || !row.AdvancedOnly;
                if (wanted && !allowed)
                {
                    hidden++;
                }
                else if (wanted && !row.CanSelect)
                {
                    unavailable++;
                }

                row.IsSelected = wanted && allowed && row.CanSelect;
            }
        }
        finally
        {
            _suppressCounts = false;
        }

        ActivePresetName = "Imported";
        UpdateCounts();
        var notes = new List<string> { $"{SelectedCount} tweaks ticked from {Path.GetFileName(path)}." };
        if (hidden > 0)
        {
            notes.Add($"{hidden} need Advanced mode (Settings) to be shown.");
        }

        if (unavailable > 0)
        {
            notes.Add($"{unavailable} are not available on this Windows.");
        }

        if (unmapped.Count > 0)
        {
            notes.Add($"No PC Checker equivalent for: {string.Join(", ", unmapped.Take(8))}{(unmapped.Count > 8 ? ", …" : string.Empty)}.");
        }

        await _dialogs.ShowMessageAsync("Selection imported", string.Join("\n\n", notes) + "\n\nNothing has been changed yet. Review the ticks, then choose Apply selected.");
    }
}
