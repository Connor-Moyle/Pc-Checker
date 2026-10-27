using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.App.Views.Dialogs;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Task Manager actions: switch to, minimize and maximize, suspend and resume, efficiency mode, affinity, restart,
/// file properties, memory dumps and Run new task. Changes are confirmed first; refusals are explained before asking.
/// </summary>
public sealed partial class ProcessesViewModel
{
    private bool HasWindowSelected() => SelectedRow is { IsSection: false, HasWindow: true };

    private bool CanSuspend() => SelectedRow is { IsSection: false, Process: not null, IsSuspended: false } && !IsSelectedProtected();

    // Any suspended member is enough: after a partial failure a group shows as running but still holds paused processes.
    private bool CanResume() => SelectedRow is { IsSection: false, Process: not null } row && row.Members.Any(m => m.State == ProcessRunState.Suspended);

    private bool CanChangeProcess() => SelectedRow is { CanEnd: true, Process: not null };

    private bool CanChangeSingle() => SelectedRow is { CanEnd: true, IsGroup: false, Process: not null };

    private bool IsSelectedProtected() => SelectedRow?.IsProtected ?? true;

    [RelayCommand(CanExecute = nameof(HasWindowSelected))]
    private void SwitchTo() => ShowSelectedWindow(WindowCommand.SwitchTo);

    [RelayCommand(CanExecute = nameof(HasWindowSelected))]
    private void Minimize() => ShowSelectedWindow(WindowCommand.Minimize);

    [RelayCommand(CanExecute = nameof(HasWindowSelected))]
    private void Maximize() => ShowSelectedWindow(WindowCommand.Maximize);

