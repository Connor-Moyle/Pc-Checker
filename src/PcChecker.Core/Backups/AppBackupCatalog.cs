using PcChecker.Core.ContextMenus;
using PcChecker.Core.Debloat;
using PcChecker.Core.Drivers;
using PcChecker.Core.RegistryHealth;
using PcChecker.Core.ScheduledTasks;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Backups;

/// <summary>Where each feature keeps its backups. The paths come from the features themselves; nothing new is invented here.</summary>
public sealed record AppBackupFolders(
    string SetupBackups,
    string RegistryBackups,
    string DriverBackups,
    string DebloatCaptureFile,
    string TaskBackups,
    string HostsBackups,
    string SendToBackups,
    string StartupBackups)
{
    /// <summary>The folders every feature uses on this PC, from the features' own path helpers.</summary>
    public static AppBackupFolders ForCurrentUser(string setupBackupDirectory, string settingsDirectory, string driverBackupRoot, string hostsBackupFolder) =>
        new(
            setupBackupDirectory,
            RegistryBackupCatalog.DefaultDirectory(Environment.GetEnvironmentVariable),
            driverBackupRoot,
            DebloatCaptureBook.DefaultPath(settingsDirectory),
            TaskBackupNaming.DefaultDirectory(Environment.GetEnvironmentVariable),
            hostsBackupFolder,
            WindowsContextMenuService.SendToBackupFolder,
            WindowsStartupService.BackupDirectory);
}

/// <summary>
/// The list of PC Checker's own backup places shown on the Backups page, in a fixed order, with what may be deleted.
/// Two places are shown but never offered for deletion: the debloat undo values (needed to undo tweaks) and the Send to
/// entries you turned off (they are the entries themselves, not copies).
/// </summary>
public static class AppBackupCatalog
{
    public const string SetupId = "setup";
    public const string RegistryId = "registry";
    public const string DriversId = "drivers";
    public const string DebloatId = "debloat";
    public const string TasksId = "tasks";
    public const string HostsId = "hosts";
    public const string SendToId = "sendto";
    public const string StartupId = "startup";

    public static IReadOnlyList<AppBackupLocation> Build(AppBackupFolders folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return
        [
            new(SetupId, "Setup backups", "Saved automatically before every setup import or reset in Settings.",
                folders.SetupBackups, "setup-*.pcchecker", BackupItemKind.Files, true, null, "settings"),
            new(RegistryId, "Registry backups", "Saved before the Registry page removes entries; restore them from that page.",
                folders.RegistryBackups, RegistryBackupCatalog.FilePrefix + "*" + RegistryBackupCatalog.RegExtension, BackupItemKind.Files, true, null, "registry")
            {
                CompanionExtensions = [RegistryBackupCatalog.ManifestExtension],
            },
            new(DriversId, "Driver backups", "A copy of every driver package the Old drivers page removed, one folder per removal.",
                folders.DriverBackups, "*", BackupItemKind.Folders, true, null, "old-drivers"),
            new(TasksId, "Scheduled task backups", "Deleted scheduled tasks, so they can be put back from the Scheduled tasks page.",
                folders.TaskBackups, TaskBackupNaming.Prefix + "*" + TaskBackupNaming.Extension, BackupItemKind.Files, true, null, "scheduled-tasks"),
            new(HostsId, "Hosts file backups", "The hosts file as it was before each save on the Hosts file page.",
                folders.HostsBackups, "hosts-*.bak", BackupItemKind.Files, true, null, "hosts"),
            new(StartupId, "Startup entry copies", "A copy of every startup entry removed on the Startup page.",
                folders.StartupBackups, "*", BackupItemKind.Files, true, null, "startup"),
            new(DebloatId, "Debloat undo values", "The settings as they were before each debloat tweak, so every tweak can be undone.",
                folders.DebloatCaptureFile, Path.GetFileName(folders.DebloatCaptureFile), BackupItemKind.SingleFile, false,
                "Needed to undo debloat tweaks, so it is never deleted here.", "debloat"),
            new(SendToId, "Turned-off Send to entries", "Send to menu entries you turned off on the Right-click menu page.",
                folders.SendToBackups, "*", BackupItemKind.Files, false,
                "These are the entries themselves; turn them back on from the Right-click menu page.", "context-menu"),
        ];
    }

    /// <summary>The smallest number of backups "Delete old ones" keeps: the newest always stays.</summary>
    public const int MinimumKept = 1;

    /// <summary>The number kept when the user does not choose.</summary>
    public const int DefaultKept = 5;

    /// <summary>
    /// The backups to delete so that the newest <paramref name="keepNewest"/> stay (at least <see cref="MinimumKept"/>),
    /// oldest first. Ties in time are broken by path so the choice never changes between calls.
    /// </summary>
    public static IReadOnlyList<AppBackupItem> SelectToDelete(IEnumerable<AppBackupItem> items, int keepNewest)
    {
        int keep = Math.Max(MinimumKept, keepNewest);
        var ordered = (items ?? [])
            .OrderByDescending(i => i.Created)
            .ThenByDescending(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ordered.Count <= keep ? [] : ordered.Skip(keep).Reverse().ToList();
    }

    /// <summary>How much space deleting <see cref="SelectToDelete"/>'s choice frees.</summary>
    public static long BytesFreed(IEnumerable<AppBackupItem> items, int keepNewest) => SelectToDelete(items, keepNewest).Sum(i => i.SizeBytes);
}
