using System.Text;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Dialogs;
using PcChecker.Core.Common;
using PcChecker.Core.GameReady;
using PcChecker.Core.Input;
using PcChecker.Core.Restore;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Game ready fixes. Nothing here has its own tweak engine: Windows settings go through the Optimize page's
/// <see cref="OptimizationActionRunner"/>, refresh rates through the Displays service (with "Keep these settings?"),
/// the DirectX graphics settings through <see cref="IGameReadyService"/>, and everything else opens the page or Windows
/// setting that owns it. Every change asks first, is journaled and can be undone from its row.
/// </summary>
public sealed partial class GameReadyViewModel
{
    /// <summary>Seconds the "Keep these settings?" dialog waits before switching the monitor back.</summary>
    public const int KeepSettingsSeconds = DisplaysViewModel.KeepSettingsSeconds;

    private const int PollingSeconds = 5;

    [RelayCommand]
    private async Task FixAsync(GameReadyRow? row)
    {
        if (row?.Fix is not { } fix || IsBusy)
        {
            return;
        }

        switch (fix.Kind)
        {
            case GameReadyFixKind.OptimizationAction when fix.Target is { } actionId:
                if (await _runner.RunAsync(actionId))
                {
                    MarkFixed(row, null, () => _runner.RunAsync(actionId, revert: true));
                }

                break;
            case GameReadyFixKind.RefreshRate:
                await FixRefreshRateAsync(row, fix, confirm: true);
                break;
            case GameReadyFixKind.DirectXSetting or GameReadyFixKind.GpuPreference:
                if (await _dialogs.ConfirmAsync(row.Title, ConfirmText(row) + "\n\n" + _runner.RestorePointNote, fix.Label))
                {
                    await FixWithServiceAsync(row, fix);
                }

                break;
            case GameReadyFixKind.OpenPage:
                OpenPage(fix.Target);
                break;
            case GameReadyFixKind.OpenLink:
                OpenLink(fix.Target);
                break;
            case GameReadyFixKind.GpuControlPanel:
                var opened = _displays.OpenGpuControlPanel();
                if (!opened.Success)
                {
                    _notifications.Warning(Loc.T("Could not open"), opened.Message);
                }

                break;
            case GameReadyFixKind.MousePollingTest:
                await TestMouseAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task UndoAsync(GameReadyRow? row)
    {
        if (row?.Undo is not { } undo || IsBusy)
        {
            return;
        }

        if (await undo())
        {
            row.IsFixed = false;
            row.Undo = null;
            row.ResultFailed = false;
            row.ResultText = Loc.T("Undone.");
            UpdateSummary();
        }
    }

    private bool CanFixAll() => HasReport && SafeFixCount > 0;

    [RelayCommand(CanExecute = nameof(CanFixAll))]
    private async Task FixAllAsync()
    {
        var rows = ToDo.Where(r => r.IsSafeFix && !r.IsFixed).ToList();
        if (rows.Count == 0 || IsBusy)
        {
            return;
        }

        var text = new StringBuilder(Loc.T("These changes are made, one after the other:")).AppendLine().AppendLine();
        foreach (var row in rows)
        {
            text.Append("• ").Append(row.Title).Append(" → ").AppendLine(row.FixLabel);
        }

        text.AppendLine().Append(Loc.T("Each one can be undone from its row afterwards. One restore point is created first when that is turned on in Settings."));
        if (rows.Any(r => r.Fix?.Kind == GameReadyFixKind.RefreshRate))
        {
            text.AppendLine().AppendLine().Append(Loc.F("The refresh rate changes last; you get {0} seconds to keep it or switch back.", KeepSettingsSeconds));
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Fix all safe items?"), text.ToString(), Loc.T("Fix all")))
        {
            return;
        }

        int done = 0, failed = 0;
        var later = rows.Where(r => r.Fix?.Kind == GameReadyFixKind.RefreshRate).ToList();
        await RunBusyAsync(async ct =>
        {
            var outcome = await _batch.RunBatchAsync(Loc.T("Game ready: fix all safe items"), true, async batchToken =>
            {
                foreach (var row in rows.Except(later))
                {
                    batchToken.ThrowIfCancellationRequested();

                    // The batch hands its work to a pool thread; each fix runs on the UI thread again (the dispatcher
                    // carries the batch scope along), so rows, commands and notifications are only touched there.
                    bool ok = await OnUiAsync(() =>
                    {
                        BusyText = Loc.F("Fixing: {0}", row.Title);
                        return ApplyWithoutAskingAsync(row, batchToken);
                    });
                    if (ok)
                    {
                        done++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }, null, ct);
            if (outcome.RestorePointWarning is { } warning)
            {
                _notifications.Warning(Loc.T("No restore point"), warning);
            }
        }, Loc.T("Fixing the safe items…"));

        foreach (var row in later)
        {
            if (await FixRefreshRateAsync(row, row.Fix!, confirm: false))
            {
                done++;
            }
            else
            {
                failed++;
            }
        }

        UpdateSummary();
        if (failed == 0)
        {
            _notifications.Success(Loc.T("Safe items fixed"), Loc.F("{0} changes made. Each row has an Undo button.", done));
        }
        else
        {
            _notifications.Warning(Loc.T("Not everything was fixed"), Loc.F("{0} changes made, {1} not. The rows say why.", done, failed));
        }
    }

    /// <summary>One fix of "Fix all": no dialog of its own (the list was confirmed), still guarded and journaled.</summary>
    private async Task<bool> ApplyWithoutAskingAsync(GameReadyRow row, CancellationToken cancellationToken)
    {
        var fix = row.Fix!;
        try
        {
            if (fix.Kind == GameReadyFixKind.OptimizationAction && fix.Target is { } actionId)
            {
                bool ok = await _runner.RunAsync(actionId, revert: false, confirm: false, cancellationToken);
                if (ok)
                {
                    MarkFixed(row, null, () => _runner.RunAsync(actionId, revert: true));
                }
                else
                {
                    MarkFailed(row, Loc.T("Not changed; see the notification."));
                }

                return ok;
            }

            var change = await _service.ApplyAsync(fix, cancellationToken);
            if (change.Result.Success)
            {
                MarkFixed(row, change.Result.Message, UndoWithService(row, change.Undo));
                return true;
            }

            MarkFailed(row, change.Result.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game ready fix {Id} failed.", row.Id);
            MarkFailed(row, ex.Message);
            return false;
        }
    }

    /// <summary>A DirectX graphics setting or graphics preference fix, with the success notification offering Undo.</summary>
    private Task FixWithServiceAsync(GameReadyRow row, GameReadyFix fix) => RunBusyAsync(async ct =>
    {
        Func<CancellationToken, Task<OperationResult>>? undo = null;
        var result = await UndoOffers.RunAsync(
            async () =>
            {
                var change = await _service.ApplyAsync(fix, ct);
                undo = change.Undo;
                return change.Result;
            },
            undoCt => undo is null ? Task.FromResult(OperationResult.Fail(Loc.T("There is nothing to undo."))) : undo(undoCt),
            row.Title);
        if (result.Success)
        {
            MarkFixed(row, result.Message, UndoWithService(row, undo));
            _notifications.Success(Loc.T("Fixed"), result.Message);
        }
        else
        {
            MarkFailed(row, result.Message);
            _notifications.Error(Loc.T("Could not fix"), result.Message);
        }

        UpdateSummary();
    }, Loc.F("Fixing: {0}", row.Title));

    /// <summary>
    /// Switches a monitor to its fastest refresh rate and asks "Keep these settings?"; with no answer within
    /// <see cref="KeepSettingsSeconds"/> seconds it switches back, as Windows does.
    /// </summary>
    private async Task<bool> FixRefreshRateAsync(GameReadyRow row, GameReadyFix fix, bool confirm)
    {
        if (fix.Target is not { } device || fix.Mode is not { } mode)
        {
            return false;
        }

        if (confirm && !await _dialogs.ConfirmAsync(Loc.T("Change the refresh rate?"),
                Loc.F("The monitor switches to {0}. The screen may go dark for a moment. If the picture looks wrong, do nothing: it switches back after {1} seconds.",
                    mode, KeepSettingsSeconds), fix.Label))
        {
            return false;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct => result = await _displays.SetModeAsync(device, mode, ct), Loc.F("Switching to {0} Hz…", mode.RefreshHz));
        if (result is null)
        {
            return false;
        }

        if (!result.Success)
        {
            MarkFailed(row, result.Message);
            _notifications.Error(Loc.T("Refresh rate not changed"), result.Message);
            return false;
        }

        var previous = fix.PreviousMode;
        if (!await ShowKeepDialogAsync(Loc.T("Keep these settings?"), Loc.F("The monitor now runs at {0}.", mode)))
        {
            if (previous is not null)
            {
                await RunBusyAsync(async ct =>
                {
                    var back = await _displays.SetModeAsync(device, previous, ct);
                    row.ResultFailed = !back.Success;
                    row.ResultText = back.Success ? Loc.F("Switched back to {0} Hz.", previous.RefreshHz) : back.Message;
                }, Loc.T("Switching back…"));
            }

            UpdateSummary();
            return false;
        }

        MarkFixed(row, Loc.F("Now {0} Hz.", mode.RefreshHz), previous is null ? null : async () =>
        {
            if (!await _dialogs.ConfirmAsync(Loc.T("Switch the refresh rate back?"), Loc.F("The monitor goes back to {0}.", previous), Loc.T("Switch back")))
            {
                return false;
            }

            OperationResult? back = null;
            await RunBusyAsync(async ct => back = await _displays.SetModeAsync(device, previous, ct), Loc.T("Switching back…"));
            Report(back, Loc.T("Refresh rate switched back"));
            return back?.Success == true;
        });
        _notifications.Success(Loc.T("Refresh rate changed"), Loc.F("Now {0} Hz. Games and the desktop feel smoother.", mode.RefreshHz));
        UpdateSummary();
        return true;
    }

    private async Task TestMouseAsync()
    {
        int hz = 0;
        bool ran = await RunBusyAsync(async ct =>
        {
            var live = new Progress<int>(value => BusyText = Loc.F("Keep moving the mouse in circles… {0} Hz", value));
            hz = await _meter.MeasureAsync(PollingSeconds, live, ct);
        }, Loc.T("Move your mouse in circles for five seconds…"));
        if (!ran)
        {
            return;
        }

        if (hz <= 0)
        {
            _notifications.Warning(Loc.T("No mouse movement seen"), Loc.T("Move the mouse continuously while the test runs, then try again."));
            return;
        }

        _service.RememberPolling(hz);
        if (_service.LastFacts is { } facts)
        {
            Apply(GameReadyRules.Evaluate(facts, Loc.Current), keepFixes: true);
        }

        _notifications.Info(Loc.T("Mouse polling rate"), Loc.F("About {0} Hz ({1}).", hz, PollingRateMath.Interpret(hz).Label));
    }

    private Func<Task<bool>>? UndoWithService(GameReadyRow row, Func<CancellationToken, Task<OperationResult>>? undo) =>
        undo is null ? null : async () =>
        {
            if (!await _dialogs.ConfirmAsync(Loc.F("Undo \"{0}\"?", row.FixLabel), Loc.T("The setting goes back to what it was before the fix."), Loc.T("Undo")))
            {
                return false;
            }

            OperationResult? result = null;
            await RunBusyAsync(async ct => result = await undo(ct), Loc.T("Undoing…"));
            Report(result, Loc.T("Undone"));
            return result?.Success == true;
        };

    private void MarkFixed(GameReadyRow row, string? message, Func<Task<bool>>? undo)
    {
        row.IsFixed = true;
        row.Undo = undo;
        row.ResultFailed = false;
        row.ResultText = message ?? (row.RequiresRestart ? Loc.T("Done. Restart Windows to finish.") : Loc.T("Done."));
        UpdateSummary();
    }

    private static Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = DispatcherHelper.UiDispatcher;
        return dispatcher is null || dispatcher.CheckAccess() ? work() : dispatcher.InvokeAsync(work).Task.Unwrap();
    }

    private static void MarkFailed(GameReadyRow row, string message)
    {
        row.ResultFailed = true;
        row.ResultText = message;
    }

    private void Report(OperationResult? result, string successTitle)
    {
        if (result is null)
        {
            return;
        }

        if (result.Success)
        {
            _notifications.Success(successTitle, result.Message);
        }
        else
        {
            _notifications.Error(Loc.T("Could not undo"), result.Message);
        }
    }

    private static string ConfirmText(GameReadyRow row) => row.Fix?.Kind switch
    {
        GameReadyFixKind.GpuPreference => Loc.F("Windows' graphics setting for these games is set to High performance (the graphics card): {0}", row.Detail),
        _ => row.Why,
    };

    private async Task<bool> ShowKeepDialogAsync(string title, string detail)
    {
        try
        {
            var dialog = new KeepSettingsDialog(title, detail, KeepSettingsSeconds);
            var result = await _contentDialogs.ShowAsync(dialog, CancellationToken.None);
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // Without a dialog host fall back to a plain confirmation that still times out: if the monitor cannot show
            // the new mode the prompt is invisible, so no answer within the countdown switches back, as Windows does.
            _logger.LogDebug(ex, "Keep-settings dialog unavailable; falling back to a plain confirmation.");
            var answer = _dialogs.ConfirmAsync(title,
                detail + "\n\n" + Loc.F("Keep these settings? If you do nothing, it switches back in {0} seconds.", KeepSettingsSeconds), Loc.T("Keep"));
            var finished = await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(KeepSettingsSeconds)));
            return finished == answer && await answer;
        }
    }
}
