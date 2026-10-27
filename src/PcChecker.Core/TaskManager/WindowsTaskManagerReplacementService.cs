using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.TaskManager;

/// <summary>
/// Windows implementation of the "Replace Task Manager" option, through the Image File Execution Options Debugger
/// value, exactly as Sysinternals Process Explorer does it. It is only allowed when PC Checker runs from Program
/// Files, so no user-writable copy can take Task Manager's elevated slot. Any value already present is saved before
/// the option is turned on and put back when it is turned off; a stale value PC Checker owns is cleaned up at start.
/// </summary>
public sealed class WindowsTaskManagerReplacementService : ITaskManagerReplacementService
{
    private const string Module = "Task manager";
    private const string BackupKeyPath = @"SOFTWARE\PcChecker\TaskManagerReplacement";
    private const string BackupValueName = "PreviousDebugger";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsTaskManagerReplacementService> _logger;
    private readonly string _exePath;
    private readonly string[] _programFilesRoots;

    public WindowsTaskManagerReplacementService(IChangeGuard guard, ILogger<WindowsTaskManagerReplacementService> logger)
    {
        _guard = guard;
        _logger = logger;
        _exePath = ResolveExePath();
        _programFilesRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
    }

    public TaskManagerReplacementStatus GetStatus()
    {
        if (!PlatformGuard.IsWindows)
        {
            return TaskManagerReplacementStatus.Off;
        }

        string? value = ReadDebuggerValue();
        bool owned = TaskManagerReplacement.OwnsValue(value, _exePath);
        bool pointsElsewhere = !owned && TaskManagerReplacement.ParseDebuggerValue(value) is { HasSwitch: true };
        string? ineligible = TaskManagerReplacement.IneligibleReason(_exePath, _programFilesRoots);
        return new TaskManagerReplacementStatus(
            Enabled: owned,
            OwnedByUs: owned,
            Eligible: ineligible is null,
            IneligibleReason: ineligible,
            PointsElsewhere: pointsElsewhere,
            CurrentValue: value);
    }

