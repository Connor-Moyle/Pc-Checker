using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PcChecker.Core.Games;

/// <summary>Steam: libraryfolders.vdf for the libraries, appmanifest_*.acf for each game, localconfig.vdf for last played.</summary>
public sealed partial class WindowsGameLibraryService
{
    private LauncherResult ReadSteam(CancellationToken ct)
    {
        var steamPath = FindSteamPath();
        if (steamPath is null)
        {
            return LauncherResult.NotInstalled;
        }

        var libraries = SteamLibraryRules.ParseLibraryFolders(
            ReadTextFile(GameLibraryMath.JoinPath(steamPath, "steamapps", "libraryfolders.vdf"))
            ?? ReadTextFile(GameLibraryMath.JoinPath(steamPath, "config", "libraryfolders.vdf")),
            steamPath);
        lock (_gate)
        {
            _steamLibraries = libraries;
        }

        var lastPlayed = ReadSteamLastPlayed(steamPath, ct);
        var games = new List<InstalledGame>();
        var missingLibraries = 0;
        foreach (var library in libraries)
        {
            ct.ThrowIfCancellationRequested();
            var steamapps = GameLibraryMath.JoinPath(library, "steamapps");
            if (!SafeDirectoryExists(steamapps))
            {
                // A library on a drive that is unplugged right now.
                missingLibraries++;
                continue;
            }

            string[] manifests;
            try
            {
                manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Steam library {Library} could not be listed.", library);
                continue;
            }

            foreach (var file in manifests)
            {
                var manifest = SteamLibraryRules.ParseAppManifest(ReadTextFile(file, 4 * 1024 * 1024));
                if (manifest is null || SteamLibraryRules.IsTool(manifest.AppId, manifest.Name))
                {
                    continue;
                }

                var shaderFolder = SteamLibraryRules.ShaderCacheFolder(library, manifest.AppId);
                long? shaderBytes = SafeDirectoryExists(shaderFolder) ? Software.DirectorySizeCalculator.TryGetSize(shaderFolder, ct) : null;
                games.Add(GameEntryFactory.Steam(
                    manifest,
                    library,
                    lastPlayed.TryGetValue(manifest.AppId, out var played) ? played : null,
                    coverPath: SteamCover(steamPath, manifest.AppId),
                    shaderCacheBytes: shaderBytes));
            }
        }

        var note = missingLibraries > 0 ? $"{missingLibraries} library folder(s) could not be found; the drive may be unplugged." : null;
        return new LauncherResult(true, games, note);
    }

    private static string? FindSteamPath()
    {
        var candidates = new[]
        {
            ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            ReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
        };
        foreach (var candidate in candidates)
        {
            if (candidate is not null)
            {
                var folder = SteamLibraryRules.NormalizeFolder(candidate);
                if (SafeDirectoryExists(GameLibraryMath.JoinPath(folder, "steamapps")))
                {
                    return folder;
                }
            }
        }

        return null;
    }

    /// <summary>The latest last-played time per game across every Steam account that has signed in on this PC.</summary>
    private IReadOnlyDictionary<string, DateTimeOffset> ReadSteamLastPlayed(string steamPath, CancellationToken ct)
    {
        var perUser = new List<IReadOnlyDictionary<string, DateTimeOffset>>();
        try
        {
            var userdata = GameLibraryMath.JoinPath(steamPath, "userdata");
            if (!SafeDirectoryExists(userdata))
            {
                return new Dictionary<string, DateTimeOffset>();
            }

            foreach (var user in Directory.GetDirectories(userdata).Take(16))
            {
                ct.ThrowIfCancellationRequested();
                var text = ReadTextFile(GameLibraryMath.JoinPath(user, "config", "localconfig.vdf"));
                if (text is not null)
                {
                    perUser.Add(SteamLibraryRules.ParseLastPlayed(text));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Steam's last-played times could not be read.");
        }

        return SteamLibraryRules.MergeLastPlayed(perUser);
    }

    private static string? SteamCover(string steamPath, string appId)
    {
        foreach (var candidate in SteamLibraryRules.CoverCandidates(steamPath, appId))
        {
            if (SafeFileExists(candidate))
            {
                return candidate;
            }
        }

        // Newer Steam versions keep the art one folder deeper (librarycache\<appid>\<hash>\library_600x900.jpg).
        try
        {
            var folder = GameLibraryMath.JoinPath(steamPath, "appcache", "librarycache", appId);
            if (SafeDirectoryExists(folder))
            {
                foreach (var sub in Directory.GetDirectories(folder).Take(8))
                {
                    var file = GameLibraryMath.JoinPath(sub, "library_600x900.jpg");
                    if (SafeFileExists(file))
                    {
                        return file;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No cover; the card shows the program's icon.
        }

        return null;
    }
}
