using PcChecker.Core.Common;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// Demo leftovers for the sample gaming desktop: traces of programs Alex removed over the last year (CCleaner, Zoom,
/// TeamViewer, MSI Afterburner, Razer Synapse, Opera GX, Origin, uTorrent and an IObit tool), scored with the real
/// rules. A focused scan after a demo uninstall adds the traces of that program. Removing and restoring only change
/// the in-memory lists; nothing on the machine is touched.
/// </summary>
public sealed class DemoLeftoverService : ILeftoverService
{
    private const string User = @"C:\Users\Alex";
    private const string Roaming = User + @"\AppData\Roaming";
    private const string Local = User + @"\AppData\Local";
    private const string Pf = @"C:\Program Files";
    private const string Pf86 = @"C:\Program Files (x86)";
    private const string CommonStart = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";
    private const string UserStart = Roaming + @"\Microsoft\Windows\Start Menu\Programs";

    private readonly object _gate = new();
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly List<LeftoverItem> _focusItems = [];
    private readonly List<LeftoverRestoreEntry> _restore;

    public DemoLeftoverService()
    {
        var lastWeek = DateTimeOffset.Now.AddDays(-6).Date.AddHours(20).AddMinutes(14);
        _restore =
        [
            new LeftoverRestoreEntry("demo-restore-1", LeftoverRestoreKind.RegistryKeys, lastWeek,
                "2 registry keys", @"HKCU\Software\Discord Inc. (old updater); HKLM\SOFTWARE\WOW6432Node\Overwolf",
                "registry-backup-" + lastWeek.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)),
            new LeftoverRestoreEntry("demo-restore-2", LeftoverRestoreKind.ScheduledTask, lastWeek.AddSeconds(3),
                "Overwolf Updater Task", @"\Overwolf Updater Task", LeftoverBackupNaming.TaskBackupId(lastWeek.AddSeconds(3), @"\Overwolf Updater Task")),
        ];
    }

    public LeftoverScanResult? Last { get; private set; }

    public string BackupDirectory => Local + @"\PcChecker\leftover-backups";

    public async Task<LeftoverScanResult> ScanAsync(LeftoverFocus? focus, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading installed programs", 5));
        await DemoData.DelayAsync(cancellationToken, 150, 350).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Checking program folders", 25, Pf));
        await DemoData.DelayAsync(cancellationToken, 250, 500).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Checking AppData", 50, Roaming));
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Checking registry, services and tasks", 75));
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Checking Start menu shortcuts", 92));
        await DemoData.DelayAsync(cancellationToken, 100, 200).ConfigureAwait(false);

        List<LeftoverItem> items;
        lock (_gate)
        {
            if (focus is not null)
            {
                _focusItems.RemoveAll(i => _removed.Contains(i.Id));
                foreach (var item in FocusItems(focus))
                {
                    if (!_removed.Contains(item.Id) && _focusItems.All(i => i.Id != item.Id))
                    {
                        _focusItems.Add(item);
                    }
                }
            }

            items = SampleItems().Concat(_focusItems)
                .Where(i => !_removed.Contains(i.Id))
                .Select(i => focus is not null && LeftoverRules.MatchesFocus(i.Name, i.Kind is LeftoverKind.Folder or LeftoverKind.EmptyFolder ? i.Location : null, focus)
                    ? i with { MatchesFocus = true, Confidence = LeftoverConfidence.High }
                    : i)
                .ToList();
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var result = new LeftoverScanResult(LeftoverRules.Order(items), 43, TimeSpan.FromSeconds(4.2), [], DateTimeOffset.Now, focus);
        Last = result;
        return result;
    }

    public async Task<OperationResult<LeftoverRemovalResult>> RemoveAsync(IReadOnlyList<LeftoverItem> items, nint ownerWindow,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return OperationResult<LeftoverRemovalResult>.Fail("Nothing was selected.");
        }

        progress?.Report(new ProgressUpdate("Creating a restore point", 5));
        await DemoData.DelayAsync(cancellationToken, 300, 600).ConfigureAwait(false);
        var outcomes = new List<LeftoverItemOutcome>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            progress?.Report(new ProgressUpdate("Removing", 10 + 85.0 * i / items.Count, item.Location));
            await DemoData.DelayAsync(cancellationToken, 60, 160).ConfigureAwait(false);
            outcomes.Add(item.CanRemove
                ? new LeftoverItemOutcome(item, true, Verb(item.Kind) + " (demo — nothing was changed).")
                : new LeftoverItemOutcome(item, false, "Kept: " + (item.Note ?? "it can only be reported.")));
        }

        var created = DateTimeOffset.Now;
        var entries = new List<LeftoverRestoreEntry>();
        var keys = outcomes.Where(o => o.Success && o.Item.Kind == LeftoverKind.RegistryKey).Select(o => o.Item).ToList();
        if (keys.Count > 0)
        {
            entries.Add(new LeftoverRestoreEntry(Guid.NewGuid().ToString("N"), LeftoverRestoreKind.RegistryKeys, created,
                keys.Count == 1 ? "1 registry key" : $"{keys.Count} registry keys", string.Join("; ", keys.Select(k => k.Location)),
                "registry-backup-" + created.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)));
        }

        foreach (var task in outcomes.Where(o => o.Success && o.Item.Kind == LeftoverKind.ScheduledTask).Select(o => o.Item))
        {
            entries.Add(new LeftoverRestoreEntry(Guid.NewGuid().ToString("N"), LeftoverRestoreKind.ScheduledTask, created,
                task.Name, task.Location, LeftoverBackupNaming.TaskBackupId(created, task.Location)));
        }

        lock (_gate)
        {
            foreach (var o in outcomes.Where(o => o.Success))
            {
                _removed.Add(o.Item.Id);
            }

            _restore.InsertRange(0, entries);
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var result = new LeftoverRemovalResult(outcomes, entries);
        return new OperationResult<LeftoverRemovalResult>(result.Removed > 0, LeftoverRules.DescribeResult(result), result);
    }

    public Task<IReadOnlyList<LeftoverRestoreEntry>> ListRestoreEntriesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LeftoverRestoreEntry>>(_restore.OrderByDescending(e => e.Created).ToList());
        }
    }

    public async Task<OperationResult> RestoreAsync(string entryId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        lock (_gate)
        {
            int index = _restore.FindIndex(e => e.Id == entryId);
            if (index < 0)
            {
                return OperationResult.Fail("That restore entry no longer exists.");
            }

            var entry = _restore[index];
            _restore[index] = entry with { Restored = DateTimeOffset.Now };
            return OperationResult.Ok(entry.Kind == LeftoverRestoreKind.ScheduledTask
                ? $"The task {entry.Title} is back in Task Scheduler (demo — nothing was changed)."
                : "The registry keys are back (demo — nothing was changed).");
        }
    }

    private static string Verb(LeftoverKind kind) => kind switch
    {
        LeftoverKind.Folder or LeftoverKind.EmptyFolder or LeftoverKind.Shortcut => "Moved to the Recycle Bin",
        LeftoverKind.RegistryKey => "Backed up and removed",
        LeftoverKind.ScheduledTask => "Exported to XML and deleted",
        _ => "Deleted",
    };

    /// <summary>The traces a removed program typically leaves: its install folder, its settings in AppData and its registry key.</summary>
    private static IEnumerable<LeftoverItem> FocusItems(LeftoverFocus focus)
    {
        var name = focus.ProgramName.Trim();
        var vendor = string.IsNullOrWhiteSpace(focus.Publisher) ? name : focus.Publisher.Trim().Split(',')[0];
        var folder = string.IsNullOrWhiteSpace(focus.InstallLocation) || !DiskSpace.DiskSpacePaths.IsLocalAbsolute(focus.InstallLocation)
            ? Pf + "\\" + name
            : DiskSpace.DiskSpacePaths.Normalize(focus.InstallLocation);
        var evidence = new LeftoverEvidence(LeftoverKind.Folder, MatchesFocus: true);
        yield return Make(LeftoverKind.Folder, name, folder, evidence, 3_400_000, note: null, focus: name, ageDays: 0);
        yield return Make(LeftoverKind.Folder, name + " settings", Roaming + "\\" + name, evidence with { InUserData = true }, 1_850_000, null, name, 0);
        var keyEvidence = new LeftoverEvidence(LeftoverKind.RegistryKey, MatchesFocus: true);
        yield return Make(LeftoverKind.RegistryKey, name, $@"HKCU\Software\{vendor}\{name}", keyEvidence, 6_200, null, name, 0);
    }

    private static IEnumerable<LeftoverItem> SampleItems()
    {
        // Empty folders: uninstallers removed the files but not the folder.
        yield return Make(LeftoverKind.EmptyFolder, "Origin", Pf86 + @"\Origin", new LeftoverEvidence(LeftoverKind.EmptyFolder), 0, ageDays: 402);
        yield return Make(LeftoverKind.EmptyFolder, "TeamViewer", @"C:\ProgramData\TeamViewer", new LeftoverEvidence(LeftoverKind.EmptyFolder), 0, ageDays: 233);

        // Folders with files.
        yield return Make(LeftoverKind.Folder, "CCleaner", Pf + @"\CCleaner",
            new LeftoverEvidence(LeftoverKind.Folder, SinceLastChange: TimeSpan.FromDays(274)), 4_200_000, ageDays: 274);
        yield return Make(LeftoverKind.Folder, "MSI Afterburner", Pf86 + @"\MSI Afterburner",
            new LeftoverEvidence(LeftoverKind.Folder, ContainsPrograms: true, SinceLastChange: TimeSpan.FromDays(305)), 52_400_000, ageDays: 305);
        yield return Make(LeftoverKind.Folder, "Zoom", Roaming + @"\Zoom",
            new LeftoverEvidence(LeftoverKind.Folder, InUserData: true, SinceLastChange: TimeSpan.FromDays(331)), 186_300_000, ageDays: 331);
        yield return Make(LeftoverKind.Folder, "Opera Software", Roaming + @"\Opera Software",
            new LeftoverEvidence(LeftoverKind.Folder, InUserData: true, SinceLastChange: TimeSpan.FromDays(198)), 412_700_000, ageDays: 198);
        yield return Make(LeftoverKind.Folder, "Razer", Local + @"\Razer",
            new LeftoverEvidence(LeftoverKind.Folder, InUserData: false, SinceLastChange: TimeSpan.FromDays(121)), 311_900_000, ageDays: 121);
        yield return Make(LeftoverKind.Folder, "uTorrent", Roaming + @"\uTorrent",
            new LeftoverEvidence(LeftoverKind.Folder, ContainsPrograms: true, InUserData: true, SinceLastChange: TimeSpan.FromDays(12)), 9_800_000, ageDays: 12);
        yield return Make(LeftoverKind.Folder, "Advanced SystemCare", Pf86 + @"\IObit\Advanced SystemCare",
            new LeftoverEvidence(LeftoverKind.Folder, UnderInstalledVendor: true, ContainsPrograms: true, SinceLastChange: TimeSpan.FromDays(64)), 96_100_000, ageDays: 64);

        // Registry keys.
        yield return Make(LeftoverKind.RegistryKey, "Piriform", @"HKCU\Software\Piriform", new LeftoverEvidence(LeftoverKind.RegistryKey), 18_400);
        yield return Make(LeftoverKind.RegistryKey, "TeamViewer", @"HKLM\SOFTWARE\WOW6432Node\TeamViewer", new LeftoverEvidence(LeftoverKind.RegistryKey), 41_700);
        yield return Make(LeftoverKind.RegistryKey, "Zoom", @"HKCU\Software\Zoom", new LeftoverEvidence(LeftoverKind.RegistryKey, KeyIsEmpty: true), 0);
        yield return Make(LeftoverKind.RegistryKey, "Advanced SystemCare", @"HKCU\Software\IObit\Advanced SystemCare",
            new LeftoverEvidence(LeftoverKind.RegistryKey, UnderInstalledVendor: true), 7_300);

        // Services.
        yield return Make(LeftoverKind.Service, "TeamViewer (TeamViewer)", "TeamViewer",
            new LeftoverEvidence(LeftoverKind.Service, TargetFolderMissing: true), null,
            target: Pf + @"\TeamViewer\TeamViewer_Service.exe", note: "Stopped. Start type: Automatic.");
        yield return Make(LeftoverKind.Service, "Razer Synapse Service (Razer Synapse Service)", "Razer Synapse Service",
            new LeftoverEvidence(LeftoverKind.Service), null,
            target: Pf86 + @"\Razer\Synapse3\Service\Razer Synapse Service.exe", note: "Stopped. Start type: Automatic.");

        // Scheduled tasks.
        yield return Make(LeftoverKind.ScheduledTask, "CCleaner Update", @"\CCleaner Update",
            new LeftoverEvidence(LeftoverKind.ScheduledTask), null, target: Pf + @"\CCleaner\CCUpdate.exe");
        yield return Make(LeftoverKind.ScheduledTask, "Opera GX scheduled Autoupdate 1712345678", @"\Opera GX scheduled Autoupdate 1712345678",
            new LeftoverEvidence(LeftoverKind.ScheduledTask, TargetFolderMissing: true), null, target: Local + @"\Programs\Opera GX\launcher.exe");

        // Start menu shortcuts.
        yield return Make(LeftoverKind.Shortcut, "CCleaner", CommonStart + @"\CCleaner\CCleaner.lnk",
            new LeftoverEvidence(LeftoverKind.Shortcut), 2_100, target: Pf + @"\CCleaner\CCleaner64.exe");
        yield return Make(LeftoverKind.Shortcut, "Zoom", UserStart + @"\Zoom\Zoom.lnk",
            new LeftoverEvidence(LeftoverKind.Shortcut, TargetFolderMissing: true), 1_900, target: Roaming + @"\Zoom\bin\Zoom.exe");
    }

    private static LeftoverItem Make(LeftoverKind kind, string name, string location, LeftoverEvidence evidence, long? size,
        string? note = null, string? focus = null, int? ageDays = null, string? target = null)
    {
        var confidence = LeftoverScoring.Score(evidence);
        return new LeftoverItem(LeftoverRules.IdFor(kind, location), kind, name, location,
            LeftoverScoring.Explain(evidence, target, focus), size, confidence, CanRemove: true, target, note, evidence.MatchesFocus,
            ageDays is { } days ? DateTimeOffset.Now.AddDays(-days) : null);
    }
}
