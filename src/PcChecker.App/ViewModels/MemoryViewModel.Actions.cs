using CommunityToolkit.Mvvm.Input;
using PcChecker.Core.Common;
using PcChecker.Core.Memory;

namespace PcChecker.App.ViewModels;

/// <summary>Memory page actions: diagnostic scheduling, standby purge, profiles, compression, SysMain and the page file editor.</summary>
public sealed partial class MemoryViewModel
{
    [RelayCommand]
    private async Task ScheduleDiagnosticAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Schedule Windows Memory Diagnostic",
            "Windows will run its built-in memory test the next time the PC restarts. The test takes a few minutes before Windows starts, and the result appears on this page afterwards.\n\nNothing changes until you restart.",
            "Schedule");
        if (!ok)
        {
            return;
        }

        bool scheduled = false;
        await RunBusyAsync(async ct =>
        {
            var result = await _memory.ScheduleWindowsMemoryDiagnosticAsync(ct);
            Report(result, "Memory diagnostic scheduled");
            scheduled = result.Success;
            if (scheduled && !DiagnosticText.Contains("scheduled", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticText = "Windows Memory Diagnostic is scheduled for the next restart. " + DiagnosticText;
            }
        }, "Scheduling the Windows Memory Diagnostic…");

        if (!scheduled)
        {
            return;
        }

        bool restart = await _dialogs.ConfirmAsync("Restart now?", "Restart the PC now to run the memory test? Save your work first. The restart starts in 10 seconds.", "Restart", isDanger: true);
        if (restart)
        {
            var launch = ShellLauncher.Open("shutdown.exe", "/r /t 10 /c \"PC Checker is restarting to run the Windows Memory Diagnostic.\"");
            if (!launch.Success)
            {
                ErrorMessage = launch.Message;
            }
        }
    }

    [RelayCommand]
    private void OpenMemoryDiagnosticTool()
    {
        var result = ShellLauncher.Open("mdsched.exe");
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    [RelayCommand]
    private async Task ClearStandbyAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Clear the standby list",
            $"Windows keeps {StandbyText} of recently used file data in RAM in case it is needed again. Clearing it frees that memory instantly; Windows refills it over time. This is harmless.",
            "Clear");
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _memory.ClearStandbyListAsync(ct);
            Report(result, "Standby list cleared");
            if (result.Success)
            {
                Apply(await _memory.GetOverviewAsync(ct));
            }
        }, "Clearing the standby list…");
    }

    [RelayCommand]
    private async Task ApplyProfileAsync(MemoryProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        string restartNote = item.Profile.AutomaticPageFile ? string.Empty : "\n\nThe page file change needs a restart to take effect.";
        bool ok = await _dialogs.ConfirmAsync($"Apply \"{item.Name}\"",
            $"{item.Description}\n\nThis will set: {item.Settings}.{restartNote}\n\nA restore point is created first if that option is on in Settings.",
            "Apply", isDanger: item.IsAdvancedOnly);
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _memory.ApplyProfileAsync(item.Profile, ct);
            Report(result, "Memory profile applied");
            Apply(await _memory.GetOverviewAsync(ct));
        }, $"Applying \"{item.Name}\"…");
    }

    [RelayCommand]
    private async Task ToggleCompressionAsync()
    {
        bool enable = !MemoryCompressionEnabled;
        bool ok = await _dialogs.ConfirmAsync(enable ? "Turn memory compression on" : "Turn memory compression off",
            enable
                ? "Windows will compress memory pages it is not using so more programs fit in RAM. This is the Windows default and recommended for nearly every PC."
                : "Windows will stop compressing idle memory. Only useful on machines with lots of RAM; most PCs are slower without it.",
            enable ? "Turn on" : "Turn off", isDanger: !enable);
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            Report(await _memory.SetMemoryCompressionAsync(enable, ct), "Memory compression changed");
            Apply(await _memory.GetOverviewAsync(ct));
        }, "Changing memory compression…");
    }

    [RelayCommand]
    private async Task ToggleSysMainAsync()
    {
        if (_memory is not IMemoryTuning tuning)
        {
            ErrorMessage = "SysMain cannot be changed with this memory service.";
            return;
        }

        bool enable = !SysMainEnabled;
        bool ok = await _dialogs.ConfirmAsync(enable ? "Turn SysMain (Superfetch) on" : "Turn SysMain (Superfetch) off",
            enable
                ? "SysMain preloads the programs you use most so they open faster. It is on by default in Windows."
                : "SysMain will stop preloading programs. This occasionally helps old hard-drive PCs with constant disk activity, but usually makes everyday use slower.",
            enable ? "Turn on" : "Turn off", isDanger: !enable);
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            Report(await tuning.SetSysMainAsync(enable, ct), "SysMain changed");
            Apply(await _memory.GetOverviewAsync(ct));
        }, "Changing SysMain…");
    }

    [RelayCommand]
    private void UseRecommendedPageFile()
    {
        var (initial, max) = PageFileRules.Recommend(Overview?.TotalBytes ?? 16L * 1024 * 1024 * 1024);
        PageFileSystemManaged = false;
        PageFileInitialMb = initial;
        PageFileMaxMb = max;
    }

    [RelayCommand]
    private async Task ApplyPageFileAsync()
    {
        string? problem = PageFileRules.Validate(PageFileDrive, PageFileSystemManaged, PageFileInitialMb, PageFileMaxMb, Overview?.TotalBytes ?? 0);
        if (problem is not null)
        {
            ErrorMessage = problem;
            return;
        }

        string what = PageFileSystemManaged ? "let Windows manage the page file" : $"set the page file on {PageFileDrive} to {PageFileInitialMb:N0}–{PageFileMaxMb:N0} MB";
        bool ok = await _dialogs.ConfirmAsync("Change the page file",
            $"PC Checker will {what}. The change takes effect after a restart. A restore point is created first if that option is on in Settings.",
            "Apply");
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            Report(await _memory.SetPageFileAsync(PageFileDrive, PageFileSystemManaged, PageFileInitialMb, PageFileMaxMb, ct), "Page file updated");
            Apply(await _memory.GetOverviewAsync(ct));
        }, "Updating the page file…");
    }
}