    public Task<OperationResult> EnableAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("This option is only available on Windows."));
        }

        if (TaskManagerReplacement.IneligibleReason(_exePath, _programFilesRoots) is { } reason)
        {
            return Task.FromResult(OperationResult.Fail(reason));
        }

        string? previous = ReadDebuggerValue();
        var options = new ChangeOptions(false, false, Module, "Replace Task Manager", Reversible: true,
            UndoHint: "Turn 'Replace Task Manager' off on the Settings page to restore Windows Task Manager.");

        return _guard.RunGuardedAsync("Turn on: open PC Checker instead of Task Manager", options, ct =>
        {
            try
            {
                // Keep any value that was there (unless it is already ours) so it can be restored later.
                if (!string.IsNullOrEmpty(previous) && !TaskManagerReplacement.OwnsValue(previous, _exePath))
                {
                    SaveBackup(previous!);
                }

                WriteDebuggerValue(TaskManagerReplacement.BuildDebuggerValue(_exePath));
                return Task.FromResult(OperationResult.Ok("Ctrl+Shift+Esc and every other way of opening Task Manager now opens PC Checker on the Processes tab."));
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                return Task.FromResult(OperationResult.Fail($"The setting could not be written: {ex.Message}", ex));
            }
        }, null, cancellationToken);
    }

    public Task<OperationResult> DisableAsync(CancellationToken cancellationToken) => DisableAsync(cancellationToken, openTaskManager: false);

    public Task<OperationResult> DisableAndOpenWindowsTaskManagerAsync(CancellationToken cancellationToken) =>
        DisableAsync(cancellationToken, openTaskManager: true);

    private Task<OperationResult> DisableAsync(CancellationToken cancellationToken, bool openTaskManager)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("This option is only available on Windows."));
        }

        string? current = ReadDebuggerValue();
        if (!TaskManagerReplacement.OwnsValue(current, _exePath) && !string.IsNullOrEmpty(current)
            && TaskManagerReplacement.ParseDebuggerValue(current) is { HasSwitch: false })
        {
            return Task.FromResult(OperationResult.Fail(
                "Another program is set as Task Manager's debugger, not PC Checker, so PC Checker will not change it."));
        }

        var options = new ChangeOptions(false, false, Module, "Replace Task Manager", Reversible: false, UndoHint: null);
        return _guard.RunGuardedAsync("Turn off: open Windows Task Manager again", options, ct =>
        {
            try
            {
                RestoreOrClear();
                var message = "Windows Task Manager opens normally again.";
                if (openTaskManager)
                {
                    var open = ShellLauncher.Open("taskmgr.exe");
                    message = open.Success
                        ? "Turned the option off and opened Windows Task Manager."
                        : "Turned the option off. Windows Task Manager could not be opened automatically; press Ctrl+Shift+Esc.";
                }

                return Task.FromResult(OperationResult.Ok(message));
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                return Task.FromResult(OperationResult.Fail($"The setting could not be changed: {ex.Message}", ex));
            }
        }, null, cancellationToken);
    }

    public Task<string?> ReconcileOnStartAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.Run<string?>(() =>
        {
            try
            {
                string? current = ReadDebuggerValue();
                if (TaskManagerReplacement.IsStaleOwnedValue(current, _exePath, System.IO.File.Exists))
                {
                    RestoreOrClear();
                    _logger.LogInformation("Removed a stale 'Replace Task Manager' value left by a PC Checker that no longer exists.");
                    return "A leftover 'Replace Task Manager' setting from an old PC Checker was cleaned up.";
                }

                // The value points at this exe with --taskmgr but is spelled differently, or at another existing copy.
                if (TaskManagerReplacement.ParseDebuggerValue(current) is { HasSwitch: true } parsed
                    && !TaskManagerReplacement.OwnsValue(current, _exePath)
                    && !System.IO.File.Exists(parsed.ExePath))
                {
                    RestoreOrClear();
                    return "A 'Replace Task Manager' setting pointing at a missing PC Checker was cleaned up.";
                }

                return null;
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                _logger.LogWarning(ex, "The 'Replace Task Manager' setting could not be checked at start.");
                return null;
            }
        }, cancellationToken);
    }

    private void RestoreOrClear()
    {
        string? backup = ReadBackup();
        using var key = Registry.LocalMachine.CreateSubKey(TaskManagerReplacement.IfeoKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(backup))
        {
            key.SetValue(TaskManagerReplacement.DebuggerValueName, backup);
        }
        else if (key.GetValue(TaskManagerReplacement.DebuggerValueName) is not null)
        {
            key.DeleteValue(TaskManagerReplacement.DebuggerValueName, throwOnMissingValue: false);
        }

        ClearBackup();
    }

    private static string? ReadDebuggerValue()
    {
        using var key = Registry.LocalMachine.OpenSubKey(TaskManagerReplacement.IfeoKeyPath);
        return key?.GetValue(TaskManagerReplacement.DebuggerValueName) as string;
    }

    private static void WriteDebuggerValue(string value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(TaskManagerReplacement.IfeoKeyPath, writable: true)
            ?? throw new System.IO.IOException("The Image File Execution Options key could not be opened.");
        key.SetValue(TaskManagerReplacement.DebuggerValueName, value);
    }

    private static void SaveBackup(string previous)
    {
        using var key = Registry.LocalMachine.CreateSubKey(BackupKeyPath, writable: true);
        key?.SetValue(BackupValueName, previous);
    }

    private static string? ReadBackup()
    {
        using var key = Registry.LocalMachine.OpenSubKey(BackupKeyPath);
        return key?.GetValue(BackupValueName) as string;
    }

    private static void ClearBackup()
    {
        using var key = Registry.LocalMachine.OpenSubKey(BackupKeyPath, writable: true);
        key?.DeleteValue(BackupValueName, throwOnMissingValue: false);
    }

    private static string ResolveExePath()
    {
        try
        {
            var module = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(module))
            {
                return module!;
            }
        }
        catch (Exception)
        {
            // Fall through to the assembly location.
        }

        return System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "PcChecker.exe";
    }

    private static bool IsRegistryFailure(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException;
}
