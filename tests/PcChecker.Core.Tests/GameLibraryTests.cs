using System.Text;
using PcChecker.Core.Games;
using Xunit;

namespace PcChecker.Core.Tests;

public class GameLibraryTests
{
    // ----- VDF parser -----

    [Fact]
    public void Vdf_parser_reads_nested_blocks_with_any_key_case()
    {
        var root = VdfParser.Parse("""
            "AppState"
            {
                "appid"     "570"
                "UserConfig"
                {
                    "language"  "english"
                }
            }
            """);

        Assert.Equal("570", root["appstate"]?.Text("AppID"));
        Assert.Equal("english", root.Path("AppState", "userconfig", "LANGUAGE")?.Value);
        Assert.True(root["AppState"]!.IsBlock);
    }

    [Fact]
    public void Vdf_parser_handles_escaped_quotes_and_backslashes()
    {
        var root = VdfParser.Parse("""
            "x"
            {
                "name"  "The \"Best\" Game"
                "path"  "C:\\Games\\Steam"
                "odd"   "D:\Library"
            }
            """);

        Assert.Equal("The \"Best\" Game", root["x"]!.Text("name"));
        Assert.Equal(@"C:\Games\Steam", root["x"]!.Text("path"));
        Assert.Equal(@"D:\Library", root["x"]!.Text("odd"));
    }

    [Fact]
    public void Vdf_parser_skips_comments_conditions_and_includes()
    {
        var root = VdfParser.Parse("""
            #base "other.vdf"
            // a comment line
            "root"
            {
                "a"  "1"  // trailing comment
                "b"  "2"  [$WIN32]
                [$X360] "c" "3"
                bare  value
            }
            """);

        var block = root["root"]!;
        Assert.Equal("1", block.Text("a"));
        Assert.Equal("2", block.Text("b"));
        Assert.Equal("3", block.Text("c"));
        Assert.Equal("value", block.Text("bare"));
    }

    [Fact]
    public void Vdf_parser_keeps_what_it_read_from_a_damaged_file()
    {
        var root = VdfParser.Parse("\"root\" { \"a\" \"1\" \"b\" { \"c\" \"2\"");

        Assert.Equal("1", root["root"]?.Text("a"));
        Assert.Equal("2", root.Path("root", "b", "c")?.Value);
        Assert.Empty(VdfParser.Parse(null).Children);
    }

    // ----- Steam -----