    [RelayCommand(CanExecute = nameof(CanSuspend))]
    private async Task SuspendAsync()
    {
        if (IsBusy || SelectedRow is not { Process: { } lead } row)
        {
            return;
        }

        string title = Title(row);
        var targets = row.Members.Where(m => m.State != ProcessRunState.Suspended).ToList();
        if (targets.Select(m => ProcessSafetyRules.SuspendRefusal(m, _windowsDirectory, _processes.OwnProcessId)).FirstOrDefault(r => r is not null) is { } refusal)
        {
            await _dialogs.ShowMessageAsync(Loc.F("{0} cannot be suspended", title), refusal);
            return;
        }

        string message = Loc.F("{0} stops using the processor until you resume it. Its windows do not react meanwhile, and network connections or sound in it may drop.", title)
                         + "\n\n" + Loc.T("It stays paused if you close PC Checker; select it here and choose Resume to let it run again.") + " " + JournalNote;
        if (!await _dialogs.ConfirmAsync(Loc.F("Suspend {0}?", title), message, Loc.T("Suspend")))
        {
            return;
        }

        await RunForMembersAsync(targets, (p, ct) => _processes.SuspendAsync(p, ct), Loc.T("Suspended"), Loc.F("Could not suspend {0}", title), Loc.F("Suspending {0}…", title));
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync()
    {
        if (IsBusy || SelectedRow is not { Process: not null } row)
        {
            return;
        }

        string title = Title(row);
        // Store and Windows apps that Windows paused itself are left alone (see ProcessSafetyRules.ResumeRefusal).
        var checks = row.Members.Where(m => m.State == ProcessRunState.Suspended)
            .Select(m => (Process: m, Refusal: ProcessSafetyRules.ResumeRefusal(m, _windowsDirectory, _processes.OwnProcessId, _processes.WasSuspendedByPcChecker(m))))
            .ToList();
        var targets = checks.Where(c => c.Refusal is null).Select(c => c.Process).ToList();
        if (targets.Count == 0)
        {
            string reason = checks.Select(c => c.Refusal).FirstOrDefault(r => r is not null) ?? Loc.T("It is not suspended.");
            await _dialogs.ShowMessageAsync(Loc.F("{0} cannot be resumed", title), reason);
            return;
        }

        await RunForMembersAsync(targets, (p, ct) => _processes.ResumeAsync(p, ct), Loc.T("Running again"), Loc.F("Could not resume {0}", title), Loc.F("Resuming {0}…", title));
    }

    [RelayCommand(CanExecute = nameof(CanChangeProcess))]
    private async Task ToggleEfficiencyModeAsync()
    {
        if (IsBusy || SelectedRow is not { Process: not null } row)
        {
            return;
        }

        string title = Title(row);
        bool turnOn = !row.Members.All(m => m.EfficiencyMode);
        if (row.Members.Select(m => ProcessSafetyRules.EfficiencyRefusal(m, _windowsDirectory, _processes.OwnProcessId)).FirstOrDefault(r => r is not null) is { } refusal)
        {
            await _dialogs.ShowMessageAsync(Loc.T("Efficiency mode is not available"), refusal);
            return;
        }

        string message = turnOn
            ? Loc.F("Windows gives {0} less power and Low priority. That saves energy and keeps other programs smooth, but it may run slower.", title)
            : Loc.F("{0} gets its normal power and Normal priority back.", title);
        if (!await _dialogs.ConfirmAsync(turnOn ? Loc.F("Turn on efficiency mode for {0}?", title) : Loc.F("Turn off efficiency mode for {0}?", title),
                message + "\n\n" + Loc.T("It resets when the program restarts.") + " " + JournalNote,
                turnOn ? Loc.T("Turn on") : Loc.T("Turn off")))
        {
            return;
        }

        var targets = row.Members.Where(m => m.EfficiencyMode != turnOn).ToList();
        await RunForMembersAsync(targets, (p, ct) => _processes.SetEfficiencyModeAsync(p, turnOn, ct),
            turnOn ? Loc.T("Efficiency mode on") : Loc.T("Efficiency mode off"), Loc.F("Could not change efficiency mode for {0}", title), Loc.T("Changing efficiency mode…"));
    }

    [RelayCommand(CanExecute = nameof(CanChangeSingle))]
    private async Task SetAffinityAsync()
    {
        if (IsBusy || SelectedRow is not { Process: { } process })
        {
            return;
        }

        int count = _processes.LogicalProcessorCount;
        if (AffinityMath.Validate(AffinityMath.FullMask(count), count) is { } unsupported)
        {
            await _dialogs.ShowMessageAsync(Loc.T("Affinity is not available"), unsupported);
            return;
        }

        ulong current = _processes.GetAffinity(process) ?? AffinityMath.FullMask(count);
        var choices = new AffinityChoices(process.Name, current, count);
        if (!await _dialogs.ShowContentAsync(Loc.F("Set affinity of {0}", process.Name), new AffinityContent { DataContext = choices }, Loc.T("Set affinity")))
        {
            return;
        }

        ulong mask = choices.Mask;
        if (AffinityMath.Validate(mask, count) is { } problem)
        {
            await _dialogs.ShowMessageAsync(Loc.T("Affinity not changed"), problem);
            return;
        }

        if (mask == current)
        {
            return;
        }

        await RunActionAsync(ct => _processes.SetAffinityAsync(process, mask, ct),
            Loc.T("Affinity changed"), Loc.F("Could not change the affinity of {0}", process.Name), Loc.T("Changing the affinity…"));
    }

    [RelayCommand(CanExecute = nameof(CanChangeProcess))]
    private async Task RestartAsync()
    {
        if (IsBusy || SelectedRow is not { Process: { } lead } row)
        {
            return;
        }

        string title = Title(row);
        // The account check happens in the service, which knows whose programs it can start again.
        if (ProcessSafetyRules.RestartRefusal(lead, _windowsDirectory, _processes.OwnProcessId, currentUser: null) is { } refusal)
        {
            await _dialogs.ShowMessageAsync(Loc.F("{0} cannot be restarted here", title), refusal);
            return;
        }

        string message = ProcessSafetyRules.IsExplorer(lead)
            ? Loc.T("The taskbar, desktop and File Explorer windows disappear for a moment and come back. Open folders are closed.")
            : Loc.F("{0} is asked to close (or ended if it does not), then started again from the same file with the same options. Anything unsaved in it may be lost.", title);
        if (!await _dialogs.ConfirmAsync(Loc.F("Restart {0}?", title), message + "\n\n" + JournalNote, Loc.T("Restart"), isDanger: true))
        {
            return;
        }

        await RunActionAsync(ct => _processes.RestartAsync(lead, ct), Loc.T("Restarted"), Loc.F("Could not restart {0}", title), Loc.F("Restarting {0}…", title));
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void ShowProperties()
    {
        if (SelectedRow?.Process is { } process)
        {
            Report(_processes.ShowProperties(process), Loc.T("Could not show the properties"));
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSingle))]
    private async Task CreateDumpAsync()
    {
        if (IsBusy || SelectedRow is not { Process: { } process })
        {
            return;
        }

        if (ProcessSafetyRules.DumpRefusal(process, _processes.OwnProcessId) is { } refusal)
        {
            await _dialogs.ShowMessageAsync(Loc.T("No memory dump for this process"), refusal);
            return;
        }

        string message = Loc.F("PC Checker saves a copy of the memory of {0} as a .DMP file in your Temp folder, for a developer or support team. The program keeps running.", process.Name)
                         + "\n\n" + Loc.F("The file can be as large as the program's memory (about {0}) and can contain anything that was open in it, such as documents or passwords. Only share it with people you trust.", ByteSize.Format(process.PrivateBytes));
        if (!await _dialogs.ConfirmAsync(Loc.F("Create a memory dump of {0}?", process.Name), message, Loc.T("Create dump file")))
        {
            return;
        }

        OperationResult<string>? result = null;
        await RunBusyAsync(async ct => result = await _processes.CreateDumpAsync(process, ct),
            Loc.F("Saving the memory of {0}… This can take a minute.", process.Name), cancellable: false);
        if (result is null)
        {
            return;
        }

        if (!result.Success || result.Value is null)
        {
            _notifications.Error(Loc.F("Could not create the dump file of {0}", process.Name), result.Message);
            return;
        }

        if (result.Message.Contains("(demo)", StringComparison.Ordinal))
        {
            _notifications.Info(Loc.T("Demo mode"), result.Message);
            return;
        }

        _notifications.Success(Loc.T("Dump file saved"), result.Message);
        Report(_processes.ShowInFolder(result.Value), Loc.T("Could not open the folder"));
    }

    [RelayCommand]
    private async Task RunNewTaskAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var choices = new RunTaskChoices(Settings.Current.Processes.RecentRunCommands,
            () => _dialogs.OpenFileAsync(Loc.T("Choose a program"), Loc.T("Programs") + "|*.exe;*.bat;*.cmd;*.msc;*.lnk|" + Loc.T("All files") + "|*.*"));
        if (!await _dialogs.ShowContentAsync(Loc.T("Run new task"), new RunTaskContent { DataContext = choices }, Loc.T("Run")))
        {
            return;
        }

        string command = choices.Command.Trim();
        if (command.Length == 0)
        {
            return;
        }

        Settings.Current.Processes.RecentRunCommands = CommandLineText.AddRecent(Settings.Current.Processes.RecentRunCommands, command);
        SaveSettings();
        await RunActionAsync(ct => _processes.RunNewTaskAsync(command, choices.AsAdministrator, ct),
            Loc.T("Opening"), Loc.F("Could not start {0}", command), Loc.F("Starting {0}…", command));
    }

