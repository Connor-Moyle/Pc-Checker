using System.Management;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Performance;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Startup;

/// <summary>
/// Windows implementation of <see cref="IStartupService"/>: StartupApproved flags and Run keys, Startup folder
/// shortcuts, scheduled tasks, service start modes (sc config) and boot configuration (bcdedit, HiberbootEnabled,
/// shutdown). Every change runs through <see cref="IChangeGuard"/>; every Windows call degrades to a failed
/// <see cref="OperationResult"/> instead of throwing.
/// </summary>
public sealed partial class WindowsStartupService : IStartupService, IRestartCanceller
{
    private const string Module = "Startup";
    private const string NotOnWindows = "This feature is only available on Windows.";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsStartupService> _logger;

    public WindowsStartupService(IChangeGuard guard, ILogger<WindowsStartupService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    /// <summary>Where removed shortcuts and registry values are kept so a removal can be undone by hand.</summary>
    public static string BackupDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcChecker", "backup", "startup");

    public int RestartDelaySeconds => RestartPrompt.CountdownSeconds;

    public Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken cancellationToken) =>
        StartupEnumerator.EnumerateAsync(cancellationToken);

    // ---------------------------------------------------------------- startup items

    public Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        if (item.Kind == StartupKinds.Winlogon)
        {
            return Task.FromResult(OperationResult.Fail("Winlogon values are shown for information only. Use a Microsoft Defender scan if you did not set this yourself."));
        }

