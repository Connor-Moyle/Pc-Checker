using System.Text;
using PcChecker.Core.Leftovers;
using PcChecker.Core.RegistryHealth;
using PcChecker.Core.Software;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Uninstall leftovers: name matching, allow-list, path safety, scoring, backup naming and the task/registry rules.</summary>
public class LeftoversTests
{
    private const string Pf = @"C:\Program Files";
    private const string Pf86 = @"C:\Program Files (x86)";
    private const string Local = @"C:\Users\Alex\AppData\Local";
    private const string Roaming = @"C:\Users\Alex\AppData\Roaming";
    private const string LocalLow = @"C:\Users\Alex\AppData\LocalLow";

    private static readonly IReadOnlyList<LeftoverRootFolder> Roots =
    [
        new(LeftoverRoot.ProgramFiles, Pf),
        new(LeftoverRoot.ProgramFilesX86, Pf86),
        new(LeftoverRoot.ProgramData, @"C:\ProgramData"),
        new(LeftoverRoot.LocalAppData, Local),
        new(LeftoverRoot.RoamingAppData, Roaming),
        new(LeftoverRoot.LocalLowAppData, LocalLow),
    ];

    private static string? Env(string name) => name switch
    {
        "SystemRoot" => @"C:\Windows",
        "ProgramFiles" => Pf,
        "LOCALAPPDATA" => Local,
        _ => null,
    };

    // ---------------------------------------------------------------- name matching

