using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;

namespace PcChecker.App.ViewModels;

/// <summary>Actions on the selected process: each system change is confirmed first and reported afterwards.</summary>
public sealed partial class ProcessesViewModel
{
    private static string JournalNote => Loc.T("It is recorded in the Action Journal (Logs page). No restore point is needed because nothing in Windows' settings changes.");

    private bool CanEndSelected() => SelectedRow is { CanEnd: true, Process: not null };

    private bool CanEndTree() => SelectedRow is { CanEnd: true, IsGroup: false, Process: not null };

    private bool CanSetPriority(ProcessPriority priority) => SelectedRow is { CanEnd: true, IsGroup: false, Process: not null };

    private bool HasFile() => SelectedRow?.PathText is { Length: > 0 };

    private bool HasProcess() => SelectedRow?.Process is not null;

    [RelayCommand(CanExecute = nameof(CanEndSelected))]
    private async Task EndTaskAsync()
    {
        if (IsBusy || SelectedRow is not { Process: { } process } row || !row.CanEnd)
        {
            return;
        }

        if (row.Group is { } group)
        {
            await EndGroupAsync(row, group);
            return;
        }

        string message = Loc.F("{0} closes straight away. Anything unsaved in it is lost.", process.Name)
                         + "\n\n" + row.WhatIsThis
                         + WindowsComponentWarning(row)
                         + "\n\n" + Loc.T("This cannot be undone; start the program again to get it back.") + " " + JournalNote;
        if (!await _dialogs.ConfirmAsync(Loc.F("End {0}?", process.Name), message, Loc.T("End task"), isDanger: true))
        {
            return;
        }

        await RunActionAsync(ct => _processes.EndProcessAsync(process, includeChildren: false, ct),
            Loc.T("Program ended"), Loc.F("Could not end {0}", process.Name), Loc.F("Ending {0}\u2026", process.Name));
    }

    [RelayCommand(CanExecute = nameof(CanEndTree))]
    private async Task EndTreeAsync()
    {
        if (IsBusy || SelectedRow is not { Process: { } process } row || !row.CanEnd || row.IsGroup)
        {
            return;
        }

        var links = _last.Processes.Select(p => new ProcessLink(p.Pid, p.ParentPid, p.StartTime, p.Name));
        var children = ProcessTree.Descendants(process.Pid, process.StartTime, links);
        int protectedChildren = children.Count(c => CriticalProcessRules.IsCritical(c.Name, c.Pid, _processes.OwnProcessId));
        string childText = children.Count == 0
            ? Loc.T("It has not started any other programs right now, so only it is ended.")
            : Loc.F("It also ends the {0} program(s) it started: {1}.", children.Count - protectedChildren,
                string.Join(", ", children.Where(c => !CriticalProcessRules.IsCritical(c.Name, c.Pid, _processes.OwnProcessId)).Select(c => c.Name).Distinct().Take(8)));
        if (protectedChildren > 0)
        {
            childText += " " + Loc.T("Windows processes among them are left running.");
        }

        string message = Loc.F("{0} closes straight away. Anything unsaved in it is lost.", process.Name) + " " + childText
                         + WindowsComponentWarning(row)
                         + "\n\n" + Loc.T("This cannot be undone; start the program again to get it back.") + " " + JournalNote;
        if (!await _dialogs.ConfirmAsync(Loc.F("End {0} and the programs it started?", process.Name), message, Loc.T("End process tree"), isDanger: true))
        {
            return;
        }

        await RunActionAsync(ct => _processes.EndProcessAsync(process, includeChildren: true, ct),
            Loc.T("Process tree ended"), Loc.F("Could not end {0}", process.Name), Loc.F("Ending {0} and the programs it started\u2026", process.Name));
    }

