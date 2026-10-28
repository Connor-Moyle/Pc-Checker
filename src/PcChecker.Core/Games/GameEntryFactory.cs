namespace PcChecker.Core.Games;

/// <summary>
/// Turns what each launcher records into an <see cref="InstalledGame"/> with the right Play, Verify and Uninstall
/// actions. Pure, so the real scan and demo mode build games the same way. Actions a launcher has no link for say what
/// to do in the launcher instead, or send the user to the Apps page.
/// </summary>
public static class GameEntryFactory
{
    private const string AppsPageUninstall = "This game is removed from the Apps page. PC Checker opens it; find the game there and choose Uninstall.";

    /// <summary>A Steam game from its app manifest and the library it is in.</summary>
    public static InstalledGame Steam(SteamAppManifest manifest, string libraryPath, DateTimeOffset? lastPlayed, string? exePath = null,
        string? coverPath = null, long? shaderCacheBytes = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var folder = GameLibraryMath.JoinPath(libraryPath, "steamapps", "common", manifest.InstallDir);
        var id = manifest.AppId;
        return new InstalledGame(
            "steam:" + id,
            GameLauncher.Steam,
            id,
            manifest.Name,
            folder,
            manifest.SizeOnDisk is > 0 ? manifest.SizeOnDisk : null,
            Latest(lastPlayed, manifest.LastPlayed),
            manifest.LastUpdated,
            exePath,
            coverPath,
            Link(GameLaunchUris.SteamRun(id), "Steam starts the game, after any update it needs."),
            Link(GameLaunchUris.SteamValidate(id), "Steam checks every file and downloads the ones that are missing or damaged. It can take a while for big games."),
            Link(GameLaunchUris.SteamUninstall(id), "Steam asks you to confirm, then removes the game. Your cloud saves stay in your Steam account."),
            SteamLibraryRules.ShaderCacheFolder(libraryPath, id),
            shaderCacheBytes,
            SteamLibraryRules.StateNote(manifest.StateFlags));
    }

    /// <summary>An Epic Games Store game from its launcher manifest.</summary>
    public static InstalledGame Epic(EpicManifest manifest, string? coverPath = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var verifyNote = "The Epic Games Launcher checks the game's files and repairs any that are damaged.";
        return new InstalledGame(
            "epic:" + manifest.AppName,
            GameLauncher.Epic,
            manifest.AppName,
            manifest.DisplayName,
            manifest.InstallLocation,
            manifest.InstallSize is > 0 ? manifest.InstallSize : null,
            null,
            null,
            manifest.ExePath,
            coverPath,
            Link(GameLaunchUris.Epic(manifest.CatalogNamespace, manifest.CatalogItemId, manifest.AppName, "launch"), "The Epic Games Launcher starts the game."),
            Link(GameLaunchUris.Epic(manifest.CatalogNamespace, manifest.CatalogItemId, manifest.AppName, "verify"), verifyNote),
            GameCommand.Link(GameLaunchUris.EpicLauncher,
                "The Epic Games Launcher opens. In Library, click the three dots on the game and choose Uninstall."),
            Note: manifest.IsIncompleteInstall ? "Not fully installed yet." : null);
    }

    /// <summary>A GOG game from its registry entry. With GOG Galaxy installed, Verify and Uninstall open the game's page there.</summary>
    public static InstalledGame Gog(GogGameEntry entry, bool galaxyInstalled, long? sizeBytes = null, string? coverPath = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var view = galaxyInstalled ? GameLaunchUris.GogGameView(entry.GameId) : null;
        GameCommand? play = entry.ExePath is not null
            ? GameCommand.Program(entry.ExePath, entry.LaunchArguments, entry.WorkingDirectory ?? FolderOf(entry.ExePath), "The game starts directly.")
            : Link(view, "GOG Galaxy opens the game's page; press Play.");
        return new InstalledGame(
            "gog:" + entry.GameId,
            GameLauncher.Gog,
            entry.GameId,
            entry.Name,
            entry.InstallPath,
            sizeBytes,
            null,
            null,
            entry.ExePath,
            coverPath,
            play,
            Link(view, "GOG Galaxy opens the game's page. Click the settings button next to Play, then Manage installation > Verify / Repair."),
            view is null
                ? GameCommand.AppsPage(AppsPageUninstall)
                : GameCommand.Link(view, "GOG Galaxy opens the game's page. Click the settings button next to Play, then Manage installation > Uninstall."));
    }

