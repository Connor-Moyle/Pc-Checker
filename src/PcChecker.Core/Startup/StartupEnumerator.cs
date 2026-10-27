using System.Diagnostics;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Startup;

/// <summary>
/// Enumerates everything that starts with Windows: registry Run/RunOnce keys (64-bit, 32-bit and per-user) with their
/// StartupApproved enabled flags, the user and common Startup folders, Task Scheduler logon/boot tasks and any unusual
/// Winlogon Shell/Userinit values. Shared with the Performance module. Returns an empty list off Windows and never throws.
/// </summary>
public static class StartupEnumerator
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnceSubKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string StartupApprovedSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    public const string TaskLocationPrefix = "Task Scheduler: ";

    /// <summary>Where a registry startup entry lives and which StartupApproved key tracks its enabled flag.</summary>
    public sealed record RegistryRunSource(RegistryHive Hive, RegistryView View, string SubKey, string ApprovedSubKey, string DisplayPrefix, bool IsRunOnce);

    public static IReadOnlyList<RegistryRunSource> RegistrySources { get; } =
    [
        new(RegistryHive.LocalMachine, RegistryView.Registry64, RunSubKey, StartupApprovedSubKey + @"\Run", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", false),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, RunSubKey, StartupApprovedSubKey + @"\Run32", @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", false),
        new(RegistryHive.CurrentUser, RegistryView.Default, RunSubKey, StartupApprovedSubKey + @"\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", false),
        new(RegistryHive.LocalMachine, RegistryView.Registry64, RunOnceSubKey, StartupApprovedSubKey + @"\Run", @"HKLM\Software\Microsoft\Windows\CurrentVersion\RunOnce", true),
        new(RegistryHive.CurrentUser, RegistryView.Default, RunOnceSubKey, StartupApprovedSubKey + @"\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce", true),
    ];

    public static Task<IReadOnlyList<StartupItem>> EnumerateAsync(CancellationToken cancellationToken) =>
        // The registry walk and Startup-folder listing are synchronous; run the whole enumeration on the thread pool so a
        // caller on the UI thread is never blocked by them.
        Task.Run(() => EnumerateCoreAsync(cancellationToken), cancellationToken);

    private static async Task<IReadOnlyList<StartupItem>> EnumerateCoreAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        var items = new List<StartupItem>();
        try
        {
            items.AddRange(EnumerateRegistry());
        }
        catch
        {
            // Registry problems must not hide the rest.
        }

        try
        {
            items.AddRange(EnumerateStartupFolders());
        }
        catch
        {
        }

        try
        {
            items.AddRange(await EnumerateScheduledTasksAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        try
        {
            items.AddRange(EnumerateWinlogon());
        }
        catch
        {
        }

        return items;
    }

    /// <summary>Finds the registry source that owns a <see cref="StartupItem.Location"/>, or null for folder/task items.</summary>
    public static RegistryRunSource? FindRegistrySource(string? location) =>
        location is null ? null : RegistrySources.FirstOrDefault(s => s.DisplayPrefix.Equals(location, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True for a RunOnce location. Windows runs RunOnce values once at the next sign-in and deletes them; it does not
    /// consult StartupApproved for them, so such an entry cannot be disabled, only removed.
    /// </summary>
    public static bool IsRunOnceLocation(string? location) => FindRegistrySource(location)?.IsRunOnce == true;

    /// <summary>Why a RunOnce entry cannot be toggled; shown instead of flipping a flag Windows ignores.</summary>
    public const string RunOnceCannotBeDisabled = "RunOnce entries run once at the next sign-in and are then deleted by Windows; a disabled flag is ignored for them. Remove the entry instead if it should not run.";

    public static string UserStartupFolder => SafeFolder(Environment.SpecialFolder.Startup);

    public static string CommonStartupFolder => SafeFolder(Environment.SpecialFolder.CommonStartup);

    /// <summary>StartupApproved key for a folder item (per user or all users).</summary>
    public static (RegistryHive Hive, string SubKey) ApprovedKeyForFolder(string folderPath) =>
        folderPath.StartsWith(CommonStartupFolder, StringComparison.OrdinalIgnoreCase) && CommonStartupFolder.Length > 0
            ? (RegistryHive.LocalMachine, StartupApprovedSubKey + @"\StartupFolder")
            : (RegistryHive.CurrentUser, StartupApprovedSubKey + @"\StartupFolder");

    /// <summary>Company name from the file's version resource; null when it cannot be read.</summary>
    public static string? ReadCompanyName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            if (!File.Exists(executablePath))
            {
                return null;
            }

            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>File size in bytes, or null when the file is missing or unreadable.</summary>
    public static long? ProbeFileSize(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<StartupItem> EnumerateRegistry()
    {
        var items = new List<StartupItem>();
        foreach (var source in RegistrySources)
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(source.Hive, source.View);
                using var run = hive.OpenSubKey(source.SubKey, writable: false);
                if (run is null)
                {
                    continue;
                }

                // StartupApproved lives under the 64-bit view even for Run32 entries (that is where Task Manager writes it).
                using var approvedBase = RegistryKey.OpenBaseKey(source.Hive, RegistryView.Registry64);
                using var approved = approvedBase.OpenSubKey(source.ApprovedSubKey, writable: false);
                foreach (var name in run.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string command = run.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;
                    bool enabled = StartupApprovedCodec.IsEnabled(approved?.GetValue(name) as byte[]);
                    string? exe = StartupCommandParser.ExtractExecutable(command);
                    string publisher = StartupPublisherRules.Resolve(ReadCompanyName(exe), name, exe);
                    items.Add(new StartupItem(name, command, source.DisplayPrefix, enabled, publisher, StartupKinds.Registry));
                }
            }
            catch
            {
                // Skip a key we cannot read (permissions, missing hive).
            }
        }

        return items;
    }

    private static IEnumerable<StartupItem> EnumerateStartupFolders()
    {
        var items = new List<StartupItem>();
        foreach (var folder in new[] { UserStartupFolder, CommonStartupFolder })
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            var (hive, approvedSubKey) = ApprovedKeyForFolder(folder);
            using var approved = SafeOpen(hive, approvedSubKey);
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string command = ResolveShortcut(file) ?? file;
                bool enabled = StartupApprovedCodec.IsEnabled(approved?.GetValue(fileName) as byte[]);
                string? exe = StartupCommandParser.ExtractExecutable(command);
                string name = Path.GetFileNameWithoutExtension(fileName);
                string publisher = StartupPublisherRules.Resolve(ReadCompanyName(exe), name, exe);
                items.Add(new StartupItem(name, command, folder, enabled, publisher, StartupKinds.Folder));
            }
        }

        return items;
    }

    private sealed class TaskRow
    {
        public string? TaskName { get; set; }
        public string? TaskPath { get; set; }
        public string? State { get; set; }
        public string? Exec { get; set; }
        public string? Args { get; set; }
        public string? Author { get; set; }
    }

    private static async Task<IEnumerable<StartupItem>> EnumerateScheduledTasksAsync(CancellationToken cancellationToken)
    {
        const string script = """
            Get-ScheduledTask -ErrorAction SilentlyContinue |
              Where-Object { $_.Triggers | Where-Object { $_.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger' } } |
              Select-Object TaskName, TaskPath, @{n='State';e={$_.State.ToString()}}, @{n='Exec';e={($_.Actions | Select-Object -First 1).Execute}}, @{n='Args';e={($_.Actions | Select-Object -First 1).Arguments}}, @{n='Author';e={$_.Author}}
            """;
        var rows = await PowerShellRunner.RunJsonAsync<List<TaskRow>>(script, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return [];
        }

        var items = new List<StartupItem>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.TaskName))
            {
                continue;
            }

            string command = string.IsNullOrWhiteSpace(row.Exec) ? string.Empty : row.Exec.Trim();
            if (!string.IsNullOrWhiteSpace(row.Args))
            {
                command = (command.Contains(' ') && !command.StartsWith('"') ? $"\"{command}\"" : command) + " " + row.Args.Trim();
            }

            string? exe = StartupCommandParser.ExtractExecutable(command);
            bool enabled = !string.Equals(row.State, "Disabled", StringComparison.OrdinalIgnoreCase);
            string? author = string.IsNullOrWhiteSpace(row.Author) ? null : row.Author.Trim();
            string publisher = StartupPublisherRules.Resolve(ReadCompanyName(exe) ?? author, row.TaskName, exe);
            string location = TaskLocationPrefix + (row.TaskPath ?? "\\") + row.TaskName;
            items.Add(new StartupItem(row.TaskName, command, location, enabled, publisher, StartupKinds.Task));
        }

        return items;
    }

    private static IEnumerable<StartupItem> EnumerateWinlogon()
    {
        using var key = SafeOpen(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", RegistryView.Registry64);
        if (key is null)
        {
            return [];
        }

        string? shell = key.GetValue("Shell")?.ToString();
        string? userinit = key.GetValue("Userinit")?.ToString();
        return WinlogonRules.Check(shell, userinit);
    }

    /// <summary>Resolves a .lnk target through the Windows Script Host shell object (late bound); null when not possible.</summary>
    public static string? ResolveShortcut(string path)
    {
        if (!PlatformGuard.IsWindows || !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(path);
            string target = (string?)link.TargetPath ?? string.Empty;
            string args = (string?)link.Arguments ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            string quoted = target.Contains(' ') ? $"\"{target}\"" : target;
            return string.IsNullOrWhiteSpace(args) ? quoted : quoted + " " + args;
        }
        catch
        {
            return null;
        }
    }

    private static RegistryKey? SafeOpen(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            return baseKey.OpenSubKey(subKey, writable: false);
        }
        catch
        {
            return null;
        }
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch
        {
            return string.Empty;
        }
    }
}