    [RelayCommand(CanExecute = nameof(CanSetPriority))]
    private async Task SetPriorityAsync(ProcessPriority priority)
    {
        if (IsBusy || SelectedRow is not { Process: { } process } row || !row.CanEnd || row.IsGroup)
        {
            return;
        }

        if (!PriorityRules.IsAllowed(priority, IsAdvancedMode))
        {
            await _dialogs.ShowMessageAsync(Loc.T("Realtime is an advanced option"), Loc.T("Turn on Advanced mode to set Realtime priority."));
            return;
        }

        string label = PriorityLabel(priority);
        string message = Loc.F("Priority decides which program gets the processor first when the PC is busy. {0} is now at {1}.",
                             process.Name, PriorityLabel(process.Priority))
                         + (PriorityWarning(priority) is { } warning ? "\n\n" + warning : string.Empty)
                         + "\n\n" + Loc.T("Windows sets it back to normal when the program restarts.") + " " + JournalNote;
        bool risky = priority is ProcessPriority.Realtime;
        if (!await _dialogs.ConfirmAsync(Loc.F("Set {0} to {1} priority?", process.Name, label), message, Loc.T("Set priority"), isDanger: risky))
        {
            return;
        }

        await RunActionAsync(ct => _processes.SetPriorityAsync(process, priority, IsAdvancedMode, ct),
            Loc.T("Priority changed"), Loc.F("Could not change the priority of {0}", process.Name), Loc.T("Changing the priority\u2026"));
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void OpenFileLocation()
    {
        if (SelectedRow?.Process is { } process)
        {
            Report(_processes.OpenFileLocation(process), Loc.T("Could not open the folder"));
        }
    }

    [RelayCommand(CanExecute = nameof(HasProcess))]
    private void SearchOnline()
    {
        if (SelectedRow?.Process is { } process)
        {
            Report(_processes.SearchOnline(process), Loc.T("Could not open the browser"));
        }
    }

    [RelayCommand(CanExecute = nameof(HasProcess))]
    private void CopyDetails()
    {
        if (SelectedRow is not { Process: { } process } row)
        {
            return;
        }

        try
        {
            string text = row.Group is { } group
                ? string.Join("\n\n", group.Members.Select(m => ProcessText.Details(m, row.Entry, ProcessSuspicionRules.Evaluate(m, row.Entry, _windowsDirectory))))
                : ProcessText.Details(process, row.Entry, row.Flags);
            Clipboard.SetText(text);
            _notifications.Success(Loc.T("Copied"), Loc.T("The details are on the clipboard."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            _notifications.Warning(Loc.T("Could not copy"), ex.Message);
        }
    }

    private async Task EndGroupAsync(RunningProcessRow row, ProcessGroup group)
    {
        var members = group.Members.Where(m => !CriticalProcessRules.IsCritical(m.Name, m.Pid, _processes.OwnProcessId)).ToList();
        string message = Loc.F("All {0} processes of {1} close straight away. Anything unsaved in them is lost.", members.Count, group.Title)
                         + "\n\n" + row.WhatIsThis
                         + WindowsComponentWarning(row)
                         + "\n\n" + Loc.T("This cannot be undone; start the program again to get it back.") + " " + JournalNote;
        if (!await _dialogs.ConfirmAsync(Loc.F("End {0}?", group.Title), message, Loc.T("End task"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            int ended = 0;
            var failures = new List<string>();
            foreach (var member in members.OrderByDescending(m => m.StartTime))
            {
                ct.ThrowIfCancellationRequested();
                var result = await _processes.EndProcessAsync(member, includeChildren: false, ct);
                if (result.Success)
                {
                    ended++;
                }
                else
                {
                    failures.Add(result.Message);
                }
            }

            if (failures.Count == 0)
            {
                _notifications.Success(Loc.T("Program ended"), Loc.F("{0}: {1} process(es) ended.", group.Title, ended));
            }
            else
            {
                _notifications.Warning(Loc.F("Could not end {0}", group.Title), string.Join(" ", failures.Distinct()));
            }
        }, Loc.F("Ending {0}\u2026", group.Title));
        await SampleNowAsync();
    }

    /// <summary>
    /// Runs one action behind the busy overlay (progress text, Cancel, no second start meanwhile) and reports the
    /// result. Cancel stops before the next process of a tree; a process already ended stays ended.
    /// </summary>
    private async Task RunActionAsync(Func<CancellationToken, Task<OperationResult>> action, string successTitle, string failureTitle, string busyText)
    {
        await RunBusyAsync(async ct =>
        {
            try
            {
                var result = await action(ct);
                if (result.Success)
                {
                    _notifications.Success(successTitle, result.Message);
                }
                else
                {
                    _notifications.Error(failureTitle, result.Message);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Process action failed.");
                _notifications.Error(failureTitle, ex.Message);
            }
        }, busyText);

        await SampleNowAsync();
    }

    /// <summary>Translatable priority name (literal strings so the extractor collects them).</summary>
    internal static string PriorityLabel(ProcessPriority? priority) => priority switch
    {
        ProcessPriority.Idle => Loc.T("Low"),
        ProcessPriority.BelowNormal => Loc.T("Below normal"),
        ProcessPriority.Normal => Loc.T("Normal"),
        ProcessPriority.AboveNormal => Loc.T("Above normal"),
        ProcessPriority.High => Loc.T("High"),
        ProcessPriority.Realtime => Loc.T("Realtime"),
        _ => Loc.T("Unknown"),
    };

    /// <summary>Translatable warning for the confirmation dialog; same text as <see cref="PriorityRules.Warning"/>.</summary>
    private static string? PriorityWarning(ProcessPriority priority) => priority switch
    {
        ProcessPriority.Realtime => Loc.T("Realtime puts this program ahead of Windows itself, including the mouse, keyboard and disk. A busy program at Realtime can freeze the whole PC until it is restarted. Only use it for a short test."),
        ProcessPriority.High => Loc.T("High priority can make other programs, sound and the mouse stutter while this program is busy."),
        ProcessPriority.Idle => Loc.T("Low priority means the program only gets the processor when nothing else wants it, so it may run much slower."),
        _ => null,
    };

    private static string WindowsComponentWarning(RunningProcessRow row) =>
        row.Entry?.Category is ProcessCatalog.Windows or ProcessCatalog.Security
            ? "\n\n" + Loc.T("This is part of Windows. Windows may start it again, or parts of the desktop may misbehave until you sign out and in again.")
            : string.Empty;

    private void Report(OperationResult result, string failureTitle)
    {
        if (!result.Success)
        {
            _notifications.Warning(failureTitle, result.Message);
        }
        else if (result.Message.Contains("(demo)", StringComparison.Ordinal))
        {
            _notifications.Info(Loc.T("Demo mode"), result.Message);
        }
    }
}