        string verb = enabled ? "Enable" : "Disable";
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, item.Name, Reversible: true,
            UndoHint: $"Set '{item.Name}' back to {(enabled ? "disabled" : "enabled")} on the Startup page ({item.Location}).");

        return _guard.RunGuardedAsync($"{verb} startup item '{item.Name}'", options, ct => item.Kind switch
        {
            StartupKinds.Task => SetTaskEnabledAsync(item, enabled, ct),
            StartupKinds.Folder => Task.FromResult(SetFolderItemEnabled(item, enabled)),
            _ => Task.FromResult(SetRegistryItemEnabled(item, enabled)),
        }, null, cancellationToken);
    }

    public Task<OperationResult> RemoveStartupItemAsync(StartupItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        if (item.Kind == StartupKinds.Winlogon)
        {
            return Task.FromResult(OperationResult.Fail("Winlogon values cannot be removed from here. Reset them with a Microsoft Defender scan or by restoring the default value in the registry."));
        }

        var options = new ChangeOptions(null, null, Module, item.Name, Reversible: true,
            UndoHint: $"A copy was saved under {BackupDirectory}. Registry values: re-add on the Startup page (Add…). Shortcuts: move the file back to {item.Location}. Tasks: import the saved XML in Task Scheduler.");

        return _guard.RunGuardedAsync($"Remove startup item '{item.Name}'", options, ct => item.Kind switch
        {
            StartupKinds.Task => RemoveTaskAsync(item, ct),
            StartupKinds.Folder => Task.FromResult(RemoveFolderItem(item)),
            _ => Task.FromResult(RemoveRegistryItem(item)),
        }, null, cancellationToken);
    }

    public Task<OperationResult> AddStartupItemAsync(string name, string command, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command))
        {
            return Task.FromResult(OperationResult.Fail("Give the startup entry a name and the program to run."));
        }

        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, name.Trim(), Reversible: true,
            UndoHint: $"Remove '{name.Trim()}' on the Startup page (it lives in HKCU\\...\\Run).");

        return _guard.RunGuardedAsync($"Add startup item '{name.Trim()}'", options, _ =>
        {
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(StartupEnumerator.RunSubKey, writable: true);
                if (run is null)
                {
                    return Task.FromResult(OperationResult.Fail("The HKCU Run key could not be opened."));
                }

                run.SetValue(name.Trim(), command.Trim(), RegistryValueKind.String);
                ClearApprovedFlag(RegistryHive.CurrentUser, StartupEnumerator.StartupApprovedSubKey + @"\Run", name.Trim());
                return Task.FromResult(OperationResult.Ok($"{name.Trim()} will start when you sign in."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(OperationResult.Fail($"Could not add the entry: {ex.Message}", ex));
            }
        }, null, cancellationToken);
    }

    private static OperationResult SetRegistryItemEnabled(StartupItem item, bool enabled)
    {
        var source = StartupEnumerator.FindRegistrySource(item.Location);
        if (source is null)
        {
            return OperationResult.Fail($"Unknown registry location: {item.Location}");
        }

        if (source.IsRunOnce)
        {
            return OperationResult.Fail($"{item.Name} cannot be {(enabled ? "enabled" : "disabled")}: {StartupEnumerator.RunOnceCannotBeDisabled}");
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(source.Hive, RegistryView.Registry64);
            using var approved = baseKey.CreateSubKey(source.ApprovedSubKey, writable: true);
            if (approved is null)
            {
                return OperationResult.Fail("The StartupApproved key could not be opened.");
            }

            approved.SetValue(item.Name, StartupApprovedCodec.Encode(enabled), RegistryValueKind.Binary);
            return OperationResult.Ok(enabled ? $"{item.Name} will start with Windows again." : $"{item.Name} will no longer start with Windows.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not update {item.Name}: {ex.Message}", ex);
        }
    }

    private static OperationResult SetFolderItemEnabled(StartupItem item, bool enabled)
    {
        string? file = FindFolderFile(item);
        if (file is null)
        {
            return OperationResult.Fail($"The shortcut for {item.Name} was not found in {item.Location}.");
        }

        var (hive, subKey) = StartupEnumerator.ApprovedKeyForFolder(item.Location);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var approved = baseKey.CreateSubKey(subKey, writable: true);
            if (approved is null)
            {
                return OperationResult.Fail("The StartupApproved\\StartupFolder key could not be opened.");
            }

            approved.SetValue(Path.GetFileName(file), StartupApprovedCodec.Encode(enabled), RegistryValueKind.Binary);
            return OperationResult.Ok(enabled ? $"{item.Name} will start with Windows again." : $"{item.Name} will no longer start with Windows.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not update {item.Name}: {ex.Message}", ex);
        }
    }

    private static async Task<OperationResult> SetTaskEnabledAsync(StartupItem item, bool enabled, CancellationToken ct)
    {
        var (path, name) = SplitTask(item);
        string cmdlet = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        var result = await PowerShellRunner.RunAsync($"{cmdlet} -TaskName {PowerShellRunner.Quote(name)} -TaskPath {PowerShellRunner.Quote(path)} -ErrorAction Stop | Out-Null", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return result.Success
            ? OperationResult.Ok(enabled ? $"Task {name} is enabled again." : $"Task {name} will no longer run at logon.")
            : OperationResult.Fail($"Task Scheduler refused: {FirstLine(result.StdErr, result.StdOut)}");
    }

    private OperationResult RemoveRegistryItem(StartupItem item)
    {
        var source = StartupEnumerator.FindRegistrySource(item.Location);
        if (source is null)
        {
            return OperationResult.Fail($"Unknown registry location: {item.Location}");
        }

        try
        {
            SaveBackup(item);
            using var hive = RegistryKey.OpenBaseKey(source.Hive, source.View);
            using var run = hive.OpenSubKey(source.SubKey, writable: true);
            run?.DeleteValue(item.Name, throwOnMissingValue: false);
            ClearApprovedFlag(source.Hive, source.ApprovedSubKey, item.Name);
            return OperationResult.Ok($"{item.Name} was removed from startup. A copy of the entry is in {BackupDirectory}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not remove {item.Name}: {ex.Message}", ex);
        }
    }

    private static OperationResult RemoveFolderItem(StartupItem item)
    {
        string? file = FindFolderFile(item);
        if (file is null)
        {
            return OperationResult.Fail($"The shortcut for {item.Name} was not found in {item.Location}.");
        }

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            string target = Path.Combine(BackupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(file)}");
            File.Move(file, target, overwrite: true);
            var (hive, subKey) = StartupEnumerator.ApprovedKeyForFolder(item.Location);
            ClearApprovedFlag(hive, subKey, Path.GetFileName(file));
            return OperationResult.Ok($"{item.Name} was moved out of the Startup folder to {target}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not move the shortcut: {ex.Message}", ex);
        }
    }

    private async Task<OperationResult> RemoveTaskAsync(StartupItem item, CancellationToken ct)
    {
        var (path, name) = SplitTask(item);
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            string xmlPath = Path.Combine(BackupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-task-{Sanitize(name)}.xml");
            var export = await PowerShellRunner.RunAsync(
                $"Export-ScheduledTask -TaskName {PowerShellRunner.Quote(name)} -TaskPath {PowerShellRunner.Quote(path)} -ErrorAction Stop | Set-Content -Path {PowerShellRunner.Quote(xmlPath)} -Encoding Unicode",
                TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!export.Success)
            {
                _logger.LogWarning("Could not export task {Task} before removing it: {Error}", name, export.StdErr);
            }

            var result = await PowerShellRunner.RunAsync(
                $"Unregister-ScheduledTask -TaskName {PowerShellRunner.Quote(name)} -TaskPath {PowerShellRunner.Quote(path)} -Confirm:$false -ErrorAction Stop",
                TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            return result.Success
                ? OperationResult.Ok($"Task {name} was removed." + (export.Success ? $" A copy was saved to {xmlPath}." : string.Empty))
                : OperationResult.Fail($"Task Scheduler refused: {FirstLine(result.StdErr, result.StdOut)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not remove the task: {ex.Message}", ex);
        }
    }

    private static void ClearApprovedFlag(RegistryHive hive, string approvedSubKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var approved = baseKey.OpenSubKey(approvedSubKey, writable: true);
            approved?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch
        {
            // The flag is cosmetic once the entry is gone.
        }
    }

    private static void SaveBackup(StartupItem item)
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            string path = Path.Combine(BackupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(item.Name)}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A missing backup should not block the removal the user asked for.
        }
    }

    private static string? FindFolderFile(StartupItem item)
    {
        try
        {
            if (!Directory.Exists(item.Location))
            {
                return null;
            }

            return Directory.EnumerateFiles(item.Location)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(item.Name, StringComparison.OrdinalIgnoreCase)
                                     || Path.GetFileName(f).Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static (string Path, string Name) SplitTask(StartupItem item)
    {
        string full = item.Location.StartsWith(StartupEnumerator.TaskLocationPrefix, StringComparison.Ordinal)
            ? item.Location[StartupEnumerator.TaskLocationPrefix.Length..]
            : item.Location;
        int slash = full.LastIndexOf('\\');
        if (slash < 0)
        {
            return ("\\", item.Name);
        }

        string path = full[..(slash + 1)];
        string name = full[(slash + 1)..];
        return (path.Length == 0 ? "\\" : path, name.Length == 0 ? item.Name : name);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    private static string FirstLine(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var line = c.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return "no details were returned";
    }

    // ---------------------------------------------------------------- services

    public Task<IReadOnlyList<ServiceInfo>> GetAutoStartServicesAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<IReadOnlyList<ServiceInfo>>([]);
        }

        return Task.Run(() =>
        {
            var list = new List<ServiceInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, StartMode, State, PathName, Description, DelayedAutoStart FROM Win32_Service");
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? name = o["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    bool delayed = o["DelayedAutoStart"] is bool b && b;
                    string mode = ServiceStartModes.Normalize(o["StartMode"]?.ToString(), delayed) ?? o["StartMode"]?.ToString() ?? "Unknown";
                    if (!ServiceStartModes.IsAutomatic(mode))
                    {
                        continue;
                    }

                    string? path = o["PathName"]?.ToString();
                    bool microsoft = path is not null && path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase);
                    list.Add(new ServiceInfo(name, o["DisplayName"]?.ToString() ?? name, mode, o["State"]?.ToString() ?? "Unknown", path, microsoft, o["Description"]?.ToString()));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not list services through WMI.");
            }

            return (IReadOnlyList<ServiceInfo>)list.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }, cancellationToken);
    }

    public Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        var (allowed, message) = ProtectedServices.CanSetStartMode(serviceName, startMode);
        if (!allowed)
        {
            return Task.FromResult(OperationResult.Fail(message ?? "Not allowed."));
        }

        string? arg = ServiceStartModes.ToScArgument(startMode);
        string canonical = ServiceStartModes.Normalize(startMode)!;
        var options = new ChangeOptions(null, null, Module, serviceName, Reversible: true,
            UndoHint: $"Set the start mode of '{serviceName}' back on the Startup page (advanced) or run: sc config \"{serviceName}\" start= <auto|demand|disabled>.");

        return _guard.RunGuardedAsync($"Set service '{serviceName}' start mode to {canonical}", options, async ct =>
        {
            var result = await CommandRunner.RunAsync("sc.exe", $"config \"{serviceName}\" start= {arg}", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return OperationResult.Fail($"sc config failed ({result.ExitCode}): {result.FirstLine}");
            }

            string note = message is null ? string.Empty : " " + message;
            return OperationResult.Ok($"{serviceName} is now set to {canonical}.{note}");
        }, null, cancellationToken);
    }
}
