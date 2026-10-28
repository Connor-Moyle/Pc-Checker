using PcChecker.Core.Common;
using PcChecker.Core.GameRuntimes;

namespace PcChecker.Core.Games;

/// <summary>
/// Demo mode: eighteen games across Steam, Epic, GOG, Xbox, Ubisoft and the EA app on the demo PC's two drives, built
/// with the same factory as the real scan. Nothing is started, removed or measured.
/// </summary>
public sealed class DemoGameLibraryService : IGameLibraryService
{
    private const long GB = 1024L * 1024 * 1024;
    private const long MB = 1024L * 1024;
    private const string SteamC = @"C:\Program Files (x86)\Steam";
    private const string SteamD = @"D:\SteamLibrary";

    public async Task<GameLibraryScan> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading Steam libraries", 20));
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Reading Epic, GOG, EA and Ubisoft games", 60));
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Reading Xbox games", 90));
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        var games = BuildGames(DateTimeOffset.Now);
        var launchers = Enum.GetValues<GameLauncher>()
            .Select(l => new LauncherSummary(l, l is not (GameLauncher.BattleNet or GameLauncher.Riot), games.Count(g => g.Launcher == l)))
            .ToList();
        return new GameLibraryScan(games, launchers, [], DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<InstalledGame>> MeasureMissingSizesAsync(IReadOnlyList<InstalledGame> games, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(games);
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        return games.Select(g => g.SizeBytes is null ? g with { SizeBytes = DemoSize(g) } : g).ToList();
    }

    public async Task<OperationResult> RunAsync(GameCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Demo mode: nothing was started.");
    }

    public async Task<OperationResult> RequestUninstallAsync(InstalledGame game, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"Demo mode: {GameLibraryMath.LauncherName(game.Launcher)} was not asked to remove {game.Name}.");
    }

    public async Task<OperationResult<long>> CleanShaderCacheAsync(InstalledGame game, nint ownerWindow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        long bytes = game.ShaderCacheBytes ?? 0;
        return OperationResult<long>.Ok(bytes, $"Demo mode: {ByteSize.Format(bytes)} of shader cache would have gone to the Recycle Bin.");
    }

    public OperationResult OpenFolder(InstalledGame game) => OperationResult.Ok("Demo mode: no folder was opened.");

