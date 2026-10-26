using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Storage;

namespace PcChecker.App.ViewModels;

/// <summary>Check disk, Repair-Volume and Optimize part of the Storage page. Every change confirms first.</summary>
public sealed partial class StorageViewModel
{
    private const int MaxOutputLines = 300;
    private CancellationTokenSource? _toolCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyToolRunning))]
    [NotifyCanExecuteChangedFor(nameof(CheckDiskScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(SpotFixCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScheduleFullRepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepairVolumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OptimizeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelToolCommand))]
    private bool _isToolRunning;

    /// <summary>False while a file-system repair runs: stopping chkdsk half-way leaves the volume dirty, so Cancel is locked.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelToolCommand))]
    private bool _toolCanCancel = true;

    [ObservableProperty]
    private string? _toolTitle;

    [ObservableProperty]
    private string? _toolStage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolResult))]
    private string? _toolResultMessage;

    [ObservableProperty]
    private bool _toolResultIsError;

    [ObservableProperty]
    private bool _toolRebootRequired;

    [ObservableProperty]
    private RepairMode _selectedRepairMode = RepairMode.Scan;

    public ObservableCollection<string> ToolOutput { get; } = [];

    public IReadOnlyList<RepairMode> RepairModes { get; } = Enum.GetValues<RepairMode>();

    public bool HasToolResult => !string.IsNullOrWhiteSpace(ToolResultMessage);

    public bool HasToolOutput => ToolOutput.Count > 0;

    public string RepairModeHelp => SelectedRepairMode switch
    {
        RepairMode.SpotFix => "Spot fix: takes the volume offline for a few seconds and fixes only the problems a scan already logged. Safe and quick.",
        RepairMode.OfflineScanAndFix => "Offline scan and fix: the full check. On the Windows drive it runs at the next restart and can take a long time.",
        _ => "Scan: looks for file system problems without changing anything. Run this first.",
    };

    public string OptimizeHelp => SelectedVolume?.MediaType switch
    {
        "HDD" => "Hard disk: defragments files so the heads move less. Takes a while on a full drive.",
        "SSD" or "SCM" => "SSD: sends TRIM so the drive knows which blocks are free. Quick, and it keeps the SSD fast.",
        _ => "Drive type unknown: Windows picks TRIM for an SSD or defragmentation for a hard disk.",
    };

    partial void OnSelectedRepairModeChanged(RepairMode value) => OnPropertyChanged(nameof(RepairModeHelp));

    partial void OnSelectedVolumeChanged(VolumeOption? value) => OnPropertyChanged(nameof(OptimizeHelp));

    private bool CanRunTool() => SelectedVolume is not null && !IsAnyToolRunning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private Task CheckDiskScan()
    {
        var volume = SelectedVolume;
        return volume is null ? Task.CompletedTask
            : RunToolAsync($"Check disk scan of {volume.Letter}", ct => _storage.CheckDiskAsync(volume.Letter, scanOnly: true, scheduleOfflineFix: false, ToolProgress(), ct));
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task SpotFix()
    {
        var volume = SelectedVolume;
        if (volume is null)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync($"Spot-fix {volume.Letter}?",
            $"chkdsk {volume.Letter} /spotfix takes the volume offline for a few seconds and repairs only the problems an earlier scan logged. " +
            "Save your work first. On the Windows drive it may instead be scheduled for the next restart. " +
            "Once started it cannot be cancelled: stopping a repair half-way leaves the file system marked dirty.",
            "Spot fix");
        if (ok)
        {
            await RunToolAsync($"Spot fix of {volume.Letter}", ct => _storage.CheckDiskAsync(volume.Letter, scanOnly: false, scheduleOfflineFix: false, ToolProgress(), ct), canCancel: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task ScheduleFullRepair()
    {
        var volume = SelectedVolume;
        if (volume is null)
        {
            return;
        }

        // chkdsk cannot take the Windows volume offline, so there it is always scheduled. A data volume is scheduled
        // only when something is using it (chkdsk is told not to force a dismount); otherwise it is repaired right now.
        string when = volume.IsSystemVolume
            ? $"chkdsk {volume.Letter} /f /r checks every sector and repairs the file system. {volume.Letter} is the Windows drive and cannot be taken offline, so Windows will run the repair the next time the PC restarts, before the desktop appears.\n\n" +
              "On a large hard disk this can take hours and the PC is unusable meanwhile."
            : $"chkdsk {volume.Letter} /f /r checks every sector and repairs the file system. If any program has files open on {volume.Letter}, the repair is scheduled for the next restart (open files are never forced closed). If nothing is using {volume.Letter}, it is taken offline and repaired right now, with the output shown here.\n\n" +
              "On a large hard disk this can take hours; the drive is unavailable meanwhile.";
        bool ok = await _dialogs.ConfirmAsync($"Full repair of {volume.Letter}?",
            when + " Only do this when a scan reported problems. If the drive is physically failing, back it up first; a repair can make recovery harder. " +
            "A repair that runs now cannot be cancelled from PC Checker: stopping chkdsk half-way leaves the file system marked dirty.",
            volume.IsSystemVolume ? "Schedule at next restart" : "Repair (now or at next restart)", isDanger: true);
        if (ok)
        {
            await RunToolAsync($"Full repair of {volume.Letter}", ct => _storage.CheckDiskAsync(volume.Letter, scanOnly: false, scheduleOfflineFix: true, ToolProgress(), ct), canCancel: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task RepairVolume()
    {
        var volume = SelectedVolume;
        if (volume is null)
        {
            return;
        }

        var mode = SelectedRepairMode;
        if (mode != RepairMode.Scan)
        {
            string what = mode == RepairMode.SpotFix
                ? "takes the volume offline for a few seconds and fixes the problems already found. Save your work first."
                : volume.IsSystemVolume
                    ? $"runs the full offline check. {volume.Letter} is the Windows drive, so it happens at the next restart and can take a long time."
                    : $"takes {volume.Letter} offline and runs the full check right now; programs using it lose access until it finishes, which can take a long time. Save your work first.";
            bool ok = await _dialogs.ConfirmAsync($"Repair-Volume {mode} on {volume.Letter}?", $"Repair-Volume -{mode} {what}", "Run repair", isDanger: mode == RepairMode.OfflineScanAndFix);
            if (!ok)
            {
                return;
            }
        }

        await RunToolAsync($"Repair-Volume {mode} on {volume.Letter}", ct => _storage.RepairVolumeAsync(volume.Letter, mode, ToolProgress(), ct));
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task Optimize()
    {
        var volume = SelectedVolume;
        if (volume is null)
        {
            return;
        }

        string explanation = volume.MediaType switch
        {
            "HDD" => $"Windows will defragment {volume.Letter}. Files are not changed, only rearranged. It can take a while and slows the PC meanwhile.",
            "SSD" or "SCM" => $"Windows will send TRIM to the SSD behind {volume.Letter}. Files are not changed. This usually takes under a minute.",
            _ => $"Windows will optimize {volume.Letter}, choosing TRIM for an SSD or defragmentation for a hard disk. Files are not changed.",
        };
        bool ok = await _dialogs.ConfirmAsync($"Optimize {volume.Letter}?", explanation, "Optimize");
        if (ok)
        {
            await RunToolAsync($"Optimize {volume.Letter}", ct => _storage.OptimizeAsync(volume.Letter, ToolProgress(), ct));
        }
    }

    private bool CanCancelTool() => IsToolRunning && ToolCanCancel;

    [RelayCommand(CanExecute = nameof(CanCancelTool))]
    private void CancelTool()
    {
        try
        {
            _toolCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private IProgress<ProgressUpdate> ToolProgress() => new Progress<ProgressUpdate>(p =>
    {
        if (!string.IsNullOrWhiteSpace(p.Stage))
        {
            ToolStage = p.Percent is { } pct && pct >= 100 ? "Finished" : p.Stage;
        }

        if (!string.IsNullOrWhiteSpace(p.Detail))
        {
            ToolOutput.Add(p.Detail);
            while (ToolOutput.Count > MaxOutputLines)
            {
                ToolOutput.RemoveAt(0);
            }

            OnPropertyChanged(nameof(HasToolOutput));
        }
    });

    /// <param name="canCancel">False for file-system repairs, which must run to completion once started.</param>
    private async Task RunToolAsync(string title, Func<CancellationToken, Task<OperationResult>> work, bool canCancel = true)
    {
        if (IsToolRunning)
        {
            return;
        }

        _toolCts?.Dispose();
        _toolCts = new CancellationTokenSource();
        var token = _toolCts.Token;

        ToolOutput.Clear();
        OnPropertyChanged(nameof(HasToolOutput));
        ToolTitle = title;
        ToolCanCancel = canCancel;
        ToolStage = "Starting…";
        ToolResultMessage = null;
        ToolResultIsError = false;
        ToolRebootRequired = false;
        ErrorMessage = null;
        IsToolRunning = true;
        RaiseToolStateChanged();

        try
        {
            _logger.LogInformation("Storage tool: {Title}", title);
            var result = await work(token);
            ToolResultMessage = result.Message;
            ToolResultIsError = !result.Success;
            ToolRebootRequired = result.RebootRequired;
            ToolStage = result.Success ? "Finished" : "Failed";
            if (result.Success)
            {
                _notifications.Show(title, result.Message, result.RebootRequired ? NotificationKind.Warning : NotificationKind.Success);
            }
            else
            {
                _notifications.Error(title, result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            ToolStage = "Cancelled";
            ToolResultMessage = $"{title} was cancelled.";
            ToolResultIsError = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Storage tool failed: {Title}", title);
            ToolStage = "Failed";
            ToolResultMessage = ex.Message;
            ToolResultIsError = true;
        }
        finally
        {
            IsToolRunning = false;
            ToolCanCancel = true;
            RaiseToolStateChanged();
            _toolCts.Dispose();
            _toolCts = null;
        }
    }
}
