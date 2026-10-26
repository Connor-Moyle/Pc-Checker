using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Startup;

/// <summary>Boot configuration half of <see cref="WindowsStartupService"/>: bcdedit, fast startup, hibernation, restarts.</summary>
public sealed partial class WindowsStartupService
{
    private const string HiberbootKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string HibernateKey = @"SYSTEM\CurrentControlSet\Control\Power";

    public async Task<BootConfig> GetBootConfigAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return BootConfig.Unknown;
        }

        BootManagerInfo manager = new(null, 0, false, null, []);
        BootLoaderInfo loader = new(null, false, null, false, null);
        try
        {
            var bootmgr = await CommandRunner.RunAsync("bcdedit.exe", "/enum {bootmgr}", TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (bootmgr.Success)
            {
                manager = BootConfigParser.ParseBootManager(bootmgr.StdOut);
            }
            else
            {
                _logger.LogWarning("bcdedit /enum {{bootmgr}} failed: {Error}", bootmgr.FirstLine);
            }

            var current = await CommandRunner.RunAsync("bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (current.Success)
            {
                loader = BootConfigParser.ParseBootLoader(current.StdOut);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the boot configuration.");
        }

        bool fastStartup = ReadDword(HiberbootKey, "HiberbootEnabled") == 1;
        bool hibernation = ReadDword(HibernateKey, "HibernateEnabled") == 1;
        DateTimeOffset? lastBoot = null;
        try
        {
            lastBoot = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        }
        catch
        {
        }

        double? bootSeconds = await ReadLastBootSecondsAsync(cancellationToken).ConfigureAwait(false);

        return new BootConfig(
            manager.TimeoutSeconds ?? 30,
            loader.Description,
            fastStartup,
            hibernation,
            loader.SafeMode,
            manager.MemoryDiagnosticScheduled,
            lastBoot,
            bootSeconds,
            loader.LegacyBootMenu);
    }

    public Task<OperationResult> SetBootTimeoutAsync(int seconds, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        int clamped = Math.Clamp(seconds, 0, 999);
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "Boot menu timeout", Reversible: true,
            UndoHint: "Set the timeout back on the Startup page or run: bcdedit /timeout 30");
        return _guard.RunGuardedAsync($"Set boot menu timeout to {clamped} s", options, async ct =>
        {
            var result = await CommandRunner.RunAsync("bcdedit.exe", $"/timeout {clamped}", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            return result.Success
                ? OperationResult.Ok($"The boot menu now waits {clamped} second{(clamped == 1 ? string.Empty : "s")}.")
                : OperationResult.Fail($"bcdedit failed: {result.FirstLine}");
        }, null, cancellationToken);
    }

    public Task<OperationResult> SetSafeModeNextBootAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "Safe Mode next boot", Reversible: true,
            UndoHint: enabled ? "Turn Safe Mode off on the Startup page, or run: bcdedit /deletevalue {current} safeboot" : "Run: bcdedit /set {current} safeboot minimal");
        return _guard.RunGuardedAsync(enabled ? "Arm Safe Mode for the next restart" : "Disarm Safe Mode", options, async ct =>
        {
            string args = enabled ? "/set {current} safeboot minimal" : "/deletevalue {current} safeboot";
            var result = await CommandRunner.RunAsync("bcdedit.exe", args, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (!result.Success)
            {
                // Deleting a value that is not there is fine.
                if (!enabled && (result.StdOut + result.StdErr).Contains("not", StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Ok("Safe Mode was not armed.");
                }

                return OperationResult.Fail($"bcdedit failed: {result.FirstLine}");
            }

            return enabled
                ? OperationResult.Ok("Windows will start in Safe Mode at the next restart. Come back to this page afterwards and turn it off, or run: bcdedit /deletevalue {current} safeboot", rebootRequired: true)
                : OperationResult.Ok("Windows will start normally at the next restart.");
        }, null, cancellationToken);
    }

    public Task<OperationResult> SetFastStartupAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        var options = new ChangeOptions(null, null, Module, "Fast startup", Reversible: true,
            UndoHint: $"Toggle fast startup back on the Startup page (HKLM\\{HiberbootKey}\\HiberbootEnabled = {(enabled ? 0 : 1)}).");
        return _guard.RunGuardedAsync(enabled ? "Turn fast startup on" : "Turn fast startup off", options, async ct =>
        {
            string note = string.Empty;
            if (enabled && ReadDword(HibernateKey, "HibernateEnabled") != 1)
            {
                var hib = await CommandRunner.RunAsync("powercfg.exe", "/hibernate on", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                if (!hib.Success)
                {
                    return OperationResult.Fail($"Fast startup needs hibernation, and it could not be turned on: {hib.FirstLine}");
                }

                note = " Hibernation was turned on because fast startup needs it.";
            }

            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(HiberbootKey, writable: true);
                if (key is null)
                {
                    return OperationResult.Fail("The power settings key could not be opened.");
                }

                key.SetValue("HiberbootEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail($"Could not write HiberbootEnabled: {ex.Message}", ex);
            }

            return OperationResult.Ok(enabled
                ? "Fast startup is on. The next shutdown will save the Windows kernel so the following boot is quicker." + note
                : "Fast startup is off. Every boot will be a full fresh start.");
        }, null, cancellationToken);
    }

    public Task<OperationResult> RestartToUefiAsync(CancellationToken cancellationToken) =>
        ScheduleRestartAsync("/r /fw /t " + RestartDelaySeconds, "Restart into the UEFI firmware settings",
            "The PC will restart into the UEFI (BIOS) settings in " + RestartDelaySeconds + " seconds. Save your work now.", cancellationToken);

    public Task<OperationResult> RestartToAdvancedStartupAsync(CancellationToken cancellationToken) =>
        ScheduleRestartAsync("/r /o /t " + RestartDelaySeconds, "Restart into Advanced startup",
            "The PC will restart into the Advanced startup menu in " + RestartDelaySeconds + " seconds. Save your work now.", cancellationToken);

    public async Task<OperationResult> CancelPendingRestartAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail(NotOnWindows);
        }

        var result = await CommandRunner.RunAsync("shutdown.exe", "/a", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _logger.LogInformation("Pending restart cancelled by the user.");
            return OperationResult.Ok("The restart was cancelled.");
        }

        // 1116: no shutdown in progress — nothing to cancel.
        return result.ExitCode == 1116
            ? OperationResult.Ok("There was no restart to cancel.")
            : OperationResult.Fail($"Could not cancel the restart: {result.FirstLine}");
    }

    public OperationResult OpenStartupAppsSettings() => ShellLauncher.Open("ms-settings:startupapps");

    public OperationResult OpenTaskScheduler() => ShellLauncher.Open("taskschd.msc");

    public OperationResult OpenSystemConfiguration() => ShellLauncher.Open("msconfig.exe");

    private Task<OperationResult> ScheduleRestartAsync(string arguments, string description, string okMessage, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "Restart", Reversible: true, UndoHint: "Cancel within the countdown (shutdown /a).");
        return _guard.RunGuardedAsync(description, options, async ct =>
        {
            var result = await CommandRunner.RunAsync("shutdown.exe", arguments, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (result.Success)
            {
                return OperationResult.Ok(okMessage, rebootRequired: true);
            }

            string reason = result.FirstLine;
            if (arguments.Contains("/fw", StringComparison.Ordinal) && (result.ExitCode == 1 || reason.Contains("firmware", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "this PC's firmware does not support it (legacy BIOS). Press the BIOS key (often Del, F2 or F10) right after powering on instead.";
            }
            else if (result.ExitCode == 1190)
            {
                reason = "a restart is already pending. Cancel it first.";
            }

            return OperationResult.Fail($"Could not schedule the restart: {reason}");
        }, null, cancellationToken);
    }

    private async Task<double?> ReadLastBootSecondsAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $e = Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Diagnostics-Performance/Operational'; Id=100} -MaxEvents 1 -ErrorAction SilentlyContinue
            if ($e) { $x = [xml]$e.ToXml(); ($x.Event.EventData.Data | Where-Object { $_.Name -eq 'BootTime' }).'#text' }
            """;
        try
        {
            var result = await PowerShellRunner.RunAsync(script, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return result.Success ? BootConfigParser.ParseBootTimeSeconds(result.StdOut) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Boot time event could not be read.");
            return null;
        }
    }

    private static int? ReadDword(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName) is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }
}
