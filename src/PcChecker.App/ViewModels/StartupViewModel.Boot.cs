using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Startup;

namespace PcChecker.App.ViewModels;

/// <summary>Boot options half of <see cref="StartupViewModel"/>: fast startup, boot menu timeout, Safe Mode, restarts.</summary>
public sealed partial class StartupViewModel
{
    [ObservableProperty]
    private BootConfig? _boot;

    [ObservableProperty]
    private string _lastBootText = "—";

    [ObservableProperty]
    private string _bootDurationText = "—";

    [ObservableProperty]
    private string? _bootDurationNote;

    [ObservableProperty]
    private string _bootDescription = "Windows";

    [ObservableProperty]
    private bool _fastStartup;

    [ObservableProperty]
    private bool _hibernationEnabled;

    [ObservableProperty]
    private bool _safeModeNextBoot;

    [ObservableProperty]
    private bool _memoryDiagnosticScheduled;

    [ObservableProperty]
    private bool _legacyBootMenu;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BootTimeoutChanged))]
    private double _bootTimeoutSeconds = 30;

    public bool BootTimeoutChanged => Boot is not null && (int)Math.Round(BootTimeoutSeconds) != Boot.TimeoutSeconds;

    public string FastStartupSummary => FastStartup
        ? "On — shutdown saves the Windows kernel so the next boot is quicker."
        : "Off — every boot is a complete fresh start.";

    private void ApplyBoot(BootConfig boot)
    {
        Boot = boot;
        FastStartup = boot.FastStartup;
        HibernationEnabled = boot.HibernationEnabled;
        SafeModeNextBoot = boot.SafeModeNextBoot;
        MemoryDiagnosticScheduled = boot.MemoryDiagnosticScheduled;
        LegacyBootMenu = boot.LegacyBootMenu;
        BootDescription = string.IsNullOrWhiteSpace(boot.DefaultDescription) ? "Windows" : boot.DefaultDescription;
        BootTimeoutSeconds = boot.TimeoutSeconds;
        LastBootText = boot.LastBoot is { } last ? FormatLastBoot(last) : "Unknown";
        BootDurationText = boot.LastBootSeconds is { } s ? FormatDuration(s) : "Not measured";
        BootDurationNote = boot.LastBootSeconds switch
        {
            null => "Windows records this in the Diagnostics-Performance event log after each boot; nothing was found yet.",
            >= BootConfigRules.VerySlowBootSeconds => "Very slow. Fewer startup apps and a check of the Storage page should help.",
            >= BootConfigRules.SlowBootSeconds => "Slower than it should be on an SSD.",
            >= 30 => "Reasonable. Trimming startup apps can shave a few seconds.",
            _ => "Quick.",
        };
        OnPropertyChanged(nameof(FastStartupSummary));
        OnPropertyChanged(nameof(BootTimeoutChanged));
    }

    private static string FormatLastBoot(DateTimeOffset last)
    {
        var ago = DateTimeOffset.Now - last;
        string agoText = ago.TotalMinutes < 1 ? "just now"
            : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalDays < 1 ? $"{ago.Hours} h {ago.Minutes} min ago"
            : $"{(int)ago.TotalDays} d {ago.Hours} h ago";
        return $"{last.LocalDateTime:g} ({agoText})";
    }

    private static string FormatDuration(double seconds) =>
        seconds >= 60 ? $"{(int)(seconds / 60)} min {seconds % 60:0} s" : $"{seconds:0.#} s";

    [RelayCommand]
    private async Task ToggleFastStartupAsync()
    {
        bool target = !(Boot?.FastStartup ?? FastStartup);
        string message = target
            ? "Fast startup makes cold boots quicker by saving the Windows kernel to disk at shutdown (it uses hibernation, which will be turned on if needed).\n\n"
              + "Pros: faster boot after a shutdown.\nCons: a shutdown is no longer a full fresh start, some driver and update problems only clear after a restart, and dual-boot PCs may see disk errors on the other system."
            : "With fast startup off, every shutdown is a complete fresh start.\n\n"
              + "Pros: the cleanest start every time; safe for dual-boot; fixes odd problems after shutdown.\nCons: a cold boot takes a few seconds longer.";
        message += "\n\n" + RestorePointNote;
        bool ok = await _dialogs.ConfirmAsync(target ? "Turn fast startup on?" : "Turn fast startup off?", message, target ? "Turn on" : "Turn off");
        if (!ok)
        {
            OnPropertyChanged(nameof(FastStartup));
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _startup.SetFastStartupAsync(target, ct);
            if (result.Success)
            {
                _notifications.Success("Fast startup " + (target ? "on" : "off"), result.Message);
                ApplyBoot(await _startup.GetBootConfigAsync(ct));
                RebuildWarnings();
            }
            else
            {
                OnPropertyChanged(nameof(FastStartup));
                _notifications.Error("Fast startup was not changed", result.Message);
            }
        }, target ? "Turning fast startup on…" : "Turning fast startup off…");
    }

    [RelayCommand]
    private async Task ApplyBootTimeoutAsync()
    {
        int seconds = Math.Clamp((int)Math.Round(BootTimeoutSeconds), 0, 999);
        bool ok = await _dialogs.ConfirmAsync("Change the boot menu wait?",
            $"Windows will wait {seconds} second{(seconds == 1 ? string.Empty : "s")} at the boot menu before starting {BootDescription}. "
            + "The menu only appears when more than one operating system (or a recovery entry) is installed. 0 skips the menu entirely.", "Apply");
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _startup.SetBootTimeoutAsync(seconds, ct);
            if (result.Success)
            {
                _notifications.Success("Boot menu wait changed", result.Message);
                ApplyBoot(await _startup.GetBootConfigAsync(ct));
                RebuildWarnings();
            }
            else
            {
                _notifications.Error("Boot menu wait was not changed", result.Message);
            }
        }, "Updating the boot menu timeout…");
    }

    [RelayCommand]
    private async Task ToggleSafeModeAsync()
    {
        bool target = !(Boot?.SafeModeNextBoot ?? SafeModeNextBoot);
        bool ok;
        if (target)
        {
            ok = await _dialogs.ConfirmAsync("Start in Safe Mode next time?",
                "Safe Mode loads Windows with only basic drivers and no startup apps. It is useful for removing a bad driver or program.\n\n"
                + "Important: Windows keeps starting in Safe Mode until you turn this off. To get back to normal, open PC Checker (or Settings → Recovery) in Safe Mode and switch this off, "
                + "or run: bcdedit /deletevalue {current} safeboot\n\nNothing happens until you restart.", "Arm Safe Mode", isDanger: true);
        }
        else
        {
            ok = await _dialogs.ConfirmAsync("Start normally next time?", "Safe Mode will no longer be forced at the next restart.", "Turn off");
        }

        if (!ok)
        {
            OnPropertyChanged(nameof(SafeModeNextBoot));
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _startup.SetSafeModeNextBootAsync(target, ct);
            if (result.Success)
            {
                _notifications.Show(target ? "Safe Mode armed" : "Safe Mode disarmed", result.Message, target ? NotificationKind.Warning : NotificationKind.Success);
                ApplyBoot(await _startup.GetBootConfigAsync(ct));
                RebuildWarnings();
            }
            else
            {
                OnPropertyChanged(nameof(SafeModeNextBoot));
                _notifications.Error("Safe Mode was not changed", result.Message);
            }
        }, "Updating boot settings…");
    }

    [RelayCommand]
    private Task RestartToUefiAsync() => ScheduleRestartAsync(
        "Restart into UEFI (BIOS) settings?",
        "Your PC will restart straight into the firmware settings, where you can change XMP/EXPO memory profiles, fan curves and boot order.",
        "Restarting into the UEFI settings", _startup.RestartToUefiAsync);

    [RelayCommand]
    private Task RestartToAdvancedStartupAsync() => ScheduleRestartAsync(
        "Restart into Advanced startup?",
        "Your PC will restart into the Windows recovery menu (Startup Repair, System Restore, Safe Mode options, Command Prompt).",
        "Restarting into Advanced startup", _startup.RestartToAdvancedStartupAsync);

    /// <summary>
    /// Uses the shared restart flow (Danger confirmation, fixed countdown, Cancel restart) so these special restarts
    /// look and behave like every other "Restart now" in the app.
    /// </summary>
    private Task ScheduleRestartAsync(string title, string description, string busy, Func<CancellationToken, Task<Core.Common.OperationResult>> action)
    {
        string message = description + $"\n\nSave your work first. You get a {Core.Common.RestartPrompt.CountdownSeconds}-second countdown to cancel.";
        Func<CancellationToken, Task<Core.Common.OperationResult>>? cancel = _startup is IRestartCanceller canceller ? canceller.CancelPendingRestartAsync : null;
        return _restart.RequestAsync(title, message, Core.Common.RestartPrompt.ConfirmButtonText(), async ct =>
        {
            Core.Common.OperationResult result = Core.Common.OperationResult.Fail("The restart was not scheduled.");
            await RunBusyAsync(async innerCt => result = await action(innerCt), busy + "…");
            return result;
        }, cancel);
    }
}
