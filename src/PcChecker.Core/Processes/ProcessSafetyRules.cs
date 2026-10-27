namespace PcChecker.Core.Processes;

/// <summary>
/// Which Task Manager actions PC Checker offers for a process, with a plain reason when it does not. Builds on
/// <see cref="CriticalProcessRules"/> (never end or change Windows' core processes or PC Checker itself) and adds the
/// rules for pausing, efficiency mode, restarting and memory dumps.
/// </summary>
public static class ProcessSafetyRules
{
    // Folders inside Windows that every user may write to.
    private static readonly string[] WritableWindowsFolders =
        ["Temp", "Tasks", "tracing", @"System32\Tasks", @"System32\spool\drivers\color", @"Registration\CRMLog", @"SysWOW64\Tasks"];

    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msc", ".lnk", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".scr", ".cpl", ".pif", ".hta", ".url", ".appref-ms", ".reg",
    };

    /// <summary>
    /// Why the process may not be suspended, or null. Windows' own programs are refused even when they show a window
    /// and sit under Apps (File Explorer's desktop, Settings, Task Manager): the rule looks at what the program is, not
    /// at the section it is shown in.
    /// </summary>
    public static string? SuspendRefusal(ProcessInfo process, string windowsDirectory, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, ownPid) is { } reason)
        {
            return reason;
        }

        if (IsWindowsOrShell(process, windowsDirectory))
        {
            return "This is part of Windows. Pausing it can freeze the desktop, sound or sign-in, so PC Checker does not offer it.";
        }

        return process.State == ProcessRunState.Suspended ? "It is already suspended. Use Resume to let it run again." : null;
    }

    /// <summary>Why the process may not be resumed, or null. Anything PC Checker could pause may be resumed.</summary>
    public static string? ResumeRefusal(ProcessInfo process, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Pid == ownPid || process.Pid <= CriticalProcessRules.SystemPid)
        {
            return CriticalProcessRules.Reason(process.Name, process.Pid, ownPid);
        }

        return process.State == ProcessRunState.Suspended ? null : "It is not suspended.";
    }

    /// <summary>
    /// Why efficiency mode may not be changed, or null. Windows manages its own processes, as in Task Manager, also
    /// the ones with a window (see <see cref="SuspendRefusal"/>).
    /// </summary>
    public static string? EfficiencyRefusal(ProcessInfo process, string windowsDirectory, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, ownPid) is { } reason)
        {
            return reason;
        }

        return IsWindowsOrShell(process, windowsDirectory)
            ? "Windows decides how its own processes save power, so efficiency mode is not offered for them (Task Manager does the same)."
            : null;
    }

    /// <summary>
    /// Why "Restart" is not offered, or null. Only programs started from a known file in the user's own session can be
    /// started again the same way; services, other accounts and Windows' own processes are left out, except File
    /// Explorer, which Windows itself starts again. Store apps (which Windows starts through their package, not their
    /// file) and the helper processes of browsers and Electron apps ("--type=renderer", started by their main
    /// process) are left out too: starting their file again would not bring the same thing back.
    /// </summary>
    public static string? RestartRefusal(ProcessInfo process, string windowsDirectory, int ownPid, string? currentUser)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (CriticalProcessRules.Reason(process.Name, process.Pid, ownPid) is { } reason)
        {
            return reason;
        }

        if (IsExplorer(process) && IsShellExplorer(process, windowsDirectory))
        {
            return null;
        }

        if (ProcessGrouping.IsWindowsProcess(process, windowsDirectory))
        {
            return process.HasWindow
                ? "This is a Windows program. Close it and open it again from the Start menu."
                : "This is part of Windows, which starts it again by itself when it is needed.";
        }

        if (!ProcessSuspicionRules.IsFullPath(process.Path))
        {
            return "PC Checker does not know which file started this program, so it cannot start it again.";
        }

        if (IsPackagedApp(process.Path))
        {
            return "This is a Microsoft Store app, which Windows starts in its own way. Close it and open it again from the Start menu.";
        }

        if (IsHelperProcess(process.CommandLine))
        {
            return "This is a helper process that its main program starts by itself. Restart the main program instead.";
        }

        if (process.SessionId == 0)
        {
            return "This runs as a Windows service. Restart it from the Services list instead.";
        }

        if (!string.IsNullOrWhiteSpace(currentUser) && !string.IsNullOrWhiteSpace(process.User)
            && !string.Equals(currentUser, process.User, StringComparison.OrdinalIgnoreCase))
        {
            return $"This program runs for another account ({process.User}), so it is not started again from here.";
        }

        return null;
    }

    /// <summary>
    /// Why no memory dump is offered, or null. Windows' core processes are left out: Local Security Authority holds
    /// sign-in secrets, and dumping the others needs rights Windows does not give.
    /// </summary>
    public static string? DumpRefusal(ProcessInfo process, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(process);
        string name = ProcessCatalog.Normalize(process.Name);
        if (name is "lsass" or "lsaiso")
        {
            return "This process holds sign-in secrets. A copy of its memory would contain them, so PC Checker does not create one.";
        }

        if (process.Pid == ownPid)
        {
            return null;
        }

        return CriticalProcessRules.Reason(process.Name, process.Pid, ownPid) is not null
            ? "Windows protects the memory of its core processes, so a dump file cannot be created for them."
            : null;
    }

    /// <summary>
    /// True when a program file may be started with administrator rights without a second look: it lives in Program
    /// Files or the Windows folder, which only administrators can change (their Temp, Tasks and similar folders that
    /// every user can write to excepted). A program anywhere else (Downloads, the Desktop, the user's folders, a games
    /// folder on another drive) could have been swapped by any program the user runs.
    /// </summary>
    public static bool MayRunElevated(string? path, string windowsDirectory, IEnumerable<string> programFilesFolders)
    {
        ArgumentNullException.ThrowIfNull(programFilesFolders);
        if (!ProcessSuspicionRules.IsFullPath(path) || ProcessSuspicionRules.IsUserWritableLocation(path)
            || path!.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (ProcessSuspicionRules.IsUnder(path, windowsDirectory))
        {
            return !WritableWindowsFolders.Any(sub => ProcessSuspicionRules.IsUnder(path, windowsDirectory.TrimEnd('\\') + "\\" + sub));
        }

        return programFilesFolders.Any(folder => !string.IsNullOrWhiteSpace(folder) && ProcessSuspicionRules.IsUnder(path, folder));
    }

    /// <summary>True for files Windows runs as a program when opened (as opposed to documents, which open in their app).</summary>
    public static bool IsRunnableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string extension = System.IO.Path.GetExtension(path.Trim().TrimEnd('\\', '/'));
        return extension.Length == 0 || RunnableExtensions.Contains(extension);
    }

    /// <summary>
    /// The file a bare program name ("notepad", "cmd.exe") starts, the way Windows looks it up: each folder of the
    /// PATH variable with each extension of PATHEXT. Null when it is not found there (Windows may still know it from
    /// its App Paths list).
    /// </summary>
    public static string? ResolveOnPath(string name, string? pathVariable, string? pathExtensions, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/', ':']) >= 0 || string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        var extensions = (string.IsNullOrWhiteSpace(pathExtensions) ? ".COM;.EXE;.BAT;.CMD" : pathExtensions)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool hasExtension = System.IO.Path.HasExtension(name);
        foreach (string folder in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string dir = folder.Trim('"');
            if (!ProcessSuspicionRules.IsFullPath(dir))
            {
                continue;
            }

            string stem = dir.TrimEnd('\\') + "\\" + name;
            if (hasExtension && fileExists(stem))
            {
                return stem;
            }

            foreach (string extension in extensions)
            {
                string candidate = stem + extension.ToLowerInvariant();
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>True for Microsoft Store (packaged) apps, which live in WindowsApps or SystemApps.</summary>
    public static bool IsPackagedApp(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (path.Replace('/', '\\').Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
            || path.Replace('/', '\\').Contains(@"\SystemApps\", StringComparison.OrdinalIgnoreCase));

    /// <summary>True for the helper processes of Chromium browsers and Electron apps ("--type=renderer", "--type=gpu-process").</summary>
    public static bool IsHelperProcess(string? commandLine) =>
        commandLine is not null && commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase);

    // Windows' own programs, and File Explorer under any name of the Windows folder (it is also the desktop).
    private static bool IsWindowsOrShell(ProcessInfo process, string windowsDirectory) =>
        ProcessGrouping.IsWindowsProcess(process, windowsDirectory) || (IsExplorer(process) && IsShellExplorer(process, windowsDirectory));

    // explorer.exe from the Windows folder, or one whose file is not known (it is then treated as the real one).
    private static bool IsShellExplorer(ProcessInfo process, string windowsDirectory) =>
        !ProcessSuspicionRules.IsFullPath(process.Path) || ProcessSuspicionRules.IsUnder(process.Path!, windowsDirectory);

    /// <summary>True for File Explorer (the desktop and taskbar).</summary>
    public static bool IsExplorer(ProcessInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return ProcessCatalog.Normalize(process.Name) == "explorer";
    }
}