    [Fact]
    public void Library_folders_new_format_lists_every_library_once()
    {
        var text = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "123" }
                }
                "1"
                {
                    "path"      "D:\\SteamLibrary"
                    "label"     ""
                }
            }
            """;

        var libraries = SteamLibraryRules.ParseLibraryFolders(text, @"C:\Program Files (x86)\Steam\");

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], libraries);
    }

    [Fact]
    public void Library_folders_old_format_reads_numbered_paths_and_skips_other_keys()
    {
        var text = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"   "1561832478"
                "ContentStatsID"        "-158337411110787451"
                "1"     "E:\\Games\\Steam"
                "2"     "F:/SteamLibrary/"
            }
            """;

        var libraries = SteamLibraryRules.ParseLibraryFolders(text);

        Assert.Equal([@"E:\Games\Steam", @"F:\SteamLibrary"], libraries);
    }

    [Fact]
    public void App_manifest_gives_name_folder_size_dates_and_state()
    {
        var manifest = SteamLibraryRules.ParseAppManifest("""
            "AppState"
            {
                "appid"         "1245620"
                "Universe"      "1"
                "name"          "ELDEN RING"
                "StateFlags"    "6"
                "installdir"    "ELDEN RING"
                "LastUpdated"   "1700000000"
                "SizeOnDisk"    "53476212736"
            }
            """);

        Assert.NotNull(manifest);
        Assert.Equal("1245620", manifest!.AppId);
        Assert.Equal("ELDEN RING", manifest.Name);
        Assert.Equal(53476212736, manifest.SizeOnDisk);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), manifest.LastUpdated);
        Assert.True(manifest.IsFullyInstalled);
        Assert.Equal("An update is waiting.", SteamLibraryRules.StateNote(manifest.StateFlags));
        Assert.Null(SteamLibraryRules.StateNote(4));
    }

    [Fact]
    public void App_manifest_without_an_id_or_folder_is_ignored()
    {
        Assert.Null(SteamLibraryRules.ParseAppManifest("\"AppState\" { \"name\" \"X\" \"installdir\" \"X\" }"));
        Assert.Null(SteamLibraryRules.ParseAppManifest("\"AppState\" { \"appid\" \"12; rm\" \"installdir\" \"X\" }"));
        Assert.Null(SteamLibraryRules.ParseAppManifest("not a manifest"));
    }

    [Theory]
    [InlineData("228980", "Steamworks Common Redistributables", true)]
    [InlineData("1493710", "Proton Experimental", true)]
    [InlineData("999", "Proton 8.0", true)]
    [InlineData("1070560", "Steam Linux Runtime", true)]
    [InlineData("1", "Some Redistributable Pack", true)]
    [InlineData("1245620", "ELDEN RING", false)]
    [InlineData("730", "Counter-Strike 2", false)]
    public void Steam_tools_and_redistributables_are_not_games(string appId, string name, bool isTool)
    {
        Assert.Equal(isTool, SteamLibraryRules.IsTool(appId, name));
    }

    [Fact]
    public void Last_played_is_read_per_app_and_the_latest_account_wins()
    {
        var userA = SteamLibraryRules.ParseLastPlayed("""
            "UserLocalConfigStore"
            {
                "Software" { "Valve" { "Steam" { "Apps"
                {
                    "570" { "LastPlayed" "1700000000" }
                    "730" { "LastPlayed" "0" }
                } } } }
            }
            """);
        var userB = SteamLibraryRules.ParseLastPlayed("""
            "UserLocalConfigStore" { "Software" { "valve" { "steam" { "apps" { "570" { "LastPlayed" "1710000000" } } } } } }
            """);

        Assert.Single(userA);
        var merged = SteamLibraryRules.MergeLastPlayed([userA, userB]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1710000000), merged["570"]);
    }

    [Fact]
    public void Shader_cache_clean_up_only_accepts_the_exact_steam_folder()
    {
        string[] libraries = [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"];

        Assert.True(SteamLibraryRules.IsShaderCacheFolder(@"D:\SteamLibrary\steamapps\shadercache\570", "570", libraries));
        Assert.True(SteamLibraryRules.IsShaderCacheFolder(@"c:/program files (x86)/steam/steamapps/shadercache/570/", "570", libraries));
        Assert.False(SteamLibraryRules.IsShaderCacheFolder(@"D:\SteamLibrary\steamapps\shadercache", "570", libraries));
        Assert.False(SteamLibraryRules.IsShaderCacheFolder(@"D:\SteamLibrary\steamapps\common\Dota", "570", libraries));
        Assert.False(SteamLibraryRules.IsShaderCacheFolder(@"D:\SteamLibrary\steamapps\shadercache\570\..\..", "570", libraries));
        Assert.False(SteamLibraryRules.IsShaderCacheFolder(@"E:\Other\steamapps\shadercache\570", "570", libraries));
    }

    [Fact]
    public void Steam_cover_candidates_cover_old_and_new_cache_layouts()
    {
        var candidates = SteamLibraryRules.CoverCandidates(@"C:\Steam", "570");

        Assert.Equal(@"C:\Steam\appcache\librarycache\570_library_600x900.jpg", candidates[0]);
        Assert.Contains(@"C:\Steam\appcache\librarycache\570\library_600x900.jpg", candidates);
    }

    // ----- Epic -----

    [Fact]
    public void Epic_manifest_is_read_and_builds_launch_and_verify_links()
    {
        var manifest = EpicManifestParser.Parse("""
            {
              "FormatVersion": 0,
              "bIsIncompleteInstall": false,
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
              "DisplayName": "Fortnite",
              "InstallLocation": "D:\\Epic Games\\Fortnite",
              "InstallSize": 33884872704,
              "AppName": "Fortnite",
              "CatalogNamespace": "fn",
              "CatalogItemId": "4fe75bbc5a674f4f9b356b5c90567da5",
              "AppCategories": ["public", "games", "applications"],
              "MainGameAppName": "Fortnite"
            }
            """);

        Assert.NotNull(manifest);
        Assert.Equal("Fortnite", manifest!.DisplayName);
        Assert.Equal(33884872704, manifest.InstallSize);
        Assert.False(manifest.IsAddOn);
        Assert.Equal(@"D:\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteLauncher.exe", manifest.ExePath);

        var game = GameEntryFactory.Epic(manifest);
        Assert.Equal("com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true", game.Play!.Target);
        Assert.Equal("com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=verify", game.Verify!.Target);
    }

    [Fact]
    public void Epic_add_ons_and_broken_files_are_skipped()
    {
        var dlc = EpicManifestParser.Parse("""
            { "DisplayName": "Some DLC", "InstallLocation": "D:\\Epic\\Game", "AppName": "dlc1", "MainGameAppName": "Game", "AppCategories": ["addons"] }
            """);

        Assert.True(dlc!.IsAddOn);
        Assert.Null(EpicManifestParser.Parse("{ not json"));
        Assert.Null(EpicManifestParser.Parse("{ \"DisplayName\": \"No folder\", \"AppName\": \"x\" }"));
    }

    // ----- Launch links -----

    [Fact]
    public void Launch_links_escape_or_refuse_unsafe_ids()
    {
        Assert.Equal("steam://rungameid/570", GameLaunchUris.SteamRun("570"));
        Assert.Null(GameLaunchUris.SteamRun("570 --evil"));
        Assert.Null(GameLaunchUris.SteamUninstall("../570"));
        Assert.Equal("com.epicgames.launcher://apps/a%20b%3Ac%26d%3Ae%3Fx?action=verify", GameLaunchUris.Epic("a b", "c&d", "e?x", "verify"));
        Assert.Null(GameLaunchUris.Epic("ns", "id", "app", "uninstall"));
        Assert.Equal("uplay://launch/5059/0", GameLaunchUris.UbisoftLaunch("5059"));
        Assert.Null(GameLaunchUris.UbisoftLaunch("5059/../x"));
        Assert.Equal("goggalaxy://openGameView/1207658924", GameLaunchUris.GogGameView("1207658924"));
        Assert.Null(GameLaunchUris.BattleNet("Pro&x"));
        Assert.Equal(@"shell:AppsFolder\Microsoft.624F8B84B80_8wekyb3d8bbwe!Game", GameLaunchUris.XboxApp("Microsoft.624F8B84B80_8wekyb3d8bbwe", "Game"));
        Assert.Null(GameLaunchUris.XboxApp("not a family name", "Game"));
    }

    // ----- Xbox -----

    [Fact]
    public void Publisher_id_matches_windows()
    {
        Assert.Equal("8wekyb3d8bbwe", XboxGameRules.PublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
    }

    [Fact]
    public void MicrosoftGame_config_gives_identity_name_program_and_logo()
    {
        var config = XboxGameRules.ParseConfig("""
            <?xml version="1.0" encoding="utf-8"?>
            <Game configVersion="1">
              <Identity Name="Microsoft.624F8B84B80" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" Version="3.0.0.0" />
              <ExecutableList>
                <Executable Name="ForzaHorizon5_Server.exe" Id="Server" TargetDeviceFamily="Desktop" />
                <Executable Name="ForzaHorizon5.exe" Id="Game" TargetDeviceFamily="PC" />
              </ExecutableList>
              <ShellVisuals DefaultDisplayName="Forza Horizon 5" PublisherDisplayName="Xbox Game Studios" StoreLogo="StoreLogo.png" />
            </Game>
            """);

        Assert.NotNull(config);
        Assert.Equal("Forza Horizon 5", config!.DisplayName);
        Assert.Equal("Microsoft.624F8B84B80_8wekyb3d8bbwe", config.PackageFamilyName);
        Assert.Equal("ForzaHorizon5.exe", config.MainExecutable!.Name);

        var game = GameEntryFactory.Xbox(config, @"C:\XboxGames\Forza Horizon 5\Content");
        Assert.Equal(@"shell:AppsFolder\Microsoft.624F8B84B80_8wekyb3d8bbwe!Game", game.Play!.Target);
        Assert.Equal(@"C:\XboxGames\Forza Horizon 5\Content\StoreLogo.png", game.CoverImagePath);
        Assert.Equal("C:", game.Drive);
    }

    [Fact]
    public void MicrosoftGame_config_with_a_resource_name_or_no_identity_is_handled()
    {
        var config = XboxGameRules.ParseConfig("""
            <Game><Identity Name="A.B" Publisher="CN=X" /><ShellVisuals DefaultDisplayName="ms-resource:Title" /></Game>
            """);

        Assert.Null(config!.DisplayName);
        Assert.Null(XboxGameRules.ParseConfig("<Game><ShellVisuals DefaultDisplayName=\"X\" /></Game>"));
        Assert.Null(XboxGameRules.ParseConfig("<NotAGame />"));
        Assert.Null(XboxGameRules.ParseConfig("<Game"));
    }

    [Fact]
    public void Gaming_root_marker_lists_the_xbox_games_folders()
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("RGBX"));
        bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(Encoding.Unicode.GetBytes("XboxGames\0"));

        Assert.Equal(["XboxGames"], XboxGameRules.ParseGamingRoot(bytes.ToArray()));
        Assert.Empty(XboxGameRules.ParseGamingRoot(Encoding.ASCII.GetBytes("nope nope nope")));
    }

    // ----- Other launchers -----

    [Fact]
    public void Riot_uninstall_command_gives_product_and_launch_arguments()
    {
        var product = LauncherRules.ParseRiotUninstall(
            "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=valorant --uninstall-patchline=live");

        Assert.NotNull(product);
        Assert.Equal(@"C:\Riot Games\Riot Client\RiotClientServices.exe", product!.ClientPath);
        Assert.Equal("--launch-product=valorant --launch-patchline=live", LauncherRules.RiotLaunchArguments(product));
        Assert.Null(LauncherRules.ParseRiotUninstall("\"C:\\Other\\uninstall.exe\" /S"));
    }

    [Fact]
    public void Gog_entries_map_and_dlc_is_skipped()
    {
        var game = LauncherRules.MapGog("1207658924", new Dictionary<string, string?>
        {
            ["gameName"] = "Unreal Tournament 2004",
            ["path"] = @"C:\GOG Games\UT2004",
            ["exe"] = @"C:\GOG Games\UT2004\System\UT2004.exe",
            ["gameID"] = "1207658924",
        });
        var dlc = LauncherRules.MapGog("1", new Dictionary<string, string?> { ["path"] = @"C:\GOG Games\X", ["dependsOn"] = "1207658924" });

        Assert.Equal("Unreal Tournament 2004", game!.Name);
        Assert.Null(dlc);
        var entry = GameEntryFactory.Gog(game, galaxyInstalled: true);
        Assert.Equal(GameCommandKind.Program, entry.Play!.Kind);
        Assert.Equal("goggalaxy://openGameView/1207658924", entry.Verify!.Target);
        Assert.Equal(GameCommandKind.AppsPage, GameEntryFactory.Gog(game, galaxyInstalled: false).Uninstall!.Kind);
    }

    [Fact]
    public void Ea_installer_data_gives_title_and_program_without_the_registry_placeholder()
    {
        var data = LauncherRules.ParseEaInstallerData("""
            <DiPManifest version="4.0">
              <gameTitles>
                <gameTitle locale="de_DE">Schlachtfeld</gameTitle>
                <gameTitle locale="en_US">Battlefield 2042</gameTitle>
              </gameTitles>
              <contentIDs><contentID>196216</contentID></contentIDs>
              <runtime>
                <launcher><filePath>[HKEY_LOCAL_MACHINE\SOFTWARE\EA Games\Battlefield 2042\Install Dir]BF2042_Trial.exe</filePath><trial>true</trial></launcher>
                <launcher><filePath>[HKEY_LOCAL_MACHINE\SOFTWARE\EA Games\Battlefield 2042\Install Dir]BF2042.exe</filePath><trial>false</trial></launcher>
              </runtime>
            </DiPManifest>
            """);

        Assert.Equal("Battlefield 2042", data!.Title);
        Assert.Equal(["196216"], data.ContentIds);
        Assert.Equal("BF2042.exe", data.ExeRelativePath);
    }

    [Fact]
    public void Uninstall_entry_rules_keep_launchers_and_steam_copies_out()
    {
        Assert.True(LauncherRules.IsEaGameEntry("Electronic Arts", "Battlefield 2042"));
        Assert.False(LauncherRules.IsEaGameEntry("Electronic Arts", "EA app"));
        Assert.True(LauncherRules.IsBlizzardGameEntry("Blizzard Entertainment", "Overwatch"));
        Assert.False(LauncherRules.IsBlizzardGameEntry("Blizzard Entertainment", "Battle.net"));
        Assert.False(LauncherRules.IsBlizzardGameEntry("Activision", "Call of Duty", "\"C:\\Program Files\\Steam\\steam.exe\" steam://uninstall/1938090"));
        Assert.True(LauncherRules.IsSteamUninstallEntry("Steam App 1238810", null));
        Assert.Equal("Pro", LauncherRules.BattleNetCode("Overwatch"));
        Assert.Equal("5059", LauncherRules.UbisoftIdFromUninstallKey("Uplay Install 5059"));
        Assert.Equal(@"C:\Games\X\x.exe", LauncherRules.ExeFromDisplayIcon("\"C:\\Games\\X\\x.exe\",0", @"C:\Games\X"));
        Assert.Null(LauncherRules.ExeFromDisplayIcon(@"C:\Games\X\unins000.exe", @"C:\Games\X"));
        Assert.Null(LauncherRules.ExeFromDisplayIcon(@"C:\Elsewhere\x.exe", @"C:\Games\X"));
    }

    [Fact]
    public void Exe_guesser_picks_the_game_and_never_a_helper()
    {
        ExeCandidate[] files =
        [
            new("unins000.exe", 3_000_000),
            new(@"_CommonRedist\vcredist\vc_redist.x64.exe", 25_000_000),
            new("UnityCrashHandler64.exe", 1_500_000),
            new(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", 2_000_000),
            new("Hades2.exe", 60_000_000),
            new(@"Tools\ModEditor.exe", 90_000_000),
        ];

        Assert.Equal("Hades2.exe", GameExeGuesser.Pick("Hades II", "Hades II", files));
        Assert.Null(GameExeGuesser.Pick("X", "X", [new ExeCandidate("unins000.exe", 1)]));
    }

    // ----- Totals, sorting, filtering -----

    [Fact]
    public void Totals_add_up_per_drive_and_count_unknown_sizes()
    {
        var games = new[]
        {
            Game("a", "Alpha", @"C:\Games\A", 10),
            Game("b", "Beta", @"D:\Games\B", 30),
            Game("c", "Gamma", @"d:\Games\C", 5),
            Game("d", "Delta", @"D:\Games\D", null),
        };

        var totals = GameLibraryMath.Totals(games);

        Assert.Equal(4, totals.GameCount);
        Assert.Equal(45, totals.SizeBytes);
        Assert.Equal(1, totals.UnknownSizeCount);
        Assert.Equal("D:", totals.Drives[0].Drive);
        Assert.Equal(3, totals.Drives[0].GameCount);
        Assert.Equal(35, totals.Drives[0].SizeBytes);
    }

    [Fact]
    public void Sorting_puts_unknowns_last_and_ignores_a_leading_the()
    {
        var now = DateTimeOffset.UtcNow;
        var games = new[]
        {
            Game("a", "The Witcher 3", @"C:\A", 50, now.AddDays(-3)),
            Game("b", "Alan Wake 2", @"C:\B", null, null),
            Game("c", "Zelda-like", @"C:\C", 90, now.AddDays(-1)),
        };

        Assert.Equal(["Alan Wake 2", "The Witcher 3", "Zelda-like"], GameLibraryMath.Sort(games, GameSortOrder.Name).Select(g => g.Name));
        Assert.Equal(["c", "a", "b"], GameLibraryMath.Sort(games, GameSortOrder.Size).Select(g => g.Id));
        Assert.Equal(["c", "a", "b"], GameLibraryMath.Sort(games, GameSortOrder.LastPlayed).Select(g => g.Id));
        Assert.Equal("c", GameLibraryMath.MostRecentlyPlayed(games)!.Id);
    }

    [Fact]
    public void Filters_combine_launcher_drive_and_search()
    {
        var games = DemoGameLibraryService.BuildGames(DateTimeOffset.Now);

        Assert.Equal(18, games.Count);
        Assert.Equal(2, games.Select(g => g.Drive).Distinct().Count());
        Assert.All(GameLibraryMath.Filter(games, GameLauncher.Steam, "D:", null), g => Assert.True(g.Launcher == GameLauncher.Steam && g.Drive == "D:"));
        Assert.Single(GameLibraryMath.Filter(games, null, null, "elden"));
        Assert.Equal(3, GameLibraryMath.Filter(games, null, null, "epic games").Count);
    }

    [Fact]
    public void Duplicates_from_two_sources_merge_into_one_game()
    {
        var first = Game("ea:bf", "Battlefield 2042", @"C:\EA\BF2042", null) with { Launcher = GameLauncher.Ea };
        var second = Game("ea:bf2", "Battlefield 2042", @"c:\ea\bf2042\", 100) with { Launcher = GameLauncher.Ea, ExePath = @"C:\EA\BF2042\BF2042.exe" };

        var merged = GameLibraryMath.MergeDuplicates([first, second]);

        Assert.Single(merged);
        Assert.Equal(100, merged[0].SizeBytes);
        Assert.Equal(@"C:\EA\BF2042\BF2042.exe", merged[0].ExePath);
    }

    [Fact]
    public void Days_since_last_played_counts_calendar_days()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(GameLibraryMath.DaysSince(null, now));
        Assert.Equal(0, GameLibraryMath.DaysSince(now.AddMinutes(-5), now));
        Assert.Equal(10, GameLibraryMath.DaysSince(now.AddDays(-10), now));
        Assert.Equal(0, GameLibraryMath.DaysSince(now.AddDays(2), now));
    }

    [Fact]
    public void Paths_join_with_backslashes_on_any_os()
    {
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Hades", GameLibraryMath.JoinPath(@"D:\SteamLibrary\", "steamapps", "common", "Hades"));
        Assert.Equal(@"C:\XboxGames", GameLibraryMath.JoinPath("C:", "XboxGames"));
        Assert.Equal("D:", GameLibraryMath.DriveOf(@"d:\x"));
        Assert.Null(GameLibraryMath.DriveOf(@"\\server\share"));
        Assert.Equal("Hades", GameLibraryMath.LastSegment(@"C:\Games\Hades\"));
    }

    [Fact]
    public void Hand_off_keeps_only_the_latest_request()
    {
        var handOff = new GameFolderScanHandOff();
        int raised = 0;
        handOff.Queued += (_, _) => raised++;

        handOff.Enqueue(new GameFolderScanRequest(@"C:\A", "A"));
        handOff.Enqueue(new GameFolderScanRequest(@"C:\B", "B"));

        Assert.Equal(2, raised);
        Assert.Equal(@"C:\B", handOff.Take()!.Folder);
        Assert.Null(handOff.Take());
    }

    private static InstalledGame Game(string id, string name, string folder, long? size, DateTimeOffset? played = null) =>
        new(id, GameLauncher.Steam, id, name, folder, size, played, null, null, null, null, null, null);
}
