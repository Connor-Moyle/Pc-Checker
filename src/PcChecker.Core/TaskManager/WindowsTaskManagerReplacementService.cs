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

        string? value;
        try
        {
            value = ReadDebuggerValue();
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            _logger.LogWarning(ex, "The Task Manager Debugger value could not be read.");
            value = null;
        }

        var kind = TaskManagerReplacement.Classify(value, _exePath, System.IO.File.Exists);
        bool owned = kind == DebuggerValueKind.Ours;
        string? ineligible = IneligibleReason();
        return new TaskManagerReplacementStatus(
            Enabled: owned,
            OwnedByUs: owned,
            Eligible: ineligible is null,
            IneligibleReason: ineligible,
            PointsElsewhere: kind == DebuggerValueKind.OtherCopy,
            CurrentValue: value)
        {
            Kind = kind,
        };
    }

    public Task<OperationResult> EnableAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("This option is only available on Windows."));
        }

        if (IneligibleReason() is { } reason)
        {
            return Task.FromResult(OperationResult.Fail(reason));
        }

        string? previous;
        try
        {
            previous = ReadDebuggerValue();
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return Task.FromResult(OperationResult.Fail($"The current setting could not be read: {ex.Message}", ex));
        }

        var kind = TaskManagerReplacement.Classify(previous, _exePath, System.IO.File.Exists);
        var options = new ChangeOptions(false, false, Module, "Replace Task Manager", Reversible: true,
            UndoHint: kind == DebuggerValueKind.ThirdParty
                ? $"Turn 'Replace Task Manager' off on the Settings page to restore the previous value: {previous}"
                : "Turn 'Replace Task Manager' off on the Settings page to restore Windows Task Manager.");
        string description = kind == DebuggerValueKind.OtherCopy
            ? "Point 'Replace Task Manager' at this copy of PC Checker"
            : "Turn on: open PC Checker instead of Task Manager";

        return _guard.RunGuardedAsync(description, options, ct =>
        {
            try
            {
                // Keep a third-party value (Process Explorer, ...) so turning the option off puts it back. A value left by
                // another PC Checker copy is not kept: the backup it made, if any, stays in place.
                if (kind == DebuggerValueKind.ThirdParty)
                {
                    SaveBackup(previous!);
                }

                WriteDebuggerValue(TaskManagerReplacement.BuildDebuggerValue(_exePath));
                return Task.FromResult(OperationResult.Ok(kind == DebuggerValueKind.OtherCopy
                    ? "Task Manager now opens this copy of PC Checker on the Processes tab."
                    : "Ctrl+Shift+Esc and every other way of opening Task Manager now opens PC Checker on the Processes tab."));
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

        string? current;
        try
        {
            current = ReadDebuggerValue();
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return Task.FromResult(OperationResult.Fail($"The current setting could not be read: {ex.Message}", ex));
        }

        if (TaskManagerReplacement.Classify(current, _exePath, System.IO.File.Exists) == DebuggerValueKind.ThirdParty)
        {
            if (openTaskManager)
            {
                return Task.FromResult(OperationResult.Fail(
                    "Another program is set to open in place of Task Manager, so Windows Task Manager cannot be opened from here."));
            }

            return Task.FromResult(OperationResult.Fail(
                "Another program is set as Task Manager's debugger, not PC Checker, so PC Checker will not change it."));
        }

        var options = new ChangeOptions(false, false, Module, "Replace Task Manager", Reversible: true,
            UndoHint: "Turn 'Replace Task Manager' on again on the Settings page.");
        return _guard.RunGuardedAsync("Turn off: open Windows Task Manager again", options, ct =>
        {
            try
            {
                string? restored = RestoreOrClear();
                var message = restored is null
                    ? "Windows Task Manager opens normally again."
                    : $"The program set in Task Manager's place before PC Checker was put back: {restored}";
                if (openTaskManager)
                {
                    // With a previous value put back, Windows opens that program instead of its own Task Manager.
                    var open = ShellLauncher.Open(System.IO.Path.Combine(Environment.SystemDirectory, "Taskmgr.exe"));
                    message = restored is not null
                        ? open.Success
                            ? $"Turned the option off and put back the program set in Task Manager's place before PC Checker, which opened instead of Windows Task Manager: {restored}"
                            : $"Turned the option off and put back the program set in Task Manager's place before PC Checker: {restored}"
                        : open.Success
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

    public Task<TaskManagerStartCheck> ReconcileOnStartAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(TaskManagerStartCheck.Nothing);
        }

        return Task.Run(async () =>
        {
            try
            {
                string? current = ReadDebuggerValue();
                switch (TaskManagerReplacement.Classify(current, _exePath, System.IO.File.Exists))
                {
                    case DebuggerValueKind.Stale:
                    {
                        // PC Checker was uninstalled or moved: the value would start a missing file, so Task Manager
                        // would not open at all. It carries --taskmgr, so it is ours to remove. Recorded in the Action
                        // Journal like any other change (no restore point for one registry value).
                        string? restored = null;
                        var options = new ChangeOptions(false, false, Module, "Replace Task Manager", Reversible: false, UndoHint: null);
                        var result = await _guard.RunGuardedAsync("Clean up a leftover 'Replace Task Manager' setting", options, _ =>
                        {
                            restored = RestoreOrClear();
                            return Task.FromResult(OperationResult.Ok(restored is null
                                ? "Removed the leftover value, so Windows Task Manager opens again."
                                : $"Removed the leftover value and put back the previous one: {restored}"));
                        }, null, cancellationToken).ConfigureAwait(false);
                        if (!result.Success)
                        {
                            _logger.LogWarning("A stale 'Replace Task Manager' value could not be cleaned up: {Message}", result.Message);
                            return TaskManagerStartCheck.Nothing;
                        }

                        _logger.LogInformation("Removed a stale 'Replace Task Manager' value left by a PC Checker that no longer exists.");
                        return new TaskManagerStartCheck(restored is null
                            ? "A leftover 'Replace Task Manager' setting from an old PC Checker was cleaned up, so Windows Task Manager opens again."
                            : $"A leftover 'Replace Task Manager' setting from an old PC Checker was cleaned up and the program set before it was put back: {restored}", null);
                    }

                    case DebuggerValueKind.OtherCopy:
                        // Another copy that still exists: never change it silently, offer to point it here instead.
                        return new TaskManagerStartCheck(null, TaskManagerReplacement.ParseDebuggerValue(current)?.ExePath);

                    default:
                        return TaskManagerStartCheck.Nothing;
                }
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                _logger.LogWarning(ex, "The 'Replace Task Manager' setting could not be checked at start.");
                return TaskManagerStartCheck.Nothing;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Null when the option may be turned on here: PC Checker runs from Program Files and neither the exe nor its folder
    /// can be changed by a standard user. Otherwise a plain reason.
    /// </summary>
    private string? IneligibleReason()
    {
        if (TaskManagerReplacement.IneligibleReason(_exePath, _programFilesRoots) is { } reason)
        {
            return reason;
        }

        if (UserWritable(_exePath) || UserWritable(System.IO.Path.GetDirectoryName(_exePath)))
        {
            return "PC Checker's install folder can be changed by standard users, which could let another program take "
                + "Task Manager's place, so this option stays off. Move PcChecker.exe to a folder only administrators can change, such as "
                + "C:\\Program Files\\PC Checker (\"Install in Program Files\" in Settings does this), and start it from there.";
        }

        return null;
    }

    /// <summary>
    /// True when Users, Authenticated Users, Everyone or Interactive may write, append, delete or change the permissions
    /// of the file or folder. Unknown (no access to read the permissions) counts as writable, to stay safe.
    /// </summary>
    private bool UserWritable(string? path)
    {
        if (string.IsNullOrEmpty(path) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            System.Security.AccessControl.FileSystemSecurity security = System.IO.Directory.Exists(path)
                ? new System.IO.DirectoryInfo(path).GetAccessControl()
                : new System.IO.FileInfo(path).GetAccessControl();
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
            var broad = new[]
            {
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.InteractiveSid, null),
            };
            const System.Security.AccessControl.FileSystemRights Dangerous =
                System.Security.AccessControl.FileSystemRights.WriteData
                | System.Security.AccessControl.FileSystemRights.AppendData
                | System.Security.AccessControl.FileSystemRights.Delete
                | System.Security.AccessControl.FileSystemRights.ChangePermissions
                | System.Security.AccessControl.FileSystemRights.TakeOwnership;
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                    && rule.IdentityReference is System.Security.Principal.SecurityIdentifier sid
                    && broad.Contains(sid)
                    && (rule.FileSystemRights & Dangerous) != 0
                    && (rule.PropagationFlags & System.Security.AccessControl.PropagationFlags.InheritOnly) == 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or InvalidOperationException
            or System.Security.Principal.IdentityNotMappedException or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "Could not read the permissions of {Path}.", path);
            return true;
        }
    }

    /// <summary>
    /// Puts back the saved third-party value when its program still exists, otherwise removes the value. Returns the
    /// value put back, or null when Windows Task Manager opens again.
    /// </summary>
    private string? RestoreOrClear()
    {
        string? backup = ReadBackup();
        using var key = Registry.LocalMachine.CreateSubKey(TaskManagerReplacement.IfeoKeyPath, writable: true);
        if (key is null)
        {
            return null;
        }

        string? restored = null;
        if (TaskManagerReplacement.ShouldRestoreBackup(backup, System.IO.File.Exists))
        {
            key.SetValue(TaskManagerReplacement.DebuggerValueName, backup!);
            restored = backup;
        }
        else
        {
            if (!string.IsNullOrEmpty(backup))
            {
                _logger.LogInformation("The previous Task Manager value was not put back because its program no longer exists: {Value}", backup);
            }

            if (key.GetValue(TaskManagerReplacement.DebuggerValueName) is not null)
            {
                key.DeleteValue(TaskManagerReplacement.DebuggerValueName, throwOnMissingValue: false);
            }
        }

        ClearBackup();
        return restored;
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