    private void ShowSelectedWindow(WindowCommand command)
    {
        if (SelectedRow is not { Process: { } process } row)
        {
            return;
        }

        var target = row.Members.FirstOrDefault(m => m.HasWindow) ?? process;
        Report(_processes.ShowWindow(target, command), Loc.T("Could not show the window"));
    }

    /// <summary>Runs one change for each process of a line behind the busy overlay and reports the outcome once.</summary>
    private Task RunForMembersAsync(IReadOnlyList<ProcessInfo> targets, Func<ProcessInfo, CancellationToken, Task<OperationResult>> change,
        string successTitle, string failureTitle, string busyText)
    {
        if (targets.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (targets.Count == 1)
        {
            return RunActionAsync(ct => change(targets[0], ct), successTitle, failureTitle, busyText);
        }

        return RunActionAsync(async ct =>
        {
            var results = new List<OperationResult>();
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await change(target, ct));
            }

            var failures = results.Where(r => !r.Success).Select(r => r.Message).Distinct().ToList();
            return failures.Count == 0
                ? OperationResult.Ok(results[0].Message.Contains("(demo)", StringComparison.Ordinal)
                    ? Loc.F("{0} processes changed (demo).", results.Count)
                    : Loc.F("{0} processes changed.", results.Count))
                : OperationResult.Fail(string.Join(" ", failures));
        }, successTitle, failureTitle, busyText);
    }

    private void RefreshTaskActionState()
    {
        SwitchToCommand.NotifyCanExecuteChanged();
        MinimizeCommand.NotifyCanExecuteChanged();
        MaximizeCommand.NotifyCanExecuteChanged();
        SuspendCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        ToggleEfficiencyModeCommand.NotifyCanExecuteChanged();
        SetAffinityCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        ShowPropertiesCommand.NotifyCanExecuteChanged();
        CreateDumpCommand.NotifyCanExecuteChanged();
    }

    private static string Title(RunningProcessRow row) => row.Group?.Title ?? (row.Process is { } p ? ProcessSorting.TitleOf(p) : row.Name);
}