    /// <summary>The demo library, with last-played times relative to <paramref name="now"/>.</summary>
    public static IReadOnlyList<InstalledGame> BuildGames(DateTimeOffset now)
    {
        DateTimeOffset Ago(double days) => now.AddDays(-days);
        SteamAppManifest Manifest(string id, string name, string dir, double sizeGb, double updatedDaysAgo, int flags = 4) =>
            new(id, name, dir, (long)(sizeGb * GB), Ago(updatedDaysAgo), null, flags);

        var games = new List<InstalledGame>
        {
            GameEntryFactory.Steam(Manifest("1091500", "Cyberpunk 2077", "Cyberpunk 2077", 70.2, 12), SteamC, Ago(1.2),
                SteamC + @"\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", shaderCacheBytes: 1240 * MB),
            GameEntryFactory.Steam(Manifest("1245620", "ELDEN RING", "ELDEN RING", 49.8, 30), SteamC, Ago(0.1),
                SteamC + @"\steamapps\common\ELDEN RING\Game\eldenring.exe", shaderCacheBytes: 610 * MB),
            GameEntryFactory.Steam(Manifest("730", "Counter-Strike 2", "Counter-Strike Global Offensive", 33.5, 2), SteamC, Ago(2.4),
                SteamC + @"\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", shaderCacheBytes: 2150 * MB),
            GameEntryFactory.Steam(Manifest("1086940", "Baldur's Gate 3", "Baldurs Gate 3", 128.4, 21), SteamD, Ago(5),
                SteamD + @"\steamapps\common\Baldurs Gate 3\bin\bg3.exe", shaderCacheBytes: 480 * MB),
            GameEntryFactory.Steam(Manifest("1145350", "Hades II", "Hades II", 9.4, 9), SteamD, Ago(23),
                SteamD + @"\steamapps\common\Hades II\Ship\Hades2.exe", shaderCacheBytes: 95 * MB),
            GameEntryFactory.Steam(Manifest("1174180", "Red Dead Redemption 2", "Red Dead Redemption 2", 121.3, 140, 6), SteamD, Ago(118),
                SteamD + @"\steamapps\common\Red Dead Redemption 2\RDR2.exe", shaderCacheBytes: 830 * MB),
            GameEntryFactory.Steam(Manifest("413150", "Stardew Valley", "Stardew Valley", 0.7, 60), SteamD, Ago(64),
                SteamD + @"\steamapps\common\Stardew Valley\Stardew Valley.exe", shaderCacheBytes: 12 * MB),
            GameEntryFactory.Steam(Manifest("2001330", "Midnight Rally 2009", "Midnight Rally 2009", 4.1, 400), @"C:\Games\Steam", Ago(410),
                DemoGameRuntimeService.DemoGameFolder + @"\MidnightRally.exe"),

            GameEntryFactory.Epic(new EpicManifest("Fortnite", @"D:\Epic Games\Fortnite", (long)(31.6 * GB), "Fortnite", "fn",
                "4fe75bbc5a674f4f9b356b5c90567da5", @"FortniteGame\Binaries\Win64\FortniteLauncher.exe", false, false)),
            GameEntryFactory.Epic(new EpicManifest("Alan Wake 2", @"D:\Epic Games\AlanWake2", (long)(89.3 * GB), "Dill", "dill",
                "c4763f236d08423eb47b4c3008779c84", "AlanWake2.exe", false, false)),
            GameEntryFactory.Epic(new EpicManifest("Hogwarts Legacy", @"D:\Epic Games\HogwartsLegacy", (long)(79.1 * GB), "fa4240e57a3c46b39f169041b7811293",
                "e97659b501af4e3981d5430dad6edb05", "fa4240e57a3c46b39f169041b7811293", "HogwartsLegacy.exe", true, false)),

            GameEntryFactory.Gog(new GogGameEntry("1495134320", "The Witcher 3: Wild Hunt - Complete Edition", @"D:\GOG Games\The Witcher 3 Wild Hunt GOTY",
                @"D:\GOG Games\The Witcher 3 Wild Hunt GOTY\bin\x64_dx12\witcher3.exe", null, null), galaxyInstalled: true, sizeBytes: (long)(56.2 * GB)),
            GameEntryFactory.Gog(new GogGameEntry("1771589310", "Disco Elysium - The Final Cut", @"D:\GOG Games\Disco Elysium",
                @"D:\GOG Games\Disco Elysium\disco.exe", null, null), galaxyInstalled: true, sizeBytes: null),

            DemoXbox("Microsoft.624F8B84B80", "Forza Horizon 5", "ForzaHorizon5.exe", 134.7),
            DemoXbox("BethesdaSoftworks.ProjectGold", "Starfield", "Starfield.exe", 125.9),
            DemoXbox("Microsoft.254428597CFE2", "Halo Infinite", "HaloInfinite.exe", 48.3),

            GameEntryFactory.Ubisoft("18265", "Assassin's Creed Mirage", @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Assassin's Creed Mirage",
                (long)(41.8 * GB), @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Assassin's Creed Mirage\ACMirage.exe"),
            GameEntryFactory.Ea("Battlefield 2042", @"C:\Program Files\EA Games\Battlefield 2042", @"C:\Program Files\EA Games\Battlefield 2042\BF2042.exe",
                @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EALauncher.exe", (long)(102.4 * GB)),
        };
        return games;
    }

    private static InstalledGame DemoXbox(string identity, string name, string exe, double sizeGb)
    {
        var config = new XboxGameConfig(identity, "CN=A4954634-DF4B-47C7-AB70-D3215D246AF1", name, [new XboxExecutable(exe, "Game", "PC")], "StoreLogo.png");
        return GameEntryFactory.Xbox(config, $@"C:\XboxGames\{name}\Content", name, (long)(sizeGb * GB));
    }

    private static long DemoSize(InstalledGame game) =>
        (long)((8 + (Math.Abs(StableHash(game.Id)) % 40)) * (double)GB);

    private static int StableHash(string text)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in text)
            {
                hash = (hash * 31) + c;
            }

            return hash == int.MinValue ? 0 : hash;
        }
    }
}
