using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Processes;

/// <summary>
/// The Task Manager actions: close windows, suspend and resume, efficiency mode, affinity, window commands, file
/// properties, memory dumps, restart and Run new task. Each change is journaled through the change guard.
/// </summary>
public sealed partial class WindowsProcessService
{
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RestartCloseWait = TimeSpan.FromSeconds(8);
    private const int ErrorNotRunning = 87;
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const long DumpHeadroomBytes = 256L * 1024 * 1024;

    private static string WindowsDirectoryPath
    {
        get
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return string.IsNullOrWhiteSpace(folder) ? ProcessSuspicionRules.DefaultWindowsDirectory : folder;
        }
    }

    public Task<OperationResult<bool>> CloseWindowsAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (Precheck(process) is { } refusal)
        {
            return Task.FromResult(OperationResult<bool>.Fail(refusal));
        }

        if (ProcessSafetyRules.IsShellExplorer(process, WindowsDirectoryPath))
        {
            // A close request to the desktop opens "Shut Down Windows"; End task ends File Explorer directly instead.
            return Task.FromResult(OperationResult<bool>.Fail("File Explorer also draws the desktop and taskbar, so it is not asked to close. Use Restart, or End task, which ends it directly."));
        }

        var options = Options(process, reversible: false, "A closed program cannot be reopened from the journal. Start it again from the Start menu.");
        return _guard.RunGuardedAsync($"Close the windows of {Target(process)}", options, ct => Task.Run(() =>
        {
            int asked = ProcessWindows.RequestClose(process.Pid);
            if (asked == 0)
            {
                return OperationResult<bool>.Ok(false, $"{process.Name} has no window to close.");
            }

            bool exited = ProcessNative.WaitForExit(process.Pid, process.StartTime!.Value.ToFileTime(), CloseWait);
            return OperationResult<bool>.Ok(exited, exited ? $"{process.Name} was closed." : $"{process.Name} is still open. It may be asking to save your work.");
        }, ct), null, cancellationToken);
    }

    // Processes PC Checker paused, by process id and start time, so they can always be resumed.
    private readonly HashSet<(int Pid, long Created)> _suspendedHere = [];

    public bool WasSuspendedByPcChecker(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.StartTime is not { } start)
        {
            return false;
        }

        lock (_suspendedHere)
        {
            return _suspendedHere.Contains((process.Pid, start.ToFileTime()));
        }
    }

    private void MarkSuspendedHere(ProcessInfo process, bool suspended)
    {
        var key = (process.Pid, process.StartTime!.Value.ToFileTime());
        lock (_suspendedHere)
        {
            if (suspended)
            {
                _suspendedHere.Add(key);
            }
            else
            {
                _suspendedHere.Remove(key);
            }
        }
    }

    public Task<OperationResult> SuspendAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        string? refusal = Precheck(process) ?? ProcessSafetyRules.SuspendRefusal(process, WindowsDirectoryPath, OwnProcessId);
        if (refusal is not null)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        var options = Options(process, reversible: true, "Use Resume on the Processes page. Closing PC Checker does not resume it.");
        return _guard.RunGuardedAsync($"Suspend {Target(process)}", options, _ => Task.Run(() =>
        {
            int error = ProcessNative.SuspendOrResume(process.Pid, process.StartTime!.Value.ToFileTime(), suspend: true);
            if (error == 0)
            {
                MarkSuspendedHere(process, suspended: true);
            }

            return Outcome(error, process, $"{process.Name} is paused. It uses no processor time until you resume it.", "pause");
        }), null, cancellationToken);
    }

    public Task<OperationResult> ResumeAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        string? refusal = process.StartTime is null
            ? Unidentified(process)
            : ProcessSafetyRules.ResumeRefusal(process, WindowsDirectoryPath, OwnProcessId, WasSuspendedByPcChecker(process));
        if (refusal is not null)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        var options = Options(process, reversible: true, "Use Suspend to pause it again.");
        return _guard.RunGuardedAsync($"Resume {Target(process)}", options, _ => Task.Run(() =>
        {
            int error = ProcessNative.SuspendOrResume(process.Pid, process.StartTime!.Value.ToFileTime(), suspend: false);
            if (error == 0)
            {
                MarkSuspendedHere(process, suspended: false);
            }

            return Outcome(error, process, $"{process.Name} runs again.", "resume");
        }), null, cancellationToken);
    }

    public Task<OperationResult> SetEfficiencyModeAsync(ProcessInfo process, bool on, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        string? refusal = Precheck(process) ?? ProcessSafetyRules.EfficiencyRefusal(process, WindowsDirectoryPath, OwnProcessId);
        if (refusal is not null)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        var options = Options(process, reversible: true, on
            ? "Turn efficiency mode off on the Processes page. It also ends when the program restarts."
            : "Turn efficiency mode on again on the Processes page.");
        string verb = on ? "Turn on efficiency mode for" : "Turn off efficiency mode for";
        return _guard.RunGuardedAsync($"{verb} {Target(process)}", options, _ => Task.Run(() =>
            Outcome(ProcessNative.SetEfficiency(process.Pid, process.StartTime!.Value.ToFileTime(), on), process,
                on ? $"{process.Name} now runs in efficiency mode: Windows gives it less power and Low priority."
                   : $"Efficiency mode is off for {process.Name}; it runs at Normal priority again.",
                "change efficiency mode for")), null, cancellationToken);
    }

    public ulong? GetAffinity(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return PlatformGuard.IsWindows ? ProcessNative.GetAffinity(process.Pid) : null;
    }

    public Task<OperationResult> SetAffinityAsync(ProcessInfo process, ulong mask, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        string? refusal = Precheck(process) ?? AffinityMath.Validate(mask, LogicalProcessorCount);
        if (refusal is not null)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        ulong? before = ProcessNative.GetAffinity(process.Pid);
        string was = before is { } b ? AffinityMath.Describe(b, LogicalProcessorCount) : "unknown";
        var options = Options(process, reversible: true, $"Set the affinity back to {was} on the Processes page. It resets when the program restarts.");
        string now = AffinityMath.Describe(mask, LogicalProcessorCount);
        return _guard.RunGuardedAsync($"Set affinity of {Target(process)} to {now}", options, _ => Task.Run(() =>
            Outcome(ProcessNative.SetAffinity(process.Pid, process.StartTime!.Value.ToFileTime(), mask), process,
                $"{process.Name} now runs on {now} (was {was}). It resets when the program restarts.", "change the affinity of")), null, cancellationToken);
    }

    public OperationResult ShowWindow(ProcessInfo process, WindowCommand command)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.HasWindow)
        {
            return OperationResult.Fail($"{process.Name} has no window to show.");
        }

        return ProcessWindows.Show(process.WindowHandle, process.Pid, command)
            ? OperationResult.Ok()
            : OperationResult.Fail($"The window of {process.Name} could not be brought up. It may have just closed.");
    }

    public OperationResult ShowProperties(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!ProcessSuspicionRules.IsFullPath(process.Path) || !File.Exists(process.Path))
        {
            return OperationResult.Fail($"The program file of {process.Name} is not known or no longer exists.");
        }

        return ProcessWindows.ShowFileProperties(process.Path)
            ? OperationResult.Ok()
            : OperationResult.Fail($"Windows could not show the properties of {process.Name}.");
    }

    public async Task<OperationResult<string>> CreateDumpAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        string? refusal = !PlatformGuard.IsWindows ? NotOnWindows
            : process.StartTime is null ? Unidentified(process)
            : ProcessSafetyRules.DumpRefusal(process, OwnProcessId);
        if (refusal is not null)
        {
            return OperationResult<string>.Fail(refusal);
        }

        // Finding a free name and checking the free space touch the disk (Temp may be on a slow or network drive),
        // so they run off the UI thread.
        var (path, spaceRefusal) = await Task.Run(() => PrepareDump(process), cancellationToken).ConfigureAwait(false);
        if (spaceRefusal is not null)
        {
            return OperationResult<string>.Fail(spaceRefusal);
        }

        var options = Options(process, reversible: true, $"Delete {path} when you no longer need it.");
        return await _guard.RunGuardedAsync($"Create memory dump of {Target(process)}", options, _ => Task.Run(() =>
        {
            int error = ProcessNative.WriteDump(process.Pid, process.StartTime!.Value.ToFileTime(), path);
            if (error == 0)
            {
                return OperationResult<string>.Ok(path, $"Saved the memory of {process.Name} to {path}.");
            }

            TryDelete(path);
            return OperationResult<string>.Fail(error switch
            {
                ErrorNotRunning => $"{process.Name} is no longer running.",
                5 or StatusAccessDenied or unchecked((int)0x80070005) => $"Windows refused to read the memory of {process.Name}. It is protected.",
                _ => $"The dump file of {process.Name} could not be written (error {error}).",
            });
        }), null, cancellationToken).ConfigureAwait(false);
    }

    private (string Path, string? Refusal) PrepareDump(ProcessInfo process)
    {
        string folder = Path.GetTempPath();
        string path = CommandLineText.DumpFilePath(folder, process.Name, File.Exists);
        try
        {
            long free = new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
            if (free < (process.PrivateBytes * 2) + DumpHeadroomBytes)
            {
                return (path, $"There is not enough free space on {Path.GetPathRoot(folder)} for a dump of {process.Name} (it can be as large as the program's memory). Free up some space first.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check the free space for a dump.");
        }

        return (path, null);
    }

    public OperationResult ShowInFolder(string path)
    {
        if (!ProcessSuspicionRules.IsFullPath(path) || !File.Exists(path))
        {
            return OperationResult.Fail("The file no longer exists.");
        }

        // Explorer only understands /select with the path glued on; the path is a full existing file name, which
        // cannot contain a quote (see OpenFileLocation).
        return ShellLauncher.Open("explorer.exe", $"/select,\"{path}\"");
    }

    public Task<OperationResult> RestartAsync(ProcessInfo process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        string? refusal = Precheck(process)
            ?? ProcessSafetyRules.RestartRefusal(process, WindowsDirectoryPath, OwnProcessId, Environment.UserName);
        if (refusal is not null)
        {
            return Task.FromResult(OperationResult.Fail(refusal));
        }

        var options = Options(process, reversible: false, "The program was closed and started again; unsaved work in it may be lost.");
        return _guard.RunGuardedAsync($"Restart {Target(process)}", options, ct => Task.Run(() => RestartCore(process), ct), null, cancellationToken);
    }

    public async Task<OperationResult> RunNewTaskAsync(string command, bool asAdministrator, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail(NotOnWindows);
        }

        // The file checks below can touch slow network folders (on the PATH or typed), so they run off the UI thread.
        var (parsed, refusal) = await Task.Run(() => PrepareRunCommand(command, asAdministrator), cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return OperationResult.Fail(refusal);
        }

        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, parsed!.Target, Reversible: false,
            UndoHint: "Close the program if you did not want it.");
        string description = asAdministrator ? $"Run new task as administrator: {command.Trim()}" : $"Run new task: {command.Trim()}";
        return await _guard.RunGuardedAsync(description, options, _ => Task.Run(() => Launch(parsed, asAdministrator)), null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a Run new task command and checks it. With administrator rights the target is replaced by the full path
    /// it was found at, so Windows does not look the name up again (in the current folder or the user's own lists).
    /// </summary>
    private static (RunCommand? Command, string? Refusal) PrepareRunCommand(string command, bool asAdministrator)
    {
        var parsed = CommandLineText.ParseRunCommand(command, p => File.Exists(p) || Directory.Exists(p));
        if (parsed is null)
        {
            return (null, "Type the name of a program, folder, document or web address.");
        }

        bool looksLikePath = parsed.Target.Contains('\\', StringComparison.Ordinal) && !parsed.Target.Contains("://", StringComparison.Ordinal);
        if (looksLikePath && !File.Exists(parsed.Target) && !Directory.Exists(parsed.Target))
        {
            return (null, $"Windows cannot find \"{parsed.Target}\". Check the name and try again.");
        }

        if (!asAdministrator)
        {
            return (parsed, null);
        }

        var decision = ProcessSafetyRules.DecideElevatedLaunch(parsed.Target, WindowsDirectoryPath, ProgramFilesFolders(), Directory.Exists,
            name => ProcessSafetyRules.ResolveOnPath(name, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"), File.Exists),
            MachineAppPath);
        return decision.Refusal is not null ? (null, decision.Refusal) : (parsed with { Target = decision.Target! }, null);
    }

    /// <summary>
    /// The program a bare name stands for in the machine-wide App Paths list (HKEY_LOCAL_MACHINE only: the per-user
    /// list can be changed by any program the user runs). Null when it is not listed or the file is missing.
    /// </summary>
    private static string? MachineAppPath(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Default);
            foreach (string candidate in Path.HasExtension(name) ? new[] { name } : new[] { name + ".exe", name })
            {
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + candidate);
                if (key?.GetValue(null) is not string value || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                if (ProcessSuspicionRules.IsFullPath(path) && File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch
        {
            // Not listed as far as PC Checker can tell.
        }

        return null;
    }

    private OperationResult RestartCore(ProcessInfo process)
    {
        long created = process.StartTime!.Value.ToFileTime();
        if (ProcessSafetyRules.IsExplorer(process))
        {
            // Ended with a non-zero exit code, File Explorer is started again by Windows itself (with the user's rights).
            var (outcome, _) = ProcessNative.Terminate(process.Pid, created, CloseWait);
            if (outcome is ProcessNative.EndOutcome.AccessDenied or ProcessNative.EndOutcome.Failed)
            {
                return OperationResult.Fail("Windows refused to restart File Explorer.");
            }

            Thread.Sleep(TimeSpan.FromSeconds(3));
            if (!ProcessNative.EnumerateProcesses().Any(e => ProcessCatalog.Normalize(e.Name) == "explorer"))
            {
                return Launch(new RunCommand("explorer.exe", string.Empty), asAdministrator: false);
            }

            return OperationResult.Ok("File Explorer was restarted. The taskbar and desktop come back in a moment.");
        }

        string path = process.Path!;
        string arguments = CommandLineText.ArgumentsAfterExecutable(process.CommandLine, process.Path);
        bool exited = false;
        if (process.HasWindow && ProcessWindows.RequestClose(process.Pid) > 0)
        {
            exited = ProcessNative.WaitForExit(process.Pid, created, RestartCloseWait);
        }

        // No cancellation from here on: once the program is closed it must be started again.
        if (!exited)
        {
            var (outcome, error) = ProcessNative.Terminate(process.Pid, created, CloseWait);
            if (outcome is ProcessNative.EndOutcome.AccessDenied or ProcessNative.EndOutcome.Critical or ProcessNative.EndOutcome.Failed)
            {
                return OperationResult.Fail($"Windows refused to close {process.Name} (error {error}), so it was not restarted.");
            }
        }

        // Started again with the user's normal rights unless it had administrator rights and lives in Program Files or
        // the Windows folder. A file anywhere else could have been swapped while the program was closed, and PC Checker
        // (already an administrator) would pass its rights on without a Windows prompt.
        bool hadAdmin = process.IsElevated == true;
        bool elevated = hadAdmin && ProcessSafetyRules.MayRunElevated(path, WindowsDirectoryPath, ProgramFilesFolders());
        var launched = Launch(new RunCommand(path, arguments), elevated, Path.GetDirectoryName(path));
        string note = process.CommandLine is null ? " Its original options could not be read, so it started without them." : string.Empty;
        if (hadAdmin && !elevated)
        {
            note += " It had administrator rights before, but PC Checker started it without them because its file is outside Program Files and Windows. Start it as administrator yourself if it needs them.";
        }

        return launched.Success
            ? OperationResult.Ok($"{process.Name} was asked to start again.{note}")
            : OperationResult.Fail($"{process.Name} was closed but could not be started again: {launched.Message}");
    }

    /// <summary>
    /// Starts a program. The arguments are passed on exactly as typed or as the program had them: splitting and
    /// re-quoting them would change their meaning for programs that read their own command line.
    /// </summary>
    private OperationResult Launch(RunCommand command, bool asAdministrator, string? workingDirectory = null)
    {
        try
        {
            if (asAdministrator)
            {
                var psi = new ProcessStartInfo(command.Target) { UseShellExecute = true, Verb = "runas", Arguments = command.Arguments };
                if (workingDirectory is not null)
                {
                    psi.WorkingDirectory = workingDirectory;
                }

                using var started = Process.Start(psi);
                return OperationResult.Ok($"Started {command.Target} with administrator rights.");
            }

            // The desktop opens it later, on its own: a wrong name shows Windows' own error message, not ours.
            UnelevatedLauncher.Open(command.Target, command.Arguments, workingDirectory);
            return OperationResult.Ok($"Asked Windows to open {command.Target}.");
        }
        catch (Exception ex) when (ProcessCatalog.Normalize(command.Target) == "explorer" && !asAdministrator)
        {
            // Without a desktop there is nobody to ask, and a missing taskbar is worse than the alternative: File
            // Explorer is started directly from PC Checker so the taskbar comes back. It then has administrator rights
            // (as PC Checker does) until it is restarted or the user signs out, so the message says so. Windows
            // normally starts File Explorer again by itself, so this is only reached when it did not.
            _logger.LogInformation(ex, "Starting File Explorer directly because the desktop is not running.");
            try
            {
                using var started = Process.Start(new ProcessStartInfo(Path.Combine(WindowsDirectoryPath, "explorer.exe")) { UseShellExecute = true });
                return OperationResult.Ok("Started File Explorer. It runs with administrator rights until you sign out or restart it; sign out and in again when you can.");
            }
            catch (Exception inner)
            {
                return OperationResult.Fail($"File Explorer could not be started: {inner.Message}", inner);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Starting {Target} failed.", command.Target);
            return asAdministrator
                ? OperationResult.Fail($"Windows could not start {command.Target}: {ex.Message}", ex)
                : OperationResult.Fail(
                    $"Windows could not start {command.Target} without administrator rights (File Explorer's desktop must be running for that). Tick \"Create this task with administrative privileges\" to start it anyway.",
                    ex);
        }
    }

    private static IReadOnlyList<string> ProgramFilesFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty,
    ];

    /// <summary>Common refusals for actions on one process: not Windows, critical, or a process that cannot be told apart from a newer one.</summary>
    private string? Precheck(ProcessInfo process) =>
        !PlatformGuard.IsWindows ? NotOnWindows : Refusal(process);

    private static string Unidentified(ProcessInfo process) =>
        $"PC Checker cannot tell {process.Name} apart from a newer process with the same ID, so it leaves it alone.";

    private static string Target(ProcessInfo process) => $"{process.Name} (PID {process.Pid})";

    private static ChangeOptions Options(ProcessInfo process, bool reversible, string undoHint) =>
        new(CreateBefore: false, CreateAfter: false, Module, Target(process), reversible, undoHint);

    private static OperationResult Outcome(int error, ProcessInfo process, string success, string verb) => error switch
    {
        0 => OperationResult.Ok(success),
        ErrorNotRunning => OperationResult.Fail($"{process.Name} is no longer running."),
        5 or StatusAccessDenied => OperationResult.Fail($"Windows refused to {verb} {process.Name}. It is protected or belongs to another account."),
        _ => OperationResult.Fail($"Could not {verb} {process.Name} (error {error})."),
    };

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove the incomplete dump file {Path}.", path);
        }
    }
}
