using PcChecker.Core.DiskSpace;
using Xunit;

namespace PcChecker.Core.Tests;

public class DiskSpaceTests
{
    private static readonly ProtectedLocations Locations = new(
        @"C:\Windows",
        [@"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData"],
        @"C:\Users",
        @"C:\Users\Alex",
        [@"C:\Tools\PcChecker", @"C:\Users\Alex\AppData\Local\PcChecker"],
        [@"D:\Documents", @"C:\Users\Alex\Desktop"]);

    private static DiskFileEntry Entry(string path, long size, int ageDays = 10, FileTypeCategory category = FileTypeCategory.Other) =>
        new(path, size, new DateTime(2026, 1, 1).AddDays(-ageDays), null, category);

    // ---- paths ----

    [Theory]
    [InlineData(@"C:", @"C:\")]
    [InlineData(@"c:/Users/Alex/", @"c:\Users\Alex")]
    [InlineData(@"  D:\Games\\ ", @"D:\Games")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("/tmp/scan/", "/tmp/scan")]
    public void Normalize_keeps_drive_roots_and_trims_separators(string input, string expected) =>
        Assert.Equal(expected, DiskSpacePaths.Normalize(input));

    [Fact]
    public void Path_helpers_split_and_compare_without_prefix_traps()
    {
        Assert.Equal("Alex", DiskSpacePaths.NameOf(@"C:\Users\Alex"));
        Assert.Equal("C:", DiskSpacePaths.NameOf(@"C:\"));
        Assert.Equal(@"C:\", DiskSpacePaths.ParentOf(@"C:\Users"));
        Assert.Null(DiskSpacePaths.ParentOf(@"C:\"));
        Assert.Equal(@"C:\Users\a.txt", DiskSpacePaths.Combine(@"C:\Users", "a.txt"));
        Assert.Equal(@"C:\a.txt", DiskSpacePaths.Combine(@"C:\", "a.txt"));
        Assert.Equal("/tmp/a.txt", DiskSpacePaths.Combine("/tmp", "a.txt"));
        Assert.True(DiskSpacePaths.IsSameOrInside(@"c:\program files\x\y.dll", @"C:\Program Files"));
        Assert.False(DiskSpacePaths.IsSameOrInside(@"C:\Program Files Backup\y.dll", @"C:\Program Files"));
        Assert.True(DiskSpacePaths.IsSameOrInside(@"C:\anything", @"C:\"));
        Assert.Equal(["Users", "Alex"], DiskSpacePaths.RelativeSegments(@"C:\Users\Alex", @"C:\"));
        Assert.Null(DiskSpacePaths.RelativeSegments(@"D:\x", @"C:\"));
        Assert.True(DiskSpacePaths.HasRelativeSegments(@"C:\Users\..\Windows"));
    }

    // ---- size aggregation ----

    [Fact]
    public void Build_tree_adds_sizes_up_to_every_parent()
    {
        var root = DiskSpaceAggregation.BuildTree(@"C:\", [
            (@"C:\pagefile.sys", 100),
            (@"C:\Users\Alex\Videos\a.mp4", 1_000),
            (@"C:\Users\Alex\Videos\b.mp4", 500),
            (@"C:\Users\Alex\Downloads\setup.exe", 50),
            (@"D:\elsewhere.bin", 9_999),
        ], [@"C:\Empty"]);

        Assert.Equal(1_650, root.TotalBytes);
        Assert.Equal(4, root.TotalFiles);
        Assert.Equal(100, root.OwnBytes);
        Assert.Equal(5, root.TotalFolders); // Empty, Users, Alex, Videos, Downloads
        var alex = DiskSpaceAggregation.Find(root, @"c:\users\ALEX");
        Assert.NotNull(alex);
        Assert.Equal(1_550, alex!.TotalBytes);
        Assert.Equal(["Videos", "Downloads"], DiskSpaceAggregation.SortedChildren(alex).Select(c => c.Name));
        Assert.Equal(["C:", "Users", "Alex"], DiskSpaceAggregation.Breadcrumbs(alex).Select(n => n.Name));
        Assert.Equal("Users", DiskSpaceAggregation.LargestChild(root)!.Name);
    }

    [Fact]
    public void Removing_a_folder_or_file_updates_every_parent()
    {
        var root = DiskSpaceAggregation.BuildTree(@"C:\", [
            (@"C:\Data\Keep\k.bin", 10),
            (@"C:\Data\Old\a.bin", 300),
            (@"C:\Data\Old\Sub\b.bin", 200),
            (@"C:\Data\loose.iso", 1_000),
        ]);
        var data = DiskSpaceAggregation.Find(root, @"C:\Data")!;
        Assert.Equal(1_510, data.TotalBytes);

        Assert.True(DiskSpaceAggregation.RemoveFolder(root, @"C:\Data\Old"));
        Assert.Equal(1_010, data.TotalBytes);
        Assert.Equal(2, data.TotalFiles);
        Assert.Equal(1, data.TotalFolders);
        Assert.Null(DiskSpaceAggregation.Find(root, @"C:\Data\Old\Sub"));

        Assert.True(DiskSpaceAggregation.RemoveFile(root, @"C:\Data\loose.iso", 1_000));
        Assert.Equal(10, root.TotalBytes);
        Assert.Equal(1, root.TotalFiles);
        Assert.False(DiskSpaceAggregation.RemoveFolder(root, @"C:\")); // the scan root itself is never removed
    }

    [Fact]
    public void Aggregate_counts_unreadable_folders_below()
    {
        var root = new FolderNode(@"C:\");
        var a = root.AddChild("A");
        a.AddChild("Locked").IsInaccessible = true;
        root.AddChild("System Volume Information").IsInaccessible = true;
        a.AddFile(42);
        DiskSpaceAggregation.Aggregate(root);
        Assert.Equal(2, root.InaccessibleFolders);
        Assert.Equal(1, a.InaccessibleFolders);
        Assert.Equal(42, root.TotalBytes);
    }

    // ---- type classification ----

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", FileTypeCategory.System)]
    [InlineData(@"C:\Windows\Media\Alarm01.wav", FileTypeCategory.System)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\archive\pc\content\basegame_1.archive", FileTypeCategory.Games)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Game\intro.mp4", FileTypeCategory.Games)]
    [InlineData(@"C:\XboxGames\Starfield\Content\Starfield - Textures01.ba2", FileTypeCategory.Games)]
    [InlineData(@"E:\Epic Games\Fortnite\FortniteGame\Content\Paks\global.utoc", FileTypeCategory.Games)]
    [InlineData(@"C:\Users\Alex\Videos\clip.MKV", FileTypeCategory.Video)]
    [InlineData(@"C:\Users\Alex\Pictures\IMG_0001.HEIC", FileTypeCategory.Images)]
    [InlineData(@"C:\Users\Alex\Desktop\backup.7z", FileTypeCategory.Archives)]
    [InlineData(@"C:\Users\Alex\Desktop\Win11.iso", FileTypeCategory.Archives)]
    [InlineData(@"C:\Users\Alex\Desktop\tool.msi", FileTypeCategory.Installers)]
    [InlineData(@"C:\Users\Alex\Downloads\Discord.exe", FileTypeCategory.Installers)]
    [InlineData(@"C:\Temp\ChromeSetup.exe", FileTypeCategory.Installers)]
    [InlineData(@"C:\Tools\ffmpeg.exe", FileTypeCategory.Other)]
    [InlineData(@"C:\Tools\library.dll", FileTypeCategory.System)]
    [InlineData(@"C:\Users\Alex\Documents\notes", FileTypeCategory.Other)]
    [InlineData(@"C:\Users\Alex\Documents\report.docx", FileTypeCategory.Other)]
    public void Files_are_classified_by_folder_then_extension(string path, FileTypeCategory expected) =>
        Assert.Equal(expected, FileTypeClassifier.Classify(path));

    [Fact]
    public void A_custom_windows_folder_is_respected()
    {
        Assert.Equal(FolderKind.Windows, FileTypeClassifier.ClassifyFolder(@"D:\WINNT\System32", @"D:\WINNT"));
        Assert.Equal(FolderKind.Normal, FileTypeClassifier.ClassifyFolder(@"C:\Windows\System32", @"D:\WINNT"));
        Assert.Equal(FolderKind.Normal, FileTypeClassifier.ClassifyFolder(@"C:\Users\Alex\Windows stuff"));
    }

    // ---- protected paths ----

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\Temp\x.tmp")]
    [InlineData(@"C:\Program Files\Steam\steam.exe")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\ProgramData\NVIDIA\cache.bin")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\Alex")]
    [InlineData(@"C:\Users\Alex\Documents")]
    [InlineData(@"C:\Users\Alex\NTUSER.DAT")]
    [InlineData(@"C:\Users\Public")]
    [InlineData(@"C:\Users\Bob\Downloads")]
    [InlineData(@"C:\Users\Alex\AppData\Local")]
    [InlineData(@"C:\Users\Alex\AppData\Roaming")]
    [InlineData(@"C:\Users\Alex\AppData\Local\PcChecker\logs\app.log")]
    [InlineData(@"C:\Tools")]
    [InlineData(@"C:\Tools\PcChecker\PcChecker.exe")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"D:\hiberfil.sys")]
    [InlineData(@"D:\System Volume Information")]
    [InlineData(@"D:\$Recycle.Bin\S-1-5-21\file.mp4")]
    [InlineData(@"C:\Recovery\WindowsRE\winre.wim")]
    [InlineData(@"C:\Users\Alex\AppData\Roaming\Microsoft\Protect\S-1-5-21\key")]
    [InlineData(@"C:\Users\Alex\AppData\Local\Microsoft\WindowsApps\python.exe")]
    [InlineData(@"C:\Users\Alex\AppData\Local\Microsoft")]
    [InlineData(@"C:\Users\Alex\AppData\Local\Packages\Microsoft.WindowsStore_8wekyb3d8bbwe")]
    [InlineData(@"C:\inetpub")]
    [InlineData(@"D:\Program Files\Some App\app.exe")]
    [InlineData(@"E:\Windows\System32")]
    [InlineData(@"D:\Documents")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Windows.old\Users\Alex\Documents")]
    [InlineData(@"\\server\share\big.iso")]
    [InlineData(@"relative\path.txt")]
    [InlineData(@"C:\Users\Alex\Downloads\..\..\..\Windows")]
    [InlineData("")]
    public void Protected_locations_are_never_offered(string path) =>
        Assert.NotNull(DiskSpaceProtection.WhyProtected(path, Locations));

    [Theory]
    [InlineData(@"C:\Users\Alex\Downloads\Win11.iso")]
    [InlineData(@"C:\Users\Alex\Videos\Captures")]
    [InlineData(@"C:\Users\Alex\AppData\Local\Temp")]
    [InlineData(@"C:\Users\Alex\AppData\Local\Spotify\Data")]
    [InlineData(@"C:\Program Files Backup\old.zip")]
    [InlineData(@"C:\Games\Old game")]
    [InlineData(@"D:\Media\Movies\film.mkv")]
    [InlineData(@"D:\Media\Recovery\notes.txt")]
    [InlineData(@"D:\Documents\Old projects\backup.zip")]
    [InlineData(@"C:\Users\Alex\AppData\Local\MicrosoftEdgeBackup\old.bin")]
    [InlineData(@"D:\Windows backups\image.vhdx")]
    public void Ordinary_user_content_can_be_offered(string path) =>
        Assert.Null(DiskSpaceProtection.WhyProtected(path, Locations));

    [Fact]
    public void System_attribute_is_protected_and_without_locations_only_structural_rules_apply()
    {
        Assert.NotNull(DiskSpaceProtection.WhyProtected(FileAttributes.System | FileAttributes.Hidden));
        Assert.Null(DiskSpaceProtection.WhyProtected(FileAttributes.Archive | FileAttributes.ReadOnly));
        Assert.Null(DiskSpaceProtection.WhyProtected(@"C:\Data\x.txt", ProtectedLocations.None));
        Assert.NotNull(DiskSpaceProtection.WhyProtected(@"C:\pagefile.sys", ProtectedLocations.None));
        Assert.NotNull(DiskSpaceProtection.WhyProtected(@"C:\", ProtectedLocations.None));
    }

    // ---- duplicates ----

    [Fact]
    public void Group_by_size_keeps_only_sizes_seen_twice_and_ignores_small_or_empty_files()
    {
        var files = new[]
        {
            Entry(@"C:\a\one.bin", 5_000), Entry(@"C:\b\one.bin", 5_000),
            Entry(@"C:\a\big.iso", 90_000), Entry(@"C:\b\big.iso", 90_000), Entry(@"C:\c\big.iso", 90_000),
            Entry(@"C:\a\single.bin", 7_000),
            Entry(@"C:\a\empty.txt", 0), Entry(@"C:\b\empty.txt", 0),
            Entry(@"C:\a\tiny.txt", 10), Entry(@"C:\b\tiny.txt", 10),
            Entry(@"C:\A\ONE.BIN", 5_000), // same file listed twice in another case
        };

        var groups = DuplicateFinder.GroupBySize(files, minBytes: 1_000);
        Assert.Equal(2, groups.Count);
        Assert.Equal(90_000, groups[0][0].SizeBytes); // most potential waste first
        Assert.Equal(3, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
    }

    [Fact]
    public void Only_size_groups_with_a_removable_file_are_worth_hashing()
    {
        var files = new[]
        {
            Entry(@"C:\Windows\WinSxS\a.dll", 4_000), Entry(@"C:\Windows\System32\a.dll", 4_000),
            Entry(@"C:\Program Files\App\video.mp4", 9_000), Entry(@"C:\Users\Alex\Videos\video.mp4", 9_000),
            Entry(@"C:\Users\Alex\Downloads\x.zip", 7_000),
        };
        Func<string, bool> isProtected = p => DiskSpaceProtection.WhyProtected(p, Locations) is not null;

        var worth = DuplicateFinder.ActionableCandidates(files, 1_000, isProtected);
        Assert.Equal([@"C:\Program Files\App\video.mp4", @"C:\Users\Alex\Videos\video.mp4"], worth.Select(f => f.Path));
    }

    [Fact]
    public void Split_by_hash_drops_unreadable_files_and_singletons_and_hard_links_count_once()
    {
        var group = new List<DiskFileEntry> { Entry(@"C:\1", 10), Entry(@"C:\2", 10), Entry(@"C:\3", 10), Entry(@"C:\4", 10), Entry(@"C:\5", 10) };
        var keys = new Dictionary<string, string?> { [@"C:\1"] = "A", [@"C:\2"] = "A", [@"C:\3"] = "B", [@"C:\4"] = null, [@"C:\5"] = "A" };
        var parts = DuplicateFinder.SplitBy([group], f => keys[f.Path]);
        var only = Assert.Single(parts);
        Assert.Equal([@"C:\1", @"C:\2", @"C:\5"], only.Select(f => f.Path));

        var ids = new Dictionary<string, string?> { [@"C:\1"] = "vol:1", [@"C:\2"] = "vol:1", [@"C:\5"] = null };
        Assert.Equal([@"C:\1", @"C:\5"], DuplicateFinder.CollapseSameFile(only, f => ids[f.Path]).Select(f => f.Path));
    }

    [Fact]
    public void Quick_hash_ranges_cover_small_files_whole_and_large_files_at_both_ends()
    {
        Assert.Empty(DuplicateFinder.PartialRanges(0));
        Assert.Equal([(0L, 100)], DuplicateFinder.PartialRanges(100, 64));
        Assert.Equal([(0L, 128)], DuplicateFinder.PartialRanges(128, 64));
        Assert.Equal([(0L, 64), (65L, 64)], DuplicateFinder.PartialRanges(129, 64));
        Assert.False(DuplicateFinder.NeedsFullHash(128, 64));
        Assert.True(DuplicateFinder.NeedsFullHash(129, 64));
    }

    [Fact]
    public void Keep_newest_and_keep_oldest_tick_every_other_copy_but_never_protected_ones()
    {
        var oldest = new DiskFileEntry(@"C:\Users\Alex\Pictures\a.jpg", 9, new DateTime(2020, 1, 1), null, FileTypeCategory.Images);
        var middle = new DiskFileEntry(@"C:\Users\Alex\Backup\a.jpg", 9, new DateTime(2022, 1, 1), null, FileTypeCategory.Images);
        var newest = new DiskFileEntry(@"C:\Program Files\App\a.jpg", 9, new DateTime(2024, 1, 1), null, FileTypeCategory.Images);
        var group = new DuplicateGroup("H", 9, [oldest, middle, newest]);
        Assert.Equal(18, group.WastedBytes);

        Assert.Equal([oldest, middle], DuplicateSelection.KeepNewest(group));
        Assert.Equal([middle, newest], DuplicateSelection.KeepOldest(group));
        Func<string, bool> isProtected = p => DiskSpaceProtection.WhyProtected(p, Locations) is not null;
        Assert.Equal([middle], DuplicateSelection.KeepOldest(group, isProtected));

        var groups = DuplicateFinder.ToGroups([("small", new[] { Entry(@"C:\x", 5), Entry(@"C:\y", 5) }), ("big", new[] { Entry(@"C:\p", 50), Entry(@"C:\q", 50) })]);
        Assert.Equal(["big", "small"], groups.Select(g => g.Hash));
    }

    // ---- collection while scanning ----

    [Fact]
    public void Collector_counts_types_keeps_largest_and_old_files_and_skips_cloud_only_sizes()
    {
        var now = new DateTime(2026, 6, 1);
        var options = new DiskScanOptions { LargestFileCount = 2, OldFileMinBytes = 100, OldFileDays = 365, DuplicateMinBytes = 50 };
        var a = new DiskScanCollector(options, now);
        var b = new DiskScanCollector(options, now);
        a.AddFile(@"C:\Videos", "film.mkv", 1_000, FileAttributes.Archive, now.AddYears(-3), now.AddYears(-2), FolderKind.Normal);
        a.AddFile(@"C:\Videos", "clip.mp4", 400, FileAttributes.Archive, now.AddDays(-2), null, FolderKind.Normal);
        b.AddFile(@"C:\Pics", "a.jpg", 30, FileAttributes.Archive, now.AddYears(-5), null, FolderKind.Normal);
        b.AddFile(@"C:\Pics", "b.png", 700, FileAttributes.Archive, now.AddYears(-2), now.AddDays(-10), FolderKind.Normal); // opened recently
        long counted = b.AddFile(@"C:\OneDrive", "online.mp4", 5_000, FileAttributes.Archive | (FileAttributes)0x00400000, now, null, FolderKind.Normal);
        Assert.Equal(0, counted);

        a.Merge(b);
        Assert.Equal(5, a.Files);
        Assert.Equal(1, a.CloudOnlyFiles);
        Assert.Equal(2_130, a.Bytes);
        Assert.Equal([@"C:\Videos\film.mkv", @"C:\Pics\b.png"], a.LargestFiles().Select(f => f.Path));
        Assert.Equal([@"C:\Videos\film.mkv"], a.OldFiles().Select(f => f.Path));
        Assert.Equal(3, a.DuplicateCandidates().Count);
        var types = a.Types();
        Assert.Equal(FileTypeCategory.Video, types[0].Category);
        Assert.Equal(1_400, types[0].Bytes);
        Assert.Equal(730, types.Single(t => t.Category == FileTypeCategory.Images).Bytes);
    }

    [Fact]
    public void Bounded_top_list_keeps_the_largest_keys_across_merges()
    {
        var a = new BoundedTopList<string>(3);
        var b = new BoundedTopList<string>(3);
        foreach (var (name, size) in new[] { ("a", 5L), ("b", 1L), ("c", 9L), ("d", 3L) })
        {
            a.Add(name, size);
        }

        b.Add("e", 7);
        b.Add("f", 2);
        a.Merge(b);
        Assert.Equal(["c", "e", "a"], a.ToDescendingList());
        Assert.False(a.WouldAccept(4));
        Assert.True(a.WouldAccept(6));
        Assert.False(new BoundedTopList<string>(0).WouldAccept(100));
    }

    [Fact]
    public void Old_big_file_rule_needs_both_size_and_age()
    {
        var now = new DateTime(2026, 6, 1);
        Assert.True(DiskSpaceRules.IsOldBigFile(200, now.AddDays(-400), now, 100, 365));
        Assert.False(DiskSpaceRules.IsOldBigFile(50, now.AddDays(-400), now, 100, 365));
        Assert.False(DiskSpaceRules.IsOldBigFile(200, now.AddDays(-100), now, 100, 365));
        Assert.True(DiskSpaceRules.IsCloudOnly(FileAttributes.Offline));
        Assert.False(DiskSpaceRules.IsCloudOnly(FileAttributes.Archive | FileAttributes.ReparsePoint));
    }

    // ---- selection and removal ----

    [Fact]
    public void Collapse_nested_drops_items_inside_selected_folders_and_repeats()
    {
        var items = new[]
        {
            new RecycleRequest(@"C:\Data\Old\file.bin", false, 10),
            new RecycleRequest(@"C:\Data\Old", true, 100),
            new RecycleRequest(@"C:\Data\Older\x.bin", false, 5),
            new RecycleRequest(@"c:\data\old", true, 100),
        };

        var plan = DiskSpaceRules.CollapseNested(items, i => i.Path);
        Assert.Equal([@"C:\Data\Old", @"C:\Data\Older\x.bin"], plan.Select(p => p.Path));

        var gone = DiskSpaceRules.RemovedFilter([@"C:\Data\Old", @"C:\Videos\a.mp4"]);
        Assert.True(gone(@"c:\data\old\deep\file.bin"));
        Assert.True(gone(@"C:\Videos\a.mp4"));
        Assert.False(gone(@"C:\Data\Older\x.bin"));
        Assert.False(gone(@"C:\Videos\a.mp4.bak"));
    }

    [Theory]
    [InlineData(@"C:\Users\Alex\OneDrive\Pictures\a.jpg", true)]
    [InlineData(@"C:\Users\Alex\OneDrive - Contoso\Report.docx", true)]
    [InlineData(@"C:\Users\Alex\Documents\OneDrive notes.txt", false)]
    [InlineData(@"C:\Users\Alex\OneDrive", false)]
    public void OneDrive_items_are_recognised_for_the_warning(string path, bool expected) =>
        Assert.Equal(expected, DiskSpaceRules.IsInCloudFolder(path));

    [Fact]
    public void Removing_recycled_items_updates_the_scan_without_rescanning()
    {
        var root = DiskSpaceAggregation.BuildTree(@"C:\", [
            (@"C:\Downloads\a.iso", 4_000),
            (@"C:\Downloads\Old\b.zip", 2_000),
            (@"C:\Videos\c.mp4", 3_000),
        ]);
        var iso = Entry(@"C:\Downloads\a.iso", 4_000, category: FileTypeCategory.Archives);
        var zip = Entry(@"C:\Downloads\Old\b.zip", 2_000, category: FileTypeCategory.Archives);
        var mp4 = Entry(@"C:\Videos\c.mp4", 3_000, category: FileTypeCategory.Video);
        var scan = new DiskScanResult(@"C:\", root, null, [iso, mp4, zip], [zip], [new FileTypeTotal(FileTypeCategory.Archives, 6_000, 2), new FileTypeTotal(FileTypeCategory.Video, 3_000, 1)],
            [iso, mp4, zip], new DiskScanStats(3, 3, 0, 0, 0, TimeSpan.Zero, false), DateTimeOffset.Now);

        var after = DiskSpaceRules.WithoutRemoved(scan, [new RecycleRequest(@"C:\Downloads\Old", true, 2_000), new RecycleRequest(@"C:\Videos\c.mp4", false, 3_000)]);
        Assert.Equal(4_000, after.Root.TotalBytes);
        Assert.Equal([iso], after.LargestFiles);
        Assert.Empty(after.OldFiles);
        Assert.Equal([iso], after.DuplicateCandidates);
        Assert.Equal(4_000, after.Types.Single(t => t.Category == FileTypeCategory.Archives).Bytes);
        Assert.Equal(0, after.Types.Single(t => t.Category == FileTypeCategory.Video).Bytes);
    }

    // ---- formatting ----

    [Fact]
    public void Formatting_is_short_and_culture_stable()
    {
        Assert.Equal("42 %", DiskSpaceFormat.PercentText(42, 100));
        Assert.Equal("<1 %", DiskSpaceFormat.PercentText(1, 1_000));
        Assert.Equal("0 %", DiskSpaceFormat.PercentText(5, 0));
        Assert.Equal(100, DiskSpaceFormat.Percent(200, 100));
        Assert.Equal("1 file", DiskSpaceFormat.Count(1, "file", "files"));
        Assert.Equal("12,345 files", DiskSpaceFormat.Count(12_345, "file", "files"));
        var now = new DateTime(2026, 6, 15, 12, 0, 0);
        Assert.Equal("today", DiskSpaceFormat.Age(now.AddHours(-3), now));
        Assert.Equal("yesterday", DiskSpaceFormat.Age(now.AddDays(-1), now));
        Assert.Equal("10 days ago", DiskSpaceFormat.Age(now.AddDays(-10), now));
        Assert.Equal("5 months ago", DiskSpaceFormat.Age(new DateTime(2026, 1, 10), now));
        Assert.Equal("1 year ago", DiskSpaceFormat.Age(new DateTime(2025, 5, 1), now));
        Assert.Equal("3 years ago", DiskSpaceFormat.Age(new DateTime(2023, 1, 1), now));
        Assert.Equal((AgeUnit.Months, 5), DiskSpaceFormat.AgeOf(new DateTime(2026, 1, 10), now));
        Assert.Equal((AgeUnit.Today, 0), DiskSpaceFormat.AgeOf(now, now));
        Assert.Equal("12 s", DiskSpaceFormat.Duration(TimeSpan.FromSeconds(12.7)));
        Assert.Equal("2 min 5 s", DiskSpaceFormat.Duration(TimeSpan.FromSeconds(125)));
        Assert.Equal("312 GB free of 1.82 TB", DiskSpaceFormat.FreeOf(312L * 1024 * 1024 * 1024, 2_000_000_000_000));
        var shortened = DiskSpaceFormat.ShortenPath(@"C:\Users\Alex\AppData\Local\Packages\Microsoft.Windows.Photos_8wekyb3d8bbwe\LocalState", 40);
        Assert.Equal(40, shortened.Length);
        Assert.Contains("…", shortened);
        Assert.Equal("Archives and disk images", DiskSpaceFormat.CategoryName(FileTypeCategory.Archives));
    }

    // ---- real reading (temporary folders, any OS) ----

    [Fact]
    public void Scanner_measures_a_real_folder_tree_and_does_not_follow_links()
    {
        var dir = Directory.CreateTempSubdirectory("pcchecker-disk-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "a", "b"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "top.bin"), new byte[1_000]);
            File.WriteAllBytes(Path.Combine(dir.FullName, "a", "one.mp4"), new byte[3_000]);
            File.WriteAllBytes(Path.Combine(dir.FullName, "a", "b", "two.zip"), new byte[5_000]);
            var outside = Directory.CreateTempSubdirectory("pcchecker-outside-");
            File.WriteAllBytes(Path.Combine(outside.FullName, "huge.bin"), new byte[50_000]);
            Directory.CreateSymbolicLink(Path.Combine(dir.FullName, "link"), outside.FullName);

            var reports = new List<Common.ProgressUpdate>();
            var options = new DiskScanOptions { DuplicateMinBytes = 2_000 };
            var result = DiskTreeScanner.Scan(dir.FullName, options, null, null, new SyncProgress(reports), CancellationToken.None);

            Assert.Equal(9_000, result.Root.TotalBytes);
            Assert.Equal(3, result.Stats.Files);
            Assert.Equal(2, result.Stats.Folders);
            Assert.Equal(1, result.Stats.SkippedLinks);
            Assert.Equal(["two.zip", "one.mp4", "top.bin"], result.LargestFiles.Select(f => f.Name));
            Assert.Equal(2, result.DuplicateCandidates.Count);
            Assert.Equal(FileTypeCategory.Archives, result.Types[0].Category);
            Assert.Equal(5_000, DiskSpaceAggregation.Find(result.Root, Path.Combine(dir.FullName, "a", "b"))!.TotalBytes);
            Assert.Contains(reports, r => r.Stage == "Done");
            outside.Delete(true);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Scanner_stops_when_cancelled()
    {
        var dir = Directory.CreateTempSubdirectory("pcchecker-disk-");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => DiskTreeScanner.Scan(dir.FullName, DiskScanOptions.Default, null, null, null, cts.Token));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task Hasher_confirms_identical_content_and_rejects_files_that_differ_only_in_the_middle()
    {
        var dir = Directory.CreateTempSubdirectory("pcchecker-dupes-");
        try
        {
            var random = new Random(7);
            var content = new byte[300_000];
            random.NextBytes(content);
            var middle = (byte[])content.Clone();
            middle[150_000] ^= 0xFF; // same size, same first and last 64 KB
            var small = new byte[5_000];
            random.NextBytes(small);

            var paths = new[] { "a.bin", "copy of a.bin", "middle.bin", "s1.dat", "s2.dat", "other.dat" }.Select(n => Path.Combine(dir.FullName, n)).ToArray();
            File.WriteAllBytes(paths[0], content);
            File.WriteAllBytes(paths[1], content);
            File.WriteAllBytes(paths[2], middle);
            File.WriteAllBytes(paths[3], small);
            File.WriteAllBytes(paths[4], small);
            var otherSmall = (byte[])small.Clone();
            otherSmall[0] ^= 1;
            File.WriteAllBytes(paths[5], otherSmall);

            var candidates = paths.Select(p => new DiskFileEntry(p, new FileInfo(p).Length, DateTime.Now, null, FileTypeCategory.Other))
                .Append(new DiskFileEntry(Path.Combine(dir.FullName, "vanished.bin"), 300_000, DateTime.Now, null, FileTypeCategory.Other))
                .ToList();
            var result = await DuplicateHasher.FindAsync(candidates, 1_000, null, null, CancellationToken.None);

            Assert.Equal(2, result.Groups.Count);
            Assert.Equal(["a.bin", "copy of a.bin"], result.Groups[0].Files.Select(f => f.Name));
            Assert.Equal(300_000, result.Groups[0].WastedBytes);
            Assert.Equal(["s1.dat", "s2.dat"], result.Groups[1].Files.Select(f => f.Name));
            Assert.Equal(64, result.Groups[0].Hash.Length);
            Assert.Equal(1, result.FilesUnreadable);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    // ---- demo service ----

    [Fact]
    public async Task Demo_service_fills_the_page_and_simulates_recycling()
    {
        var svc = new DemoDiskSpaceService();
        var drives = await svc.GetDrivesAsync(CancellationToken.None);
        Assert.Equal(2, drives.Count);
        Assert.True(drives[0].IsSystemDrive);

        var scan = await svc.ScanAsync(@"C:\", DiskScanOptions.Default, null, CancellationToken.None);
        Assert.True(scan.Root.TotalBytes > 900L * 1024 * 1024 * 1024);
        Assert.True(scan.Root.TotalBytes <= drives[0].UsedBytes);
        Assert.NotEmpty(scan.LargestFiles);
        Assert.NotEmpty(scan.OldFiles);
        Assert.Contains(scan.Types, t => t.Category == FileTypeCategory.Games);
        Assert.True(scan.Stats.InaccessibleFolders > 0);

        var dupes = await svc.FindDuplicatesAsync(scan.DuplicateCandidates, DiskScanOptions.Default.DuplicateMinBytes, null, CancellationToken.None);
        Assert.True(dupes.Groups.Count > 100);
        Assert.All(dupes.Groups, g => Assert.True(g.Files.Count >= 2));

        var iso = scan.LargestFiles.Concat(scan.OldFiles).First(f => f.Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
        var recycle = await svc.RecycleAsync([new RecycleRequest(iso.Path, false, iso.SizeBytes), new RecycleRequest(@"C:\Windows", true, 1)], 0, null, CancellationToken.None);
        Assert.NotNull(recycle.Value);
        Assert.Equal(1, recycle.Value!.Recycled);
        Assert.Equal(1, recycle.Value.Failed);

        var again = await svc.ScanAsync(@"C:\", DiskScanOptions.Default, null, CancellationToken.None);
        Assert.DoesNotContain(again.LargestFiles.Concat(again.OldFiles), f => f.Path == iso.Path);
        Assert.Equal(scan.Root.TotalBytes - iso.SizeBytes, again.Root.TotalBytes);

        var folder = await svc.ScanAsync(@"C:\Users\Alex", DiskScanOptions.Default, null, CancellationToken.None);
        Assert.Equal(@"C:\Users\Alex", folder.RootPath);
        Assert.Null(folder.Drive);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => svc.ScanAsync(@"C:\Nope", DiskScanOptions.Default, null, CancellationToken.None));
    }

    private sealed class SyncProgress(List<Common.ProgressUpdate> sink) : IProgress<Common.ProgressUpdate>
    {
        public void Report(Common.ProgressUpdate value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