    [Theory]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("Western Digital Technologies, Inc.", "westerndigitaltechnologies")]
    [InlineData("7-Zip 24.08 (x64)", "7zip")]
    [InlineData("Battle.net", "battlenet")]
    [InlineData("Driver Booster 12", "driverbooster")]
    [InlineData("Intel(R) Chipset Device Software", "intelchipsetdevicesoftware")]
    [InlineData("Café Studio GmbH", "cafestudio")]
    public void Names_are_normalised_without_versions_bitness_brackets_and_legal_forms(string name, string compact)
    {
        Assert.Equal(compact, LeftoverNameMatcher.Compact(name));
    }

    [Fact]
    public void CamelCase_names_are_split_into_words()
    {
        Assert.Equal(["video", "lan"], LeftoverNameMatcher.Tokens("VideoLAN"));
        Assert.Equal(["git", "for", "windows"], LeftoverNameMatcher.Tokens("GitForWindows"));
        Assert.Equal(["html", "help"], LeftoverNameMatcher.Tokens("HTMLHelp"));
        Assert.Empty(LeftoverNameMatcher.Tokens("  "));
    }

    [Theory]
    [InlineData("Epic Games", "Epic Games Launcher", true)]
    [InlineData("obs-studio", "OBS Studio", true)]
    [InlineData("Magician", "Samsung Magician", true)]
    [InlineData("Logitec", "Logitech", true)]
    [InlineData("Western Digital", "Western Digital Technologies, Inc.", true)]
    [InlineData("CCleaner", "Steam", false)]
    [InlineData("Free Media Converter", "Free Antivirus", false)]
    [InlineData("Piriform", "Valve Corporation", false)]
    public void Strong_matches_need_the_same_name_a_prefix_a_containment_or_a_typo(string candidate, string reference, bool expected)
    {
        Assert.Equal(expected, LeftoverNameMatcher.IsStrongMatch(candidate, reference));
    }

    [Fact]
    public void Loose_matches_use_shared_significant_words_and_acronyms_but_not_generic_words()
    {
        Assert.True(LeftoverNameMatcher.IsMatch("Corsair", "CORSAIR iCUE5 Software"));
        Assert.True(LeftoverNameMatcher.IsMatch("WD", "Western Digital"));
        Assert.True(LeftoverNameMatcher.IsMatch("Samsung", "Samsung Electronics"));
        Assert.False(LeftoverNameMatcher.IsMatch("Software Updater", "Avast Software"));
        Assert.False(LeftoverNameMatcher.IsMatch("TeamViewer", "Discord Inc."));
        Assert.True(LeftoverNameMatcher.IsGenericName("Common Files"));
        Assert.False(LeftoverNameMatcher.IsGenericName("Advanced SystemCare"));
    }

    [Fact]
    public void Edit_distance_stops_at_the_limit()
    {
        Assert.Equal(0, LeftoverNameMatcher.Distance("steam", "steam", 2));
        Assert.Equal(1, LeftoverNameMatcher.Distance("logitech", "logitec", 2));
        Assert.Equal(3, LeftoverNameMatcher.Distance("abc", "xyzuvw", 2));
    }

    // ---------------------------------------------------------------- installed program index

    [Fact]
    public void Program_index_owns_install_folders_their_vendor_folders_and_what_is_inside()
    {
        var programs = new[]
        {
            InstalledProgram.Create("m32:steam", "Steam", "Valve Corporation", installLocation: Pf86 + @"\Steam"),
            InstalledProgram.Create("m64:nv", "NVIDIA App", "NVIDIA Corporation", installLocation: Pf + @"\NVIDIA Corporation\NVIDIA App",
                uninstallString: "\"" + Pf + @"\NVIDIA Corporation\NVIDIA App\Uninstaller\NVIDIA App Uninstaller.exe" + "\""),
            InstalledProgram.Create("appx:spotify", "Spotify", "Spotify AB", source: SoftwareSource.StoreApp,
                packageFullName: "SpotifyAB.SpotifyMusic_1.249.0.0_x64__zpdnekdrzrea0"),
            // A broken entry that claims all of AppData\Local must not hide every folder in it.
            InstalledProgram.Create("usr:bad", "Broken Tool", "Nobody", installLocation: Local),
        };

        var index = new LeftoverProgramIndex(programs, Env, Roots.Select(r => r.Path));

        Assert.Equal(4, index.ProgramCount);
        Assert.True(index.OwnsPath(Pf86 + @"\Steam\steamapps"));
        Assert.True(index.OwnsPath(Pf + @"\NVIDIA Corporation"));
        Assert.True(index.ContainsInstall(Pf + @"\NVIDIA Corporation"));
        Assert.False(index.ContainsInstall(Pf86 + @"\Steam"));
        Assert.False(index.OwnsPath(Local + @"\CCleaner"));
        Assert.Equal("Valve Corporation", index.MatchName("Valve"));
        Assert.NotNull(index.MatchName("SpotifyMusic"));
        Assert.Null(index.MatchName("Piriform"));
    }

    // ---------------------------------------------------------------- allow-list

    [Theory]
    [InlineData(LeftoverRoot.ProgramFiles, "Microsoft Office", true)]
    [InlineData(LeftoverRoot.ProgramFiles, "Windows Defender", true)]
    [InlineData(LeftoverRoot.ProgramFiles, "Common Files", true)]
    [InlineData(LeftoverRoot.ProgramFilesX86, "NVIDIA Corporation", true)]
    [InlineData(LeftoverRoot.ProgramData, "Package Cache", true)]
    [InlineData(LeftoverRoot.ProgramData, "{8B1A2C3D-0000-4E5F-9A8B-7C6D5E4F3A2B}", true)]
    [InlineData(LeftoverRoot.LocalAppData, ".vscode", true)]
    [InlineData(LeftoverRoot.ProgramFilesX86, "Steam", true)]
    [InlineData(LeftoverRoot.RoamingAppData, "Mozilla", true)]
    [InlineData(LeftoverRoot.ProgramFiles, "Mozilla", false)]
    [InlineData(LeftoverRoot.ProgramFiles, "CCleaner", false)]
    [InlineData(LeftoverRoot.LocalAppData, "Razer", false)]
    public void Shared_and_system_folders_are_never_reported(LeftoverRoot root, string name, bool allowed)
    {
        Assert.Equal(allowed, LeftoverAllowList.IsAllowedFolder(root, name));
    }

    [Fact]
    public void Shared_vendor_subfolders_and_registry_vendors_are_allow_listed()
    {
        Assert.True(LeftoverAllowList.IsSharedInsideVendor("Updater"));
        Assert.True(LeftoverAllowList.IsSharedInsideVendor("Common"));
        Assert.True(LeftoverAllowList.IsSharedInsideVendor("Data"));
        Assert.False(LeftoverAllowList.IsSharedInsideVendor("Advanced SystemCare"));

        Assert.True(LeftoverAllowList.IsAllowedRegistryVendor("Microsoft"));
        Assert.True(LeftoverAllowList.IsAllowedRegistryVendor("Classes"));
        Assert.True(LeftoverAllowList.IsAllowedRegistryVendor("WOW6432Node"));
        Assert.True(LeftoverAllowList.IsAllowedRegistryVendor("Policies"));
        Assert.False(LeftoverAllowList.IsAllowedRegistryVendor("Piriform"));
    }

    // ---------------------------------------------------------------- path safety

    [Theory]
    [InlineData(Pf + @"\CCleaner", null)]
    [InlineData(Pf86 + @"\IObit\Advanced SystemCare", null)]
    [InlineData(LocalLow + @"\Razer", null)]
    [InlineData(Pf, "A whole system folder")]
    [InlineData(Pf + @"\Vendor\Product\Sub", "Only folders directly")]
    [InlineData(@"C:\Users\Alex\Documents\Old", "not inside")]
    [InlineData(@"D:\Games\Old", "not inside")]
    [InlineData(@"\\server\share\Program Files\X", "Network")]
    [InlineData(Pf + @"\..\Windows", "plain local path")]
    [InlineData(@"C:\PROGRA~1\CCleaner", "Short (8.3)")]
    [InlineData(Pf + @"\Microsoft Office", "shared by Windows")]
    [InlineData(Pf86 + @"\IObit\Updater", "shared by other programs")]
    [InlineData(Pf + @"\Cc*", "plain local path")]
    public void Folders_are_only_removed_one_or_two_levels_inside_the_listed_roots(string path, string? refusal)
    {
        var why = LeftoverPathRules.WhyFolderRefused(path, Roots);
        if (refusal is null)
        {
            Assert.Null(why);
        }
        else
        {
            Assert.NotNull(why);
            Assert.Contains(refusal, why, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_most_specific_root_is_used_so_LocalLow_is_not_taken_for_Local()
    {
        Assert.Equal(LeftoverRoot.LocalLowAppData, LeftoverPathRules.RootOf(LocalLow + @"\Unity Game", Roots)!.Root);
        Assert.Equal(LeftoverRoot.LocalAppData, LeftoverPathRules.RootOf(Local + @"\Razer", Roots)!.Root);
        Assert.Null(LeftoverPathRules.RootOf(@"C:\Windows\Temp", Roots));
        Assert.Equal([Pf86 + @"\IObit"], LeftoverPathRules.FoldersBetween(Pf86, Pf86 + @"\IObit\Advanced SystemCare"));
        Assert.Empty(LeftoverPathRules.FoldersBetween(Pf, Pf + @"\CCleaner"));
    }

    [Fact]
    public void Links_system_items_and_anything_inside_a_linked_folder_are_refused()
    {
        Assert.Null(LeftoverPathRules.WhyRefusedByAttributes(FileAttributes.Directory, [FileAttributes.Directory]));
        Assert.Contains("link", LeftoverPathRules.WhyRefusedByAttributes(FileAttributes.Directory | FileAttributes.ReparsePoint, []));
        Assert.Contains("linked folder", LeftoverPathRules.WhyRefusedByAttributes(FileAttributes.Directory, [FileAttributes.Directory | FileAttributes.ReparsePoint]));
        Assert.Contains("system", LeftoverPathRules.WhyRefusedByAttributes(FileAttributes.Directory | FileAttributes.System, []));
        Assert.NotNull(LeftoverPathRules.WhyRefusedByAttributes(FileAttributes.Archive | FileAttributes.Offline, []));
    }

    [Fact]
    public void Shortcuts_must_be_lnk_files_inside_a_Start_menu_folder()
    {
        string[] start = [@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs", Roaming + @"\Microsoft\Windows\Start Menu\Programs"];
        Assert.Null(LeftoverPathRules.WhyShortcutRefused(start[0] + @"\CCleaner\CCleaner.lnk", start));
        Assert.Null(LeftoverPathRules.WhyShortcutRefused(start[1] + @"\Zoom.lnk", start));
        Assert.NotNull(LeftoverPathRules.WhyShortcutRefused(start[0] + @"\CCleaner\CCleaner.exe", start));
        Assert.NotNull(LeftoverPathRules.WhyShortcutRefused(@"C:\Users\Alex\Desktop\Zoom.lnk", start));
        Assert.NotNull(LeftoverPathRules.WhyShortcutRefused(start[0] + @"\a\b\c\d\e\Deep.lnk", start));
    }

    // ---------------------------------------------------------------- confidence scoring

    [Fact]
    public void Confidence_is_High_only_when_the_scan_is_sure()
    {
        var old = TimeSpan.FromDays(400);
        Assert.Equal(LeftoverConfidence.High, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.EmptyFolder)));
        Assert.Equal(LeftoverConfidence.High, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.Folder, SinceLastChange: old)));
        Assert.Equal(LeftoverConfidence.Medium, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.Folder, SinceLastChange: TimeSpan.FromDays(60))));
        Assert.Equal(LeftoverConfidence.Medium, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.Folder, ContainsPrograms: true, SinceLastChange: old)));
        Assert.Equal(LeftoverConfidence.Medium, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.Folder, InUserData: true, SinceLastChange: old)));
        Assert.Equal(LeftoverConfidence.Low, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.Folder, SinceLastChange: TimeSpan.FromDays(3))));
        Assert.Equal(LeftoverConfidence.Low, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.Folder, UnderInstalledVendor: true, SinceLastChange: old)));
        Assert.Equal(LeftoverConfidence.High, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.Folder, MatchesFocus: true, SinceLastChange: TimeSpan.Zero)));
    }

    [Fact]
    public void Services_tasks_shortcuts_and_keys_are_scored_by_what_is_gone()
    {
        foreach (var kind in new[] { LeftoverKind.Service, LeftoverKind.ScheduledTask, LeftoverKind.Shortcut })
        {
            Assert.Equal(LeftoverConfidence.High, LeftoverScoring.Score(new LeftoverEvidence(kind, TargetFolderMissing: true)));
            Assert.Equal(LeftoverConfidence.Medium, LeftoverScoring.Score(new LeftoverEvidence(kind)));
        }

        Assert.Equal(LeftoverConfidence.High, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.RegistryKey, KeyIsEmpty: true)));
        Assert.Equal(LeftoverConfidence.Medium, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.RegistryKey)));
        Assert.Equal(LeftoverConfidence.Low, LeftoverScoring.Score(new LeftoverEvidence(LeftoverKind.RegistryKey, UnderInstalledVendor: true)));

        var text = LeftoverScoring.Explain(new LeftoverEvidence(LeftoverKind.ScheduledTask, MatchesFocus: true, TargetFolderMissing: true),
            @"C:\Program Files\CCleaner\CCUpdate.exe", "CCleaner");
        Assert.Contains("CCUpdate.exe", text);
        Assert.Contains("whole folder is gone", text);
        Assert.Contains("CCleaner, which you just uninstalled", text);
    }

    [Fact]
    public void Only_removable_High_findings_are_preselected()
    {
        var high = new LeftoverItem("a", LeftoverKind.EmptyFolder, "Origin", Pf86 + @"\Origin", "r", 0, LeftoverConfidence.High, CanRemove: true);
        Assert.True(high.IsPreselected);
        Assert.False((high with { Confidence = LeftoverConfidence.Medium }).IsPreselected);
        Assert.False((high with { CanRemove = false }).IsPreselected);
    }

    // ---------------------------------------------------------------- backup naming and seals

    [Fact]
    public void Task_backup_ids_are_readable_sortable_and_validated()
    {
        var created = new DateTimeOffset(2026, 10, 27, 14, 30, 5, TimeSpan.Zero);
        var id = LeftoverBackupNaming.TaskBackupId(created, @"\Opera GX scheduled Autoupdate 1712345678");
        Assert.StartsWith(LeftoverBackupNaming.TaskPrefix, id);
        Assert.EndsWith("-opera-gx-scheduled-autoupdate-1712345678", id);
        Assert.True(LeftoverBackupNaming.IsValidTaskBackupId(id));
        Assert.Equal(created.ToLocalTime().DateTime, LeftoverBackupNaming.CreatedFromId(id)!.Value.DateTime);

        Assert.Equal("unicode-task", LeftoverBackupNaming.Slug("Ünïcode   Task!!"));
        Assert.Equal("task", LeftoverBackupNaming.Slug("***"));
        Assert.True(LeftoverBackupNaming.Slug(new string('a', 100)).Length <= 40);

        Assert.False(LeftoverBackupNaming.IsValidTaskBackupId(@"leftover-task-..\..\evil"));
        Assert.False(LeftoverBackupNaming.IsValidTaskBackupId("registry-backup-20261027-143005"));
        Assert.False(LeftoverBackupNaming.IsValidTaskBackupId(null));

        var taken = new HashSet<string> { "x", "x-2" };
        Assert.Equal("x-3", LeftoverBackupNaming.Unique("x", taken.Contains));
        Assert.Equal("y", LeftoverBackupNaming.Unique("y", taken.Contains));
    }

    [Theory]
    [InlineData(@"\CCleaner Update", true)]
    [InlineData(@"\Opera\Opera GX Autoupdate", true)]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", false)]
    [InlineData(@"\PC Checker\health", false)]
    [InlineData(@"\..\Evil", false)]
    [InlineData(@"Relative", false)]
    [InlineData(@"\Folder\", false)]
    [InlineData(@"\Bad|Name", false)]
    public void Only_third_party_task_paths_may_be_removed(string path, bool expected)
    {
        Assert.Equal(expected, LeftoverBackupNaming.IsRemovableTaskPath(path));
    }

    [Fact]
    public void A_task_seal_binds_the_xml_to_its_task_name()
    {
        var xml = Encoding.Unicode.GetBytes("<Task />");
        var seal = LeftoverBackupNaming.Seal(xml, @"\CCleaner Update");
        Assert.True(LeftoverBackupNaming.SealMatches(xml, @"\CCleaner Update", seal));
        Assert.False(LeftoverBackupNaming.SealMatches(xml, @"\Other", seal));
        Assert.False(LeftoverBackupNaming.SealMatches(Encoding.Unicode.GetBytes("<Task></Task>"), @"\CCleaner Update", seal));
        Assert.False(LeftoverBackupNaming.SealMatches(xml, @"\CCleaner Update", null));
    }

    [Fact]
    public void Restore_list_round_trips_and_drops_entries_with_bad_ids()
    {
        var now = DateTimeOffset.Now;
        var entries = new[]
        {
            new LeftoverRestoreEntry("1", LeftoverRestoreKind.RegistryKeys, now, "2 registry keys", @"HKCU\Software\Piriform", "registry-backup-20261027-143005"),
            new LeftoverRestoreEntry("2", LeftoverRestoreKind.ScheduledTask, now.AddMinutes(1), "CCleaner Update", @"\CCleaner Update",
                LeftoverBackupNaming.TaskBackupId(now, @"\CCleaner Update"), Restored: now.AddMinutes(5)),
            new LeftoverRestoreEntry("3", LeftoverRestoreKind.ScheduledTask, now, "Evil", @"\Microsoft\Windows\Evil", "leftover-task-x"),
            new LeftoverRestoreEntry("4", LeftoverRestoreKind.RegistryKeys, now, "Evil", @"HKLM\SOFTWARE\X", @"..\..\evil"),
        };

        var back = LeftoverBackupNaming.DeserializeRestoreList(LeftoverBackupNaming.SerializeRestoreList(entries));
        Assert.Equal(["2", "1"], back.Select(e => e.Id));
        Assert.NotNull(back[0].Restored);
        Assert.Empty(LeftoverBackupNaming.DeserializeRestoreList("{ not json"));
        Assert.Empty(LeftoverBackupNaming.DeserializeRestoreList(null));
    }

    // ---------------------------------------------------------------- tasks, services, registry keys

    [Fact]
    public void Task_actions_are_read_from_the_xml_with_or_without_namespace()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>Piriform</Author></RegistrationInfo>
              <Actions Context="Author">
                <Exec><Command>"C:\Program Files\CCleaner\CCUpdate.exe"</Command><Arguments>/check</Arguments></Exec>
              </Actions>
            </Task>
            """;
        var actions = LeftoverTaskXml.ReadActions(xml)!;
        Assert.Equal([@"""C:\Program Files\CCleaner\CCUpdate.exe"""], actions.Commands);
        Assert.False(actions.HasOtherActions);
        Assert.Equal("Piriform", actions.Author);

        var com = LeftoverTaskXml.ReadActions("<Task><Actions><ComHandler><ClassId>{1}</ClassId></ComHandler></Actions></Task>")!;
        Assert.True(com.HasOtherActions);
        Assert.Null(LeftoverTaskXml.ReadActions("<NotATask />"));
        Assert.Null(LeftoverTaskXml.ReadActions("<!DOCTYPE Task [<!ENTITY x \"y\">]><Task>&x;</Task>"));
        Assert.Equal(xml.Trim(), LeftoverTaskXml.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(xml.Trim())]));
        Assert.Equal(@"\Vendor\Update", LeftoverTaskXml.TaskPathFromFile(@"C:\Windows\System32\Tasks", @"C:\Windows\System32\Tasks\Vendor\Update"));
    }

    [Fact]
    public void A_task_counts_only_when_every_program_it_starts_is_missing()
    {
        static string? Extract(string c) => c.Trim('"');
        var missing = new LeftoverTaskActions([@"C:\Gone\a.exe", @"C:\Gone\b.exe"], false, null);
        Assert.Equal(@"C:\Gone\a.exe", LeftoverRules.MissingTaskTarget(missing, Extract, _ => TargetState.Missing));

        var mixed = new LeftoverTaskActions([@"C:\Gone\a.exe", @"C:\Here\b.exe"], false, null);
        Assert.Null(LeftoverRules.MissingTaskTarget(mixed, Extract, p => p.Contains("Gone") ? TargetState.Missing : TargetState.Present));
        Assert.Null(LeftoverRules.MissingTaskTarget(missing with { HasOtherActions = true }, Extract, _ => TargetState.Missing));
        Assert.Null(LeftoverRules.MissingTaskTarget(missing, Extract, _ => TargetState.Unknown));
        Assert.Null(LeftoverRules.MissingTaskTarget(null, Extract, _ => TargetState.Missing));
    }

    [Fact]
    public void Services_are_deleted_only_when_stopped_with_their_program_missing_and_never_from_Windows()
    {
        Assert.True(LeftoverRules.CanDeleteService(imageMissing: true, "Stopped"));
        Assert.False(LeftoverRules.CanDeleteService(imageMissing: true, "Running"));
        Assert.False(LeftoverRules.CanDeleteService(imageMissing: true, null));
        Assert.False(LeftoverRules.CanDeleteService(imageMissing: false, "Stopped"));
        Assert.True(LeftoverRules.IsWindowsImage(@"C:\Windows\System32\gone.exe", @"C:\Windows"));
        Assert.False(LeftoverRules.IsWindowsImage(@"C:\Program Files\TeamViewer\TeamViewer_Service.exe", @"C:\Windows"));
    }

    [Theory]
    [InlineData("HKCU", @"Software\Piriform", null)]
    [InlineData("HKCU", @"Software\IObit\Advanced SystemCare", null)]
    [InlineData("HKLM", @"SOFTWARE\WOW6432Node\TeamViewer", null)]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Office", "never removed")]
    [InlineData("HKLM", @"SOFTWARE\Classes", "never removed")]
    [InlineData("HKLM", @"SOFTWARE\WOW6432Node", "never removed")]
    [InlineData("HKCU", @"Software\Piriform\CCleaner\Options", "one product key")]
    [InlineData("HKCU", @"Software\Vendor\Policies", "")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\X", "directly under Software")]
    [InlineData("HKU", @"S-1-5-21\Software\Piriform", "Only program settings")]
    [InlineData("HKCU", @"Software\..\Piriform", "not valid")]
    public void Registry_keys_are_removed_only_as_vendor_or_product_keys_under_Software(string hive, string path, string? refusal)
    {
        var why = LeftoverRules.WhyKeyRefused(hive, path);
        if (refusal is null)
        {
            Assert.Null(why);
        }
        else
        {
            Assert.NotNull(why);
            Assert.Contains(refusal, why, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Focus_matches_use_the_install_folder_or_a_close_name()
    {
        var focus = new LeftoverFocus("Free Media Converter", "SoftStack Ltd", Pf86 + @"\SoftStack\Free Media Converter");
        Assert.True(LeftoverRules.MatchesFocus("Free Media Converter", Pf86 + @"\SoftStack\Free Media Converter", focus));
        Assert.True(LeftoverRules.MatchesFocus("SoftStack", Pf86 + @"\SoftStack", focus));
        Assert.True(LeftoverRules.MatchesFocus("Free Media Converter", null, focus));
        Assert.True(LeftoverRules.MatchesFocus("SoftStack", null, focus));
        Assert.False(LeftoverRules.MatchesFocus("Common", null, focus));
        Assert.False(LeftoverRules.MatchesFocus("CCleaner", Pf + @"\CCleaner", focus));
        Assert.False(LeftoverRules.MatchesFocus("Free Media Converter", null, null));
    }

    [Fact]
    public void Summaries_and_split_keys_read_naturally()
    {
        var items = new[]
        {
            new LeftoverItem("1", LeftoverKind.Folder, "A", Pf + @"\A", "r", 1_500_000, LeftoverConfidence.High, true),
            new LeftoverItem("2", LeftoverKind.EmptyFolder, "B", Pf + @"\B", "r", 0, LeftoverConfidence.High, true),
            new LeftoverItem("3", LeftoverKind.RegistryKey, "C", @"HKCU\Software\C", "r", null, LeftoverConfidence.Medium, true),
        };
        Assert.Equal($"1 folder ({PcChecker.Core.Common.ByteSize.Format(1_500_000)}), 1 empty folder and 1 registry key", LeftoverRules.Summarize(items));
        Assert.Equal("nothing", LeftoverRules.Summarize([]));
        Assert.Equal(("HKCU", @"Software\C"), LeftoverRules.SplitKey(@"HKEY_CURRENT_USER\Software\C"));
        Assert.Null(LeftoverRules.SplitKey("Software"));
        Assert.Equal(LeftoverRules.IdFor(LeftoverKind.Folder, @"c:\x"), LeftoverRules.IdFor(LeftoverKind.EmptyFolder, @"C:\X"));

        var ordered = LeftoverRules.Order(items);
        Assert.Equal(LeftoverConfidence.Medium, ordered[^1].Confidence);
    }

    // ---------------------------------------------------------------- queue and demo service

    [Fact]
    public void Scan_queue_keeps_only_the_latest_request_and_announces_it()
    {
        var queue = new LeftoverScanQueue();
        int raised = 0;
        queue.Queued += (_, _) => raised++;
        queue.Enqueue(new LeftoverFocus("A", null, null));
        queue.Enqueue(new LeftoverFocus("B", null, null));
        Assert.Equal(2, raised);
        Assert.True(queue.HasPending);
        Assert.Equal("B", queue.Take()!.ProgramName);
        Assert.Null(queue.Take());
    }

    [Fact]
    public async Task Demo_leftovers_scan_remove_and_restore_without_touching_the_machine()
    {
        var svc = new DemoLeftoverService();
        var scan = await svc.ScanAsync(null, null, CancellationToken.None);
        Assert.Same(scan, svc.Last);
        Assert.True(scan.Items.Count >= 15);
        Assert.Contains(scan.Items, i => i.Kind == LeftoverKind.Service);
        Assert.Contains(scan.Items, i => i.Kind == LeftoverKind.ScheduledTask);
        Assert.Contains(scan.Items, i => i.Kind == LeftoverKind.Shortcut);
        Assert.Contains(scan.Items, i => i.Confidence == LeftoverConfidence.Low);
        Assert.All(scan.Items.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.EmptyFolder),
            i => Assert.Null(LeftoverPathRules.WhyFolderRefused(i.Location, Roots)));

        var focus = new LeftoverFocus("Free Media Converter", "SoftStack Ltd", Pf86 + @"\SoftStack\Free Media Converter", "m32:mediaconv");
        var focused = await svc.ScanAsync(focus, null, CancellationToken.None);
        var mine = focused.Items.Where(i => i.MatchesFocus).ToList();
        Assert.Equal(3, mine.Count);
        Assert.All(mine, i => Assert.Equal(LeftoverConfidence.High, i.Confidence));

        var selected = focused.Items.Where(i => i.IsPreselected).ToList();
        var removed = await svc.RemoveAsync(selected, 0, null, CancellationToken.None);
        Assert.True(removed.Success);
        Assert.Equal(selected.Count, removed.Value!.Removed);
        Assert.Contains(removed.Value.RestoreEntries, e => e.Kind == LeftoverRestoreKind.RegistryKeys);

        var after = await svc.ScanAsync(null, null, CancellationToken.None);
        Assert.DoesNotContain(after.Items, i => selected.Any(s => s.Id == i.Id));

        var entry = (await svc.ListRestoreEntriesAsync(CancellationToken.None))[0];
        Assert.True((await svc.RestoreAsync(entry.Id, CancellationToken.None)).Success);
        Assert.NotNull((await svc.ListRestoreEntriesAsync(CancellationToken.None)).First(e => e.Id == entry.Id).Restored);
    }
}
