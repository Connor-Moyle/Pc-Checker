using PcChecker.Core.Common;

namespace PcChecker.Core.RegistryHealth;

/// <summary>
/// Demo registry checker for the sample gaming desktop: a modest, believable set of leftovers (uninstalled launchers,
/// old App Paths, MUI cache entries…), simulated progress, and in-memory backups that "fix" and "restore" for real
/// within the session. Pure .NET.
/// </summary>
public sealed class DemoRegistryHealthService : IRegistryHealthService
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string MuiCache = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string User = @"C:\Users\Connor";
    private const string Pf = @"C:\Program Files";
    private const string Pf86 = @"C:\Program Files (x86)";

    private static readonly Dictionary<RegistryIssueCategory, int> TypicalKeys = new()
    {
        [RegistryIssueCategory.UninstallEntries] = 412,
        [RegistryIssueCategory.AppPaths] = 96,
        [RegistryIssueCategory.SharedDlls] = 38,
        [RegistryIssueCategory.FileAssociations] = 9_870,
        [RegistryIssueCategory.StartupEntries] = 6,
        [RegistryIssueCategory.ComObjects] = 14_220,
        [RegistryIssueCategory.MuiCache] = 1,
        [RegistryIssueCategory.Fonts] = 2,
        [RegistryIssueCategory.HelpFiles] = 4,
        [RegistryIssueCategory.InstallerFolders] = 1,
        [RegistryIssueCategory.ServicesMissingImage] = 742,
        [RegistryIssueCategory.ContextMenuHandlers] = 64,
    };

    private readonly object _lock = new();
    private readonly HashSet<string> _fixedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _backupContents = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RegistryBackup> _backups;

    public DemoRegistryHealthService()
    {
        var now = DateTimeOffset.Now;
        _backups =
        [
            Backup(now.AddDays(-12).AddHours(-3), 4, "2 MUI cache entries, 1 App Paths entry and 1 startup entry"),
            Backup(now.AddDays(-104).AddHours(-7), 9, "5 uninstall entries, 3 file associations and 1 font entry"),
        ];
    }

    public IReadOnlyList<RegistryIssueCategory> Categories { get; } = Enum.GetValues<RegistryIssueCategory>();

    public async Task<RegistryScanResult> ScanAsync(IEnumerable<RegistryIssueCategory> categories, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var wanted = (categories ?? Categories).Distinct().ToList();
        if (wanted.Count == 0)
        {
            wanted = Categories.ToList();
        }

        var random = DemoData.CreateRandom();
        var found = new List<RegistryIssue>();
        int keys = 0;
        var all = SampleIssues();
        for (int i = 0; i < wanted.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = wanted[i];
            progress?.Report(new ProgressUpdate(RegistryIssueRules.Info(category).Name, 100.0 * i / wanted.Count, $"{found.Count} found so far"));
            await Task.Delay(random.Next(60, 160), cancellationToken).ConfigureAwait(false);
            keys += TypicalKeys.GetValueOrDefault(category);
            lock (_lock)
            {
                found.AddRange(all.Where(x => x.Category == category && !_fixedIds.Contains(x.Id)));
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var warnings = new List<string>();
        if (wanted.Contains(RegistryIssueCategory.ComObjects))
        {
            warnings.Add("Orphaned COM components: Windows did not allow reading 3 keys; they were skipped.");
        }

        return new RegistryScanResult(found, RegistryIssueRules.CountByCategory(found, wanted), keys,
            TimeSpan.FromMilliseconds(1_800 + 90 * wanted.Count), warnings, DateTimeOffset.Now);
    }

    public async Task<RegistryFixResult> FixAsync(IReadOnlyList<RegistryIssue> issues, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var fixable = issues.Where(i => i.SafeToFix).DistinctBy(i => i.Id).ToList();
        int skipped = issues.Count - fixable.Count;
        var messages = issues.Where(i => !i.SafeToFix).Select(i => $"Left alone (report only): {i.FullKeyPath}").ToList();
        if (fixable.Count == 0)
        {
            return new RegistryFixResult(0, skipped, 0, null, ["Nothing was changed.", .. messages]);
        }

        progress?.Report(new ProgressUpdate("Creating a restore point"));
        await DemoData.DelayAsync(cancellationToken, 300, 600).ConfigureAwait(false);
        for (int i = 0; i < fixable.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProgressUpdate(i < fixable.Count / 2 ? "Backing up" : "Removing", 100.0 * i / fixable.Count, fixable[i].FullKeyPath));
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        RegistryBackup backup;
        lock (_lock)
        {
            backup = Backup(DateTimeOffset.Now, fixable.Count, RegistryIssueRules.Summarize(fixable), _backups.Select(b => b.Id));
            _fixedIds.UnionWith(fixable.Select(f => f.Id));
            _backupContents[backup.Id] = fixable.Select(f => f.Id).ToList();
            _backups.Insert(0, backup);
        }

        var result = new RegistryFixResult(fixable.Count, skipped, 0, backup.Id, messages);
        return result with { Messages = [RegistryIssueRules.DescribeFix(result), .. messages] };
    }

    public async Task<IReadOnlyList<RegistryBackup>> ListBackupsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 150).ConfigureAwait(false);
        lock (_lock)
        {
            return _backups.OrderByDescending(b => b.Created).ToList();
        }
    }

    public async Task<OperationResult> RestoreBackupAsync(string backupId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 600).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_backups.Any(b => b.Id == backupId))
            {
                return OperationResult.Fail("The backup file no longer exists.");
            }

            if (_backupContents.TryGetValue(backupId, out var ids))
            {
                _fixedIds.ExceptWith(ids);
            }
        }

        return OperationResult.Ok("The backed-up entries are back in the registry. Scan again to see them.");
    }

    public async Task<OperationResult> DeleteBackupAsync(string backupId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 150).ConfigureAwait(false);
        lock (_lock)
        {
            int removed = _backups.RemoveAll(b => b.Id == backupId);
            _backupContents.Remove(backupId);
            return removed > 0 ? OperationResult.Ok("Backup deleted.") : OperationResult.Fail("The backup file no longer exists.");
        }
    }

    private static RegistryBackup Backup(DateTimeOffset created, int count, string summary, IEnumerable<string>? existingIds = null)
    {
        // Ids only go down to the second; add "-2", "-3"... like the Windows service so two fixes in one second never collide.
        var taken = new HashSet<string>(existingIds ?? [], StringComparer.OrdinalIgnoreCase);
        var id = RegistryBackupCatalog.NewId(created);
        for (int n = 2; taken.Contains(id); n++)
        {
            id = RegistryBackupCatalog.NewId(created) + "-" + n;
        }

        return new RegistryBackup(id, created, $@"{User}\AppData\Local\PcChecker\registry-backups\{id}.reg", count, summary);
    }

    /// <summary>The sample machine's leftovers (23 in the standard categories, a few more in advanced ones).</summary>
    private static List<RegistryIssue> SampleIssues()
    {
        static string Q(string s) => "\u201C" + s + "\u201D";
        static RegistryIssue Leftover(string hive, string key, string name, string uninstaller) =>
            RegistryIssueRules.Create(RegistryIssueCategory.UninstallEntries, hive, key, null,
                $"{Q(name)} is still listed in Installed apps, but its uninstaller and install folder are gone.", uninstaller);
        static RegistryIssue AppPath(string hive, string root, string exe, string target) =>
            RegistryIssueRules.Create(RegistryIssueCategory.AppPaths, hive, root + "\\" + exe, null,
                $"{Q(exe)} can still be started from Run (Win+R), but it points at a program that is no longer installed.", target);
        static RegistryIssue Mui(string target, string suffix) =>
            RegistryIssueRules.Create(RegistryIssueCategory.MuiCache, "HKCU", MuiCache, target + suffix,
                $"Windows still remembers the name of {RegistryPathTools.FileName(target)}, which is no longer on this PC.", target);
        static RegistryIssue Startup(string hive, string key, string name, string target) =>
            RegistryIssueRules.Create(RegistryIssueCategory.StartupEntries, hive, key, name,
                $"{Q(name)} is set to start every time you sign in, but {RegistryPathTools.FileName(target)} no longer exists.", target);
        static RegistryIssue Service(string name, string display, string image) =>
            RegistryIssueRules.Create(RegistryIssueCategory.ServicesMissingImage, "HKLM", @"SYSTEM\CurrentControlSet\Services\" + name, null,
                $"The service {Q(display)} ({name}) points at {RegistryPathTools.FileName(image)}, which no longer exists. " +
                "Uninstall the program it belonged to, or use that vendor's removal tool; PC Checker does not edit services.", image);

        return
        [
            Leftover("HKLM", UninstallWow + @"\OldGameLauncher", "Old Game Launcher", Pf86 + @"\Old Game Launcher\Uninstall.exe"),
            Leftover("HKLM", UninstallWow + @"\Afterburner", "MSI Afterburner 4.6.4", Pf86 + @"\MSI Afterburner\uninstall.exe"),
            Leftover("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiscordPTB", "Discord PTB", User + @"\AppData\Local\DiscordPTB\Update.exe"),
            Leftover("HKLM", UninstallWow + @"\Uplay", "Ubisoft Connect", Pf86 + @"\Ubisoft\Ubisoft Game Launcher\Uninstall.exe"),
            Leftover("HKLM", Uninstall + @"\WinRAR archiver", "WinRAR 6.11 (64-bit)", Pf + @"\WinRAR\Uninstall.exe"),
            Leftover("HKLM", UninstallWow + @"\RGBFusion2", "RGB Fusion 2.0", Pf86 + @"\GIGABYTE\RGBFusion\unins000.exe"),

            AppPath("HKLM", AppPaths, "Photoshop.exe", Pf + @"\Adobe\Adobe Photoshop 2023\Photoshop.exe"),
            AppPath("HKLM", AppPaths, "Audacity.exe", Pf + @"\Audacity\Audacity.exe"),
            AppPath("HKLM", AppPaths, "notepad++.exe", Pf + @"\Notepad++\notepad++.exe"),
            AppPath("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths", "Spotify.exe", User + @"\AppData\Roaming\Spotify\Spotify.exe"),

            Mui(User + @"\Downloads\GeForce_Experience_v3.27.0.112.exe", ".FriendlyAppName"),
            Mui(User + @"\Downloads\RiotClientInstall.exe", ".FriendlyAppName"),
            Mui(Pf86 + @"\Old Game Launcher\Launcher.exe", ".FriendlyAppName"),
            Mui(@"D:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", ".FriendlyAppName"),
            Mui(User + @"\AppData\Local\Temp\7zS4A1B\setup.exe", ".ApplicationCompany"),

            Startup("HKCU", Run, "Old Game Launcher", Pf86 + @"\Old Game Launcher\Launcher.exe"),
            Startup("HKCU", Run, "Discord PTB", User + @"\AppData\Local\DiscordPTB\Update.exe"),
            Startup("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "GIGABYTE RGB Fusion", Pf86 + @"\GIGABYTE\RGBFusion\RGBFusion.exe"),

            RegistryIssueRules.Create(RegistryIssueCategory.FileAssociations, "HKCU", @"Software\Classes\Applications\WinRAR.exe", null,
                $"{Q("WinRAR.exe")} is still offered in Open with, but WinRAR.exe is no longer installed.", Pf + @"\WinRAR\WinRAR.exe"),
            RegistryIssueRules.Create(RegistryIssueCategory.FileAssociations, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mkv\OpenWithList", "c",
                "The Open with list for .mkv files still offers mpc-hc64.exe, which is no longer installed.", Pf + @"\MPC-HC\mpc-hc64.exe"),

            Service("RzActionSvc", "Razer Game Manager Service", Pf86 + @"\Razer\Razer Services\GMS\GameManagerService.exe"),
            Service("AsusUpdateCheck", "ASUS Update Check", Pf86 + @"\ASUS\AsusUpdateCheck\AsusUpdateCheck.exe"),

            RegistryIssueRules.Create(RegistryIssueCategory.Fonts, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", "Roboto Condensed Bold (TrueType)",
                $"The font {Q("Roboto Condensed Bold (TrueType)")} is listed as installed, but its file RobotoCondensed-Bold.ttf is missing.", @"C:\Windows\Fonts\RobotoCondensed-Bold.ttf"),

            // Advanced-only categories (shown in advanced mode).
            RegistryIssueRules.Create(RegistryIssueCategory.ComObjects, "HKLM", @"SOFTWARE\WOW6432Node\Classes\CLSID\{6A2D3C8E-41F0-4B7A-9C15-2E8F7D0B1A44}", null,
                $"The component {Q("Old Game Launcher Overlay")} {{6A2D3C8E-41F0-4B7A-9C15-2E8F7D0B1A44}} is registered, but its file overlay32.dll is gone.", Pf86 + @"\Old Game Launcher\overlay32.dll"),
            RegistryIssueRules.Create(RegistryIssueCategory.ContextMenuHandlers, "HKLM", @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers\WinRAR", null,
                $"The file right-click menu entry {Q("WinRAR")} belongs to a program whose file RarExt.dll is gone.", Pf + @"\WinRAR\RarExt.dll"),
            RegistryIssueRules.Create(RegistryIssueCategory.InstallerFolders, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders", Pf86 + @"\Old Game Launcher\",
                $"Windows Installer still records the folder {Pf86}\\Old Game Launcher, which no longer exists.", Pf86 + @"\Old Game Launcher\"),
        ];
    }
}
