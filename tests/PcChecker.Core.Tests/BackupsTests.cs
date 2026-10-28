using PcChecker.Core.Backups;
using PcChecker.Core.Restore;
using PcChecker.Core.Shell;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>The Backups page: restore point rules, shadow storage, System Protection, Windows backups and PC Checker's own backups.</summary>
public class BackupsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    // ---- Restore points ---------------------------------------------------------------------------------------------

    [Fact]
    public void Wmi_times_are_read_without_the_computer_language()
    {
        var time = RestorePointRules.ParseWmiTime("20261027143005.123456+060");
        Assert.NotNull(time);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 14, 30, 5, TimeSpan.FromHours(1)).AddTicks(1234560), time!.Value);

        Assert.Equal(TimeSpan.FromMinutes(-300), RestorePointRules.ParseWmiTime("20260101000000.000000-300")!.Value.Offset);
        Assert.Null(RestorePointRules.ParseWmiTime("27.10.2026 14:30"));
        Assert.Null(RestorePointRules.ParseWmiTime("20261332000000.000000+000"));
        Assert.Null(RestorePointRules.ParseWmiTime(null));
    }

    [Theory]
    [InlineData(0, "Application install")]
    [InlineData(1, "Application uninstall")]
    [InlineData(7, "Checkpoint")]
    [InlineData(10, "Device driver install")]
    [InlineData(12, "Modify settings")]
    [InlineData(17, "Windows Update")]
    [InlineData(99, "Type 99")]
    public void Restore_point_types_are_words(int type, string expected)
    {
        Assert.Equal(expected, new SystemRestorePoint(1, "x", Now, type, 100).TypeText);
    }

    [Fact]
    public void Event_types_and_origins_are_plain_words()
    {
        Assert.Equal("Change started", RestorePointRules.EventName(100));
        Assert.Equal("Change finished", RestorePointRules.EventName(101));
        Assert.Equal("Event 7", RestorePointRules.EventName(7));

        Assert.Equal("PC Checker", new SystemRestorePoint(1, "PC Checker: before Disable startup item", Now, 12, 100).Origin);
        Assert.Equal("PC Checker", new SystemRestorePoint(2, "Manual restore point (PC Checker)", Now, 12, 100).Origin);
        Assert.Equal("Windows Update", new SystemRestorePoint(3, "Windows Update", Now, 17, 100).Origin);
        Assert.Equal("Windows (scheduled)", new SystemRestorePoint(4, "Scheduled Checkpoint", Now, 7, 100).Origin);
        Assert.Equal("A driver installer", new SystemRestorePoint(5, "Installed NVIDIA driver", Now, 10, 100).Origin);
    }

    [Fact]
    public void The_newest_point_is_the_highest_sequence_number()
    {
        var points = new[]
        {
            new SystemRestorePoint(41, "a", Now.AddDays(-3), 12, 100),
            new SystemRestorePoint(43, "c", Now.AddDays(-40), 12, 100),
            new SystemRestorePoint(42, "b", Now.AddDays(-1), 12, 100),
        };

        Assert.Equal(43, RestorePointRules.Newest(points)!.SequenceNumber);
        Assert.True(RestorePointRules.IsNewest(points[1], points));
        Assert.False(RestorePointRules.IsNewest(points[2], points));
        Assert.Equal(40, RestorePointRules.DaysSinceNewest(points, Now));
        Assert.Null(RestorePointRules.DaysSinceNewest([], Now));
    }

    // ---- Shadow storage ---------------------------------------------------------------------------------------------

    [Fact]
    public void Wmi_shadow_storage_gives_bytes_and_a_percentage()
    {
        const ulong capacity = 1_000_000_000_000;
        var info = ShadowStorageParser.FromWmi(used: 9_600_000_000, allocated: 10_100_000_000, max: 50_000_000_000, volumeCapacity: capacity);
        Assert.Equal(9_600_000_000, info.UsedBytes);
        Assert.Equal(10_100_000_000, info.AllocatedBytes);
        Assert.Equal(50_000_000_000, info.MaxBytes);
        Assert.False(info.IsUnbounded);
        Assert.Equal(5.0, info.MaxPercent);

        var unbounded = ShadowStorageParser.FromWmi(1, 1, ulong.MaxValue, capacity);
        Assert.True(unbounded.IsUnbounded);
        Assert.Equal(100, unbounded.MaxPercent);
        Assert.True(ShadowStorageParser.FromWmi(1, 1, capacity, capacity).IsUnbounded);
        Assert.Null(ShadowStorageParser.FromWmi(1, 1, 5_000, null).MaxPercent);
    }

    [Fact]
    public void The_volume_is_read_from_a_wmi_reference()
    {
        Assert.Equal(@"\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\",
            ShadowStorageParser.VolumeIdFromReference(@"Win32_Volume.DeviceID=""\\\\?\\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\\"""));
        Assert.Null(ShadowStorageParser.VolumeIdFromReference("Win32_Volume.DeviceID=\"C:\""));
        Assert.Null(ShadowStorageParser.VolumeIdFromReference(null));
    }

    [Fact]
    public void Vssadmin_output_is_read_in_english()
    {
        const string output = """
            vssadmin 1.1 - Volume Shadow Copy Service administrative command-line tool
            (C) Copyright 2001-2013 Microsoft Corp.

            Shadow Copy Storage association
               For volume: (C:)\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\
               Shadow Copy Storage volume: (C:)\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\
               Used Shadow Copy Storage space: 2.53 GB (1%)
               Allocated Shadow Copy Storage space: 3.07 GB (1%)
               Maximum Shadow Copy Storage space: 9.31 GB (5%)

            Shadow Copy Storage association
               For volume: (D:)\\?\Volume{9d2a11c1-0000-0000-0000-100000000000}\
               Shadow Copy Storage volume: (D:)\\?\Volume{9d2a11c1-0000-0000-0000-100000000000}\
               Used Shadow Copy Storage space: 0 bytes (0%)
               Allocated Shadow Copy Storage space: 0 bytes (0%)
               Maximum Shadow Copy Storage space: UNBOUNDED (100%)
            """;

        var blocks = ShadowStorageParser.ParseVssadmin(output);
        Assert.Equal(2, blocks.Count);

        var c = blocks[0];
        Assert.Equal("C:", c.ForDrive);
        Assert.Equal(@"\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\", c.ForVolumeId);
        Assert.Equal((long)Math.Round(2.53 * 1024 * 1024 * 1024), c.UsedBytes);
        Assert.Equal((long)Math.Round(9.31 * 1024 * 1024 * 1024), c.MaxBytes);
        Assert.Equal(5, c.MaxPercent);
        Assert.False(c.MaxUnbounded);

        var d = blocks[1];
        Assert.Equal("D:", d.ForDrive);
        Assert.Equal(0, d.UsedBytes);
        Assert.True(d.MaxUnbounded);
        Assert.Null(d.MaxBytes);
        Assert.True(ShadowStorageParser.ToInfo(d).IsUnbounded);
    }

    [Fact]
    public void Vssadmin_output_is_read_in_german_and_french()
    {
        const string german = """
            Schattenkopie-Speicherassoziation
               Für Volume: (C:)\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\
               Schattenkopie-Speichervolume: (C:)\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\
               Verwendeter Schattenkopie-Speicherbereich: 1,25 GB (0 %)
               Zugeordneter Schattenkopie-Speicherbereich: 1,5 GB (0 %)
               Maximaler Schattenkopie-Speicherbereich: 47,6 GB (10 %)
            """;
        var de = Assert.Single(ShadowStorageParser.ParseVssadmin(german));
        Assert.Equal("C:", de.ForDrive);
        Assert.Equal((long)Math.Round(1.25 * 1024 * 1024 * 1024), de.UsedBytes);
        Assert.Equal((long)Math.Round(47.6 * 1024 * 1024 * 1024), de.MaxBytes);
        Assert.Equal(10, de.MaxPercent);

        const string french = """
            Association de stockage de cliché instantané
               Pour le volume : (C:)\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\
               Volume de stockage de cliché instantané : (C:)\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\
               Espace de stockage utilisé : 512 Mo (0%)
               Espace de stockage alloué : 1 Go (0%)
               Espace de stockage maximal : ILLIMITÉ (100%)
            """;
        var fr = Assert.Single(ShadowStorageParser.ParseVssadmin(french));
        Assert.Equal(512L << 20, fr.UsedBytes);
        Assert.Equal(1L << 30, fr.AllocatedBytes);
        Assert.True(fr.MaxUnbounded);
        Assert.Empty(ShadowStorageParser.ParseVssadmin("No items found that satisfy the query."));
    }

    [Fact]
    public void A_percentage_limit_becomes_bytes()
    {
        Assert.Equal(50_000_000_000, ShadowStorageParser.BytesForPercent(1_000_000_000_000, 5));
        Assert.Equal(1_000_000_000_000, ShadowStorageParser.BytesForPercent(1_000_000_000_000, 250));
        Assert.Null(ShadowStorageParser.BytesForPercent(null, 5));
    }

    // ---- System Protection ------------------------------------------------------------------------------------------

    [Fact]
    public void Protected_drives_are_read_from_the_spp_client_list()
    {
        var list = SystemProtectionRules.ParseClients(
        [
            @"\\?\Volume{4c1b02c1-d990-11dc-99ae-806e6f6e6963}\:(C%3A)",
            @"\\?\Volume{9d2a11c1-0000-0000-0000-100000000000}\:(e%3a)",
            "",
            "garbage",
        ]);

        Assert.Equal(2, list.Count);
        Assert.Equal("C:", list[0].Drive);
        Assert.Equal("E:", list[1].Drive);
        Assert.True(SystemProtectionRules.IsProtected(list, "c:\\"));
        Assert.True(SystemProtectionRules.IsProtected(list, "E"));
        Assert.False(SystemProtectionRules.IsProtected(list, "D:"));

        // A drive whose letter changed is still found by its volume id.
        Assert.True(SystemProtectionRules.IsProtected(list, "F:", @"\\?\Volume{9D2A11C1-0000-0000-0000-100000000000}\"));
        Assert.Empty(SystemProtectionRules.ParseClients(null));
    }

    [Fact]
    public void Drive_letters_and_vssadmin_sizes_are_normalised()
    {
        Assert.Equal("C:", SystemProtectionRules.NormalizeDrive("c"));
        Assert.Equal("D:", SystemProtectionRules.NormalizeDrive(@"D:\"));
        Assert.Null(SystemProtectionRules.NormalizeDrive(@"\\server\share"));
        Assert.Null(SystemProtectionRules.NormalizeDrive("C:\\Windows"));

        Assert.Equal("/MaxSize=5%", SystemProtectionRules.MaxSizeArgument(5));
        Assert.Equal("/MaxSize=100%", SystemProtectionRules.MaxSizeArgument(400));
        Assert.Equal("/MaxSize=1%", SystemProtectionRules.MaxSizeArgument(0));
        Assert.Equal("/MaxSize=UNBOUNDED", SystemProtectionRules.MaxSizeArgument(new ShadowStorageInfo(1, 1, 0, true, 100)));
        Assert.Equal("/MaxSize=10737418240", SystemProtectionRules.MaxSizeArgument(new ShadowStorageInfo(1, 1, 10L << 30, false, 3)));

        Assert.Equal(3, SystemProtectionRules.PercentForEditing(new ShadowStorageInfo(1, 1, 1, false, 3.4)));
        Assert.Equal(SystemProtectionRules.SuggestedPercent, SystemProtectionRules.PercentForEditing(null));
    }

    // ---- Health hints -----------------------------------------------------------------------------------------------

    private static BackupHealthFacts Healthy() => new()
    {
        Now = Now,
        SystemDrive = "C:",
        SystemDriveProtected = true,
        PointsKnown = true,
        NewestPoint = Now.AddDays(-2),
        SystemDriveStorage = new ShadowStorageInfo(4L << 30, 5L << 30, 40L << 30, false, 5),
        FileHistory = BackupFeatureState.Off,
        OneDriveFolders = BackupFeatureState.On,
    };

    [Fact]
    public void A_healthy_pc_gets_no_hints_and_unknown_facts_give_none()
    {
        Assert.Empty(BackupHealthRules.Evaluate(Healthy()));
        Assert.Empty(BackupHealthRules.Evaluate(new BackupHealthFacts { Now = Now }));
    }

    [Fact]
    public void Protection_off_on_the_windows_drive_comes_first()
    {
        var hints = BackupHealthRules.Evaluate(Healthy() with { SystemDriveProtected = false, OneDriveFolders = BackupFeatureState.Off });
        Assert.Equal("protection.off", hints[0].Id);
        Assert.Equal(BackupHintLevel.Critical, hints[0].Level);
        Assert.Equal("System Protection is off on C:", string.Format(hints[0].Title, hints[0].Args.ToArray()));
        Assert.Equal(BackupHintAction.TurnOnProtection, hints[0].Action);
        Assert.Equal("files.unprotected", hints[^1].Id);

        // With protection off, the restore point age and the space do not matter.
        Assert.DoesNotContain(hints, h => h.Id is "restore.stale" or "restore.none" or "storage.small");
    }

    [Fact]
    public void An_old_or_missing_restore_point_is_flagged_after_30_days()
    {
        var stale = Assert.Single(BackupHealthRules.Evaluate(Healthy() with { NewestPoint = Now.AddDays(-30) }));
        Assert.Equal("restore.stale", stale.Id);
        Assert.Equal("No restore point in the last 30 days", string.Format(stale.Title, stale.Args.ToArray()));
        Assert.Empty(BackupHealthRules.Evaluate(Healthy() with { NewestPoint = Now.AddDays(-29) }));

        Assert.Equal("restore.none", Assert.Single(BackupHealthRules.Evaluate(Healthy() with { NewestPoint = null })).Id);
        Assert.Empty(BackupHealthRules.Evaluate(Healthy() with { NewestPoint = null, PointsKnown = false }));
    }

    [Fact]
    public void Less_than_one_gigabyte_for_restore_points_is_flagged()
    {
        var small = Assert.Single(BackupHealthRules.Evaluate(Healthy() with { SystemDriveStorage = new ShadowStorageInfo(1, 1, 700L << 20, false, 0.1) }));
        Assert.Equal("storage.small", small.Id);
        Assert.Equal("Restore points use less than 1 GB — older ones will be deleted quickly", small.Title);
        Assert.Empty(BackupHealthRules.Evaluate(Healthy() with { SystemDriveStorage = new ShadowStorageInfo(1, 1, 0, true, 100) }));
        Assert.Empty(BackupHealthRules.Evaluate(Healthy() with { SystemDriveStorage = new ShadowStorageInfo(1, 1, 1L << 30, false, 1) }));
    }

    [Fact]
    public void Personal_files_without_any_backup_get_a_suggestion()
    {
        var hint = Assert.Single(BackupHealthRules.Evaluate(Healthy() with { OneDriveFolders = BackupFeatureState.Off }));
        Assert.Equal("files.unprotected", hint.Id);
        Assert.Equal(BackupHintLevel.Suggestion, hint.Level);
        Assert.Empty(BackupHealthRules.Evaluate(Healthy() with { OneDriveFolders = BackupFeatureState.Unknown, FileHistory = BackupFeatureState.Off }));
    }

    [Fact]
    public void The_dashboard_flags_system_protection_off_on_the_windows_drive()
    {
        var facts = new AttentionFacts { Now = Now, LastCheckup = Now, SystemProtectionOn = false };
        var item = Assert.Single(AttentionRules.Build(facts));
        Assert.Equal("backups.protection-off", item.Id);
        Assert.Equal(AttentionLevel.Warning, item.Level);
        Assert.Equal(AttentionAction.OpenPage, item.Action);
        Assert.Equal("backups", item.PageKey);
        Assert.Contains("backups", HelpAndShortcutTests.NavigationKeys());

        Assert.Empty(AttentionRules.Build(facts with { SystemProtectionOn = true }));
        Assert.Empty(AttentionRules.Build(facts with { SystemProtectionOn = null }));
    }

    // ---- Windows backups --------------------------------------------------------------------------------------------

    [Fact]
    public void System_image_versions_are_read_by_their_fixed_form()
    {
        const string output = """
            wbadmin 1.0 - Sicherungs-Befehlszeilentool
            Sicherungszeit: 02.03.2026 21:14
            Sicherungsziel: 1394/USB-Datenträger mit der Bezeichnung Backup(E:)
            Versionsbezeichner: 03/02/2026-20:14
            Wiederherstellbar: Volume(s), Datei(en), Bare-Metal-Recovery

            Sicherungszeit: 10.09.2026 08:00
            Versionsbezeichner: 09/10/2026-06:00
            """;

        var versions = WindowsBackupRules.ParseWbadminVersions(output);
        Assert.Equal(2, versions.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 6, 0, 0, TimeSpan.Zero), versions[0]);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 20, 14, 0, TimeSpan.Zero), versions[1]);

        var status = WindowsBackupRules.DescribeSystemImages(0, output, Now);
        Assert.Equal(2, status.Count);
        Assert.Equal("2 Windows backups found; the newest was made 48 days ago.", status.Message);

        var none = WindowsBackupRules.DescribeSystemImages(-2147024894, "ERROR - No backup was found.", Now);
        Assert.Equal(0, none.Count);
        Assert.Null(none.Newest);
        Assert.Empty(WindowsBackupRules.ParseWbadminVersions("13/45/2026-99:99"));
    }

    [Fact]
    public void Folders_inside_onedrive_are_backed_up_by_it()
    {
        var folders = new Dictionary<string, string?>
        {
            ["Desktop"] = @"C:\Users\Alex\OneDrive\Desktop",
            ["Documents"] = @"c:\users\alex\onedrive\Documents\",
            ["Pictures"] = @"C:\Users\Alex\Pictures",
            ["Music"] = @"C:\Users\Alex\OneDriveOld\Music",
            ["Videos"] = null,
        };

        var inside = WindowsBackupRules.FoldersInOneDrive(folders, [@"C:\Users\Alex\OneDrive\", null, ""]);
        Assert.Equal(["Desktop", "Documents"], inside);
        Assert.Equal("Desktop and Documents", WindowsBackupRules.JoinNames(inside));
        Assert.Equal("Desktop, Documents and Pictures", WindowsBackupRules.JoinNames(["Desktop", "Documents", "Pictures"]));
        Assert.Empty(WindowsBackupRules.FoldersInOneDrive(folders, []));
    }

    [Fact]
    public void File_history_is_on_when_it_has_a_configuration()
    {
        Assert.Equal(BackupFeatureState.On, WindowsBackupRules.FileHistoryState(true, ["Config1.xml", "Config2.xml"]));
        Assert.Equal(BackupFeatureState.Off, WindowsBackupRules.FileHistoryState(true, ["readme.txt"]));
        Assert.Equal(BackupFeatureState.Off, WindowsBackupRules.FileHistoryState(false, null));
    }

    // ---- PC Checker's own backups -----------------------------------------------------------------------------------

    private static AppBackupItem Item(string name, int daysAgo, long size = 100) => new(@"C:\b\" + name, Now.AddDays(-daysAgo), size);

    [Fact]
    public void Delete_old_ones_keeps_the_newest_n()
    {
        var items = new[] { Item("a", 10), Item("b", 1), Item("c", 30), Item("d", 5), Item("e", 60, 500) };

        var toDelete = AppBackupCatalog.SelectToDelete(items, 2);
        Assert.Equal(["e", "c", "a"], toDelete.Select(i => Path.GetFileName(i.Path.Replace('\\', '/'))));
        Assert.Equal(700, AppBackupCatalog.BytesFreed(items, 2));

        // The newest always stays, and nothing goes when there are no more than N.
        Assert.Equal(4, AppBackupCatalog.SelectToDelete(items, 0).Count);
        Assert.Equal(4, AppBackupCatalog.SelectToDelete(items, -3).Count);
        Assert.Empty(AppBackupCatalog.SelectToDelete(items, 5));
        Assert.Empty(AppBackupCatalog.SelectToDelete([], 1));

        // Same time: the choice does not change between calls.
        var tie = new[] { Item("x", 3), Item("y", 3), Item("z", 3) };
        Assert.Equal(AppBackupCatalog.SelectToDelete(tie, 1), AppBackupCatalog.SelectToDelete(tie.Reverse(), 1));
    }

    [Fact]
    public void The_catalog_uses_each_features_own_folder_and_protects_what_must_stay()
    {
        var folders = new AppBackupFolders("setup", "registry", "drivers", "debloat-capture.json", "tasks", "hosts", "sendto", "startup");
        var locations = AppBackupCatalog.Build(folders);

        Assert.Equal(locations.Count, locations.Select(l => l.Id).Distinct().Count());
        Assert.False(locations.Single(l => l.Id == AppBackupCatalog.DebloatId).CanDeleteOld);
        Assert.False(locations.Single(l => l.Id == AppBackupCatalog.SendToId).CanDeleteOld);
        Assert.All(locations.Where(l => !l.CanDeleteOld), l => Assert.False(string.IsNullOrWhiteSpace(l.KeepReason)));
        Assert.Equal([".json"], locations.Single(l => l.Id == AppBackupCatalog.RegistryId).CompanionExtensions);

        var pages = HelpAndShortcutTests.NavigationKeys();
        Assert.All(locations, l => Assert.Contains(l.PageKey, pages));

        var real = AppBackupFolders.ForCurrentUser("setup", "settings", "drivers", "hosts");
        Assert.EndsWith("registry-backups", real.RegistryBackups);
        Assert.EndsWith("task-backups", real.TaskBackups);
        Assert.EndsWith("debloat-capture.json", real.DebloatCaptureFile);
    }

    [Fact]
    public void Backup_files_are_counted_with_their_sidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcchecker-backups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "registry-backup-20261001-100000.reg"), new byte[100]);
            File.WriteAllBytes(Path.Combine(root, "registry-backup-20261001-100000.json"), new byte[20]);
            File.WriteAllBytes(Path.Combine(root, "registry-backup-20261002-100000.reg"), new byte[50]);
            File.WriteAllBytes(Path.Combine(root, "unrelated.txt"), new byte[999]);
            var sub = Directory.CreateDirectory(Path.Combine(root, "20261003-120000"));
            File.WriteAllBytes(Path.Combine(sub.FullName, "oem1.inf"), new byte[300]);
            Directory.CreateDirectory(Path.Combine(sub.FullName, "nested"));
            File.WriteAllBytes(Path.Combine(sub.FullName, "nested", "driver.sys"), new byte[700]);

            var folders = new AppBackupFolders("s", root, root, "d.json", "t", "h", "so", "st");
            var registry = AppBackupCatalog.Build(folders).Single(l => l.Id == AppBackupCatalog.RegistryId);
            var items = AppBackupFiles.List(registry);
            Assert.Equal(2, items.Count);
            Assert.Equal(170, items.Sum(i => i.SizeBytes));
            Assert.Equal([Path.Combine(root, "registry-backup-20261001-100000.json")],
                AppBackupFiles.Companions(registry, Path.Combine(root, "registry-backup-20261001-100000.reg")));
            Assert.True(AppBackupFiles.IsInside(registry, items[0].Path));
            Assert.False(AppBackupFiles.IsInside(registry, Path.Combine(Path.GetTempPath(), "elsewhere.reg")));

            var drivers = AppBackupCatalog.Build(folders).Single(l => l.Id == AppBackupCatalog.DriversId);
            var folder = Assert.Single(AppBackupFiles.List(drivers));
            Assert.Equal(1000, folder.SizeBytes);

            var missing = AppBackupCatalog.Build(folders with { HostsBackups = Path.Combine(root, "no-such-folder") }).Single(l => l.Id == AppBackupCatalog.HostsId);
            Assert.Empty(AppBackupFiles.List(missing));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- Demo -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Demo_mode_fills_every_part_of_the_page()
    {
        var demo = new DemoBackupsService();
        var overview = await demo.GetRestorePointsAsync(CancellationToken.None);
        Assert.True(overview.Points.Count >= 4);
        Assert.True(overview.SystemDrive!.IsProtected);
        Assert.Contains(overview.Drives, d => d.IsProtected == false);
        Assert.Equal(3, (await demo.GetWindowsBackupsAsync(CancellationToken.None)).Count);
        Assert.True((await demo.CheckSystemImagesAsync(CancellationToken.None)).Count > 0);

        var backups = await demo.GetAppBackupsAsync(CancellationToken.None);
        Assert.All(backups.Where(b => b.Location.CanDeleteOld && b.Location.Id != AppBackupCatalog.StartupId && b.Location.Id != AppBackupCatalog.TasksId),
            b => Assert.True(b.Count > 0));

        var hosts = backups.Single(b => b.Location.Id == AppBackupCatalog.HostsId);
        var result = await demo.DeleteOldAppBackupsAsync(AppBackupCatalog.HostsId, 3, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(hosts.Count - 3, result.Value);
        Assert.False((await demo.DeleteOldAppBackupsAsync(AppBackupCatalog.DebloatId, 1, CancellationToken.None)).Success);

        var trimmed = await demo.DeleteAllButNewestAsync(null, CancellationToken.None);
        Assert.True(trimmed.Success);
        Assert.Single((await demo.GetRestorePointsAsync(CancellationToken.None)).Points);
    }
}
