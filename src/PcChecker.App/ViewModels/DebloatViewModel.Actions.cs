using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
            return;
        }

        if (rows.Count == 0)
        {
            _notifications.Info("Nothing to do", revert
                ? "None of the selected tweaks is applied, or they cannot be undone here."
                : "The selected tweaks are already applied or not available on this Windows.");
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

        foreach (var row in rows)
        {
            row.IsBusy = true;
        }

        DebloatRunResult? result = null;
        var ids = tweaks.Select(t => t.Id).ToList();
        try
        {
            await RunBusyAsync(async ct =>
            {
                var progress = new Progress<ProgressUpdate>(p => BusyText = p.Detail is null ? p.Stage : $"{p.Stage}: {p.Detail}");
                result = revert
                    ? await _debloat.RevertAsync(ids, progress, ct)
                    : await _debloat.ApplyAsync(ids, progress, ct);
            }, revert ? "Reverting tweaks…" : "Applying tweaks…");

            // Cancel stops the batch between tweaks, so the states are re-read even then. Only the tweaks in the batch
            // (and those sharing a setting with them) are checked, and this check can be cancelled on its own.
            await RunBusyAsync(async ct => ApplyStates(await _debloat.GetStatesAsync(ids, ct), partial: true), "Checking the result…");
        }
        finally
        {
            foreach (var row in rows)
            {
                row.IsBusy = false;
            }
        }

        if (result is not null)
        {
            ShowResults(result, revert);
        }
    }

    private void ShowResults(DebloatRunResult result, bool revert)
    {
        Results.Clear();
        foreach (var r in result.Results.OrderBy(r => r.Outcome switch { TweakOutcome.Failed => 0, TweakOutcome.Skipped => 1, _ => 2 }))
        {
            Results.Add(new DebloatResultRow(r));
        }

        HasResults = Results.Count > 0;
        ResultsTitle = revert ? "Revert finished" : "Debloat finished";
        ResultsMessage = result.Summary;
        ResultsSeverity = result.Failed > 0
            ? (result.Succeeded > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error)
            : InfoBarSeverity.Success;
        IsRestartExplorerSuggested |= result.RestartExplorerSuggested;
        IsRebootRequired |= result.RebootRequired;
        _logger.LogInformation("Debloat {Verb}: {Summary}", revert ? "revert" : "apply", result.Summary);

        var kind = result.Failed > 0 ? NotificationKind.Warning : NotificationKind.Success;
        _notifications.Show(ResultsTitle, result.Summary, kind);
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