    /// <summary>An Xbox app or Microsoft Store game from its MicrosoftGame.config and the folder it is in.</summary>
    public static InstalledGame Xbox(XboxGameConfig config, string contentFolder, string? displayName = null, long? sizeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var pfn = config.PackageFamilyName;
        var exe = config.MainExecutable;
        var logo = string.IsNullOrWhiteSpace(config.StoreLogo) || config.StoreLogo.Contains("..", StringComparison.Ordinal)
            ? null
            : GameLibraryMath.JoinPath(contentFolder, config.StoreLogo);
        const string settingsNote = "Windows Settings opens the list of installed apps. Find the game and click the three dots next to it. ";
        return new InstalledGame(
            "xbox:" + pfn,
            GameLauncher.Xbox,
            pfn,
            config.DisplayName ?? displayName ?? config.IdentityName,
            contentFolder,
            sizeBytes,
            null,
            null,
            exe is null ? null : GameLibraryMath.JoinPath(contentFolder, exe.Name),
            logo,
            Link(GameLaunchUris.XboxApp(pfn, exe?.Id ?? "Game"), "Windows starts the game as you."),
            GameCommand.Link(GameLaunchUris.InstalledAppsSettings, settingsNote + "Choose Advanced options, then Repair."),
            GameCommand.Link(GameLaunchUris.InstalledAppsSettings, settingsNote + "Choose Uninstall."));
    }

    /// <summary>A Ubisoft Connect game from its install id and folder.</summary>
    public static InstalledGame Ubisoft(string installId, string name, string folder, long? sizeBytes = null, string? exePath = null)
    {
        var launch = GameLaunchUris.UbisoftLaunch(installId);
        return new InstalledGame(
            "ubisoft:" + installId,
            GameLauncher.Ubisoft,
            installId,
            name,
            SteamLibraryRules.NormalizeFolder(folder),
            sizeBytes,
            null,
            null,
            exePath,
            null,
            Link(launch, "Ubisoft Connect starts the game."),
            GameCommand.Link(GameLaunchUris.UbisoftLauncher, "Ubisoft Connect opens. Open the game, choose Properties, then Verify files."),
            GameCommand.AppsPage(AppsPageUninstall));
    }

    /// <summary>An EA app game. <paramref name="eaAppPath"/> is the EA app's program, when found.</summary>
    public static InstalledGame Ea(string name, string folder, string? exePath, string? eaAppPath, long? sizeBytes = null)
    {
        var key = GameExeGuesser.Simplify(GameLibraryMath.LastSegment(folder));
        GameCommand? openApp = eaAppPath is null ? null : GameCommand.Program(eaAppPath, null, FolderOf(eaAppPath), null);
        return new InstalledGame(
            "ea:" + (key.Length > 0 ? key : GameExeGuesser.Simplify(name)),
            GameLauncher.Ea,
            name,
            name,
            SteamLibraryRules.NormalizeFolder(folder),
            sizeBytes,
            null,
            null,
            exePath,
            null,
            exePath is not null
                ? GameCommand.Program(exePath, null, FolderOf(exePath), "The game starts; the EA app may open first to sign you in.")
                : openApp is null ? null : openApp with { Note = "The EA app opens; press Play on the game." },
            openApp is null ? null : openApp with { Note = "The EA app opens. In your library, open the game, click Manage, then Repair." },
            GameCommand.AppsPage(AppsPageUninstall));
    }

    /// <summary>A Battle.net game from its Uninstall entry. <paramref name="launcherPath"/> is the Battle.net app, when found.</summary>
    public static InstalledGame BattleNet(string name, string folder, string? launcherPath, long? sizeBytes = null, string? exePath = null)
    {
        var code = LauncherRules.BattleNetCode(name);
        var page = code is null ? null : GameLaunchUris.BattleNet(code);
        GameCommand? openApp = page is not null
            ? GameCommand.Link(page)
            : launcherPath is null ? null : GameCommand.Program(launcherPath, null, FolderOf(launcherPath));
        return new InstalledGame(
            "battlenet:" + (code ?? GameExeGuesser.Simplify(name)),
            GameLauncher.BattleNet,
            code ?? name,
            name,
            SteamLibraryRules.NormalizeFolder(folder),
            sizeBytes,
            null,
            null,
            exePath,
            null,
            openApp is null ? null : openApp with { Note = "The Battle.net app opens the game; press Play." },
            openApp is null ? null : openApp with { Note = "The Battle.net app opens. Click the settings button next to Play, then Scan and Repair." },
            GameCommand.AppsPage(AppsPageUninstall));
    }

    /// <summary>A Riot game (League of Legends, VALORANT…) from its Uninstall entry.</summary>
    public static InstalledGame Riot(string name, string folder, RiotProduct product, long? sizeBytes = null, string? exePath = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        var client = FolderOf(product.ClientPath);
        return new InstalledGame(
            "riot:" + product.Product + "." + product.Patchline,
            GameLauncher.Riot,
            product.Product,
            name,
            SteamLibraryRules.NormalizeFolder(folder),
            sizeBytes,
            null,
            null,
            exePath,
            null,
            GameCommand.Program(product.ClientPath, LauncherRules.RiotLaunchArguments(product), client, "The Riot Client starts the game."),
            GameCommand.Program(product.ClientPath, null, client, "The Riot Client opens. It checks and repairs the game's files by itself each time the game starts."),
            GameCommand.AppsPage(AppsPageUninstall));
    }

    private static GameCommand? Link(string? uri, string? note = null) => uri is null ? null : GameCommand.Link(uri, note);

    private static string? FolderOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        int slash = path.LastIndexOfAny(['\\', '/']);
        return slash > 0 ? path[..slash] : null;
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
