using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Restore;

namespace PcChecker.Core.ContextMenus;

/// <summary>Turning entries off and on, the classic menu switch and restarting File Explorer.</summary>
public sealed partial class WindowsContextMenuService
{
    private const string LegacyDisable = "LegacyDisable";

    public async Task<OperationResult> SetEnabledAsync(MenuEntry entry, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("The right-click menu can only be changed on Windows.");
        }

        // The page also checks Advanced mode for Microsoft apps; Windows' own entries are refused here whatever asks.
        if (ContextMenuRules.WhyLocked(entry, advancedMode: true) is { } locked)
        {
            return OperationResult.Fail(locked);
        }

        if (entry.IsEnabled == enabled)
        {
            return OperationResult.Ok(enabled ? $"\"{entry.Text}\" is already turned on." : $"\"{entry.Text}\" is already turned off.");
        }

        string description = (enabled ? "Turn on" : "Turn off") + $" \"{entry.Text}\" in the right-click menu";
        var options = new ChangeOptions(
            CreateBefore: entry.Kind == MenuEntryKind.SendTo ? false : null,
            CreateAfter: null,
            Module: Module,
            Target: Target(entry),
            Reversible: true,
            UndoHint: UndoHint(entry, enabled));

        var result = await _guard.RunGuardedAsync(description, options,
            ct => Task.Run(() => Apply(entry, enabled), ct), null, cancellationToken).ConfigureAwait(false);
        // Everything but a command verb with HKLM keys changes only the current user (HKCU, the Send to folder).
        bool perUser = entry.Kind != MenuEntryKind.Command || entry.Keys.All(k => k.Scope == MenuEntryScope.User);
        return perUser ? ElevatedUserScope.Annotate(result) : result;
    }

    public Task<OperationResult> SetClassicMenuAsync(bool classic, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows || !ClassicMenuSwitch.IsSupported(Environment.OSVersion.Version.Build))
        {
            return Task.FromResult(OperationResult.Fail("This switch is only for Windows 11. Earlier versions of Windows always show the full menu."));
        }

        string description = classic ? "Show the full (classic) right-click menu" : "Show the Windows 11 compact right-click menu";
        var options = new ChangeOptions(null, null, Module, "HKCU\\" + ClassicMenuSwitch.InprocPath, true,
            classic
                ? "Switch it off on Clean up → Right-click menu, or delete HKCU\\" + ClassicMenuSwitch.KeyPath + " and restart File Explorer."
                : "Switch it on again on Clean up → Right-click menu.");
        return _guard.RunGuardedAsync(description, options, ct => Task.Run(() =>
        {
            using var existing = Open(MenuEntryScope.User, ClassicMenuSwitch.InprocPath);
            string? value = existing?.GetValue(string.Empty) as string;
            if (ClassicMenuSwitch.IsForeignRegistration(existing is not null, value))
            {
                return OperationResult.Fail("Another program registered this key, so PC Checker leaves it alone.");
            }

            if (classic)
            {
                using var key = Registry.CurrentUser.CreateSubKey(ClassicMenuSwitch.InprocPath, writable: true);
                key.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
                return ElevatedUserScope.Annotate(OperationResult.Ok("The full right-click menu is on. Restart File Explorer to see it."));
            }

            Registry.CurrentUser.DeleteSubKeyTree(ClassicMenuSwitch.KeyPath, throwOnMissingSubKey: false);
            return ElevatedUserScope.Annotate(OperationResult.Ok("The Windows 11 compact menu is back. Restart File Explorer to see it."));
        }, ct), null, cancellationToken);
    }

    public async Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Restarting File Explorer is only possible on Windows.");
        }

        var result = await PowerShellRunner.RunAsync(WindowsDebloatService.RestartExplorerScript, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("File Explorer restart requested from the right-click menu page (exit code {Code}).", result.ExitCode);
        return result.Success
            ? OperationResult.Ok("File Explorer restarted. The right-click menu now shows your changes.")
            : OperationResult.Fail("File Explorer could not be restarted. Sign out and back in to see the changes.");
    }

    private OperationResult Apply(MenuEntry entry, bool enabled) => entry.Kind switch
    {
        MenuEntryKind.Command => ApplyVerb(entry, enabled),
        MenuEntryKind.SendTo => ApplySendTo(entry, enabled),
        _ => ApplyBlockList(entry, enabled),
    };

    private static OperationResult ApplyVerb(MenuEntry entry, bool enabled)
    {
        var changed = new List<string>();
        var failed = new List<string>();
        foreach (var key in entry.Keys)
        {
            using var verb = Open(key.Scope, key.Path, writable: true);
            if (verb is null)
            {
                failed.Add(key.DisplayPath);
                continue;
            }

            bool has = verb.GetValueNames().Any(n => n.Equals(LegacyDisable, StringComparison.OrdinalIgnoreCase));
            if (!enabled && !has)
            {
                verb.SetValue(LegacyDisable, string.Empty, RegistryValueKind.String);
                changed.Add(key.DisplayPath);
            }
            else if (enabled && has)
            {
                verb.DeleteValue(LegacyDisable, throwOnMissingValue: false);
                changed.Add(key.DisplayPath);
            }
        }

        if (changed.Count == 0)
        {
            return OperationResult.Fail(failed.Count > 0
                ? "Windows did not allow the change (" + string.Join(", ", failed) + ")."
                : "Nothing needed changing.");
        }

        string done = enabled
            ? $"\"{entry.Text}\" is back in the right-click menu."
            : $"\"{entry.Text}\" is turned off. Nothing was deleted; turn it back on here at any time.";
        string partial = failed.Count > 0 ? " Some keys could not be changed: " + string.Join(", ", failed) + "." : string.Empty;
        return OperationResult.Ok($"{done} Changed: {string.Join(", ", changed)}.{partial} Restart File Explorer to see it.");
    }

    private static OperationResult ApplyBlockList(MenuEntry entry, bool enabled)
    {
        string clsid = entry.Clsid!;
        var user = ValueNames(MenuEntryScope.User, ShellExtensionBlockList.KeyPath);
        var machine = ValueNames(MenuEntryScope.Machine, ShellExtensionBlockList.KeyPath);
        var plan = ShellExtensionBlockList.Plan(clsid, enabled, user, machine);
        switch (plan.Action)
        {
            case BlockListAction.AddForUser:
            {
                using var key = Registry.CurrentUser.CreateSubKey(ShellExtensionBlockList.KeyPath, writable: true);
                key.SetValue(clsid, ShellExtensionBlockList.ValueData, RegistryValueKind.String);
                return OperationResult.Ok($"\"{entry.Text}\" is turned off for you (added {clsid} to HKCU\\{ShellExtensionBlockList.KeyPath}). Nothing was deleted. Restart File Explorer to see it.");
            }

            case BlockListAction.Remove:
            {
                var removed = new List<string>();
                var failed = new List<string>();
                if (plan.InUserList && ShellExtensionBlockList.MatchingName(user, clsid) is { } userName)
                {
                    using var key = Open(MenuEntryScope.User, ShellExtensionBlockList.KeyPath, writable: true);
                    (key is null ? failed : removed).Add("HKCU");
                    key?.DeleteValue(userName, throwOnMissingValue: false);
                }

                if (plan.InMachineList && ShellExtensionBlockList.MatchingName(machine, clsid) is { } machineName)
                {
                    using var key = Open(MenuEntryScope.Machine, ShellExtensionBlockList.KeyPath, writable: true);
                    (key is null ? failed : removed).Add("HKLM");
                    key?.DeleteValue(machineName, throwOnMissingValue: false);
                }

                if (failed.Count > 0)
                {
                    string done = removed.Count > 0 ? $" It was taken off the list in {string.Join(" and ", removed)} only." : string.Empty;
                    string admin = failed.Contains("HKLM") ? " Blocking for every user can only be undone with administrator rights." : string.Empty;
                    return OperationResult.Fail($"\"{entry.Text}\" is still turned off: Windows did not allow the change in {string.Join(" and ", failed)}.{done}{admin}");
                }

                if (removed.Count == 0)
                {
                    return OperationResult.Fail($"\"{entry.Text}\" was not found on the Blocked list, so nothing was changed.");
                }

                return OperationResult.Ok($"\"{entry.Text}\" is back in the right-click menu (removed from the Blocked list in {string.Join(" and ", removed)}). Restart File Explorer to see it.");
            }

            default:
                return OperationResult.Ok(enabled ? $"\"{entry.Text}\" is already turned on." : $"\"{entry.Text}\" is already turned off.");
        }
    }

    private OperationResult ApplySendTo(MenuEntry entry, bool enabled)
    {
        string backup = SendToBackupFolder;
        if (!enabled)
        {
            string source = Path.Combine(SendToFolder, entry.KeyName);
            if (!File.Exists(source))
            {
                return OperationResult.Fail($"{source} is no longer there.");
            }

            var folder = Directory.CreateDirectory(backup);
            folder.Attributes |= FileAttributes.Hidden;
            string target = Path.Combine(backup, SendToRules.FreeName(entry.KeyName, n => File.Exists(Path.Combine(backup, n))));
            File.Move(source, target);
            return OperationResult.Ok($"\"{entry.Text}\" is no longer in Send to. The shortcut was moved to {target}; turn it back on here at any time.");
        }

        string from = entry.BackupPath ?? Path.Combine(backup, entry.KeyName);
        if (!File.Exists(from))
        {
            return OperationResult.Fail($"The saved copy {from} is no longer there.");
        }

        Directory.CreateDirectory(SendToFolder);
        string to = Path.Combine(SendToFolder, SendToRules.FreeName(Path.GetFileName(from), n => File.Exists(Path.Combine(SendToFolder, n))));
        File.Move(from, to);
        return OperationResult.Ok($"\"{entry.Text}\" is back in Send to.");
    }

    private string Target(MenuEntry entry) => entry.Kind switch
    {
        MenuEntryKind.Command => string.Join("; ", entry.Keys.Select(k => k.DisplayPath)),
        MenuEntryKind.SendTo => entry.BackupPath ?? Path.Combine(SendToFolder, entry.KeyName),
        _ => $"{entry.Text} {entry.Clsid}",
    };

    private string UndoHint(MenuEntry entry, bool enabled)
    {
        string page = "Turn it " + (enabled ? "off" : "back on") + " on Clean up → Right-click menu";
        return entry.Kind switch
        {
            MenuEntryKind.Command => enabled
                ? $"{page}. Before: a LegacyDisable value in {string.Join(", ", entry.Keys.Select(k => k.DisplayPath))}."
                : $"{page}, or delete the LegacyDisable value PC Checker added in {string.Join(", ", entry.Keys.Select(k => k.DisplayPath))}.",
            MenuEntryKind.SendTo => enabled
                ? $"{page}. Before: the file was kept in {SendToBackupFolder}."
                : $"{page}, or move {entry.KeyName} from {SendToBackupFolder} back to {SendToFolder}.",
            _ => enabled
                ? $"{page}. Before: {entry.Clsid} was in the Blocked list" + (entry.BlockedForAllUsers ? " for every user (HKLM)." : " (HKCU).")
                : $"{page}, or delete the value {entry.Clsid} from HKCU\\{ShellExtensionBlockList.KeyPath}.",
        };
    }
}
