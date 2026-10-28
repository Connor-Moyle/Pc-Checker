using System.Globalization;

namespace PcChecker.Core.Games;

/// <summary>What one <c>appmanifest_&lt;appid&gt;.acf</c> file says about an installed Steam app.</summary>
public sealed record SteamAppManifest(
    string AppId,
    string Name,
    string InstallDir,
    long? SizeOnDisk,
    DateTimeOffset? LastUpdated,
    DateTimeOffset? LastPlayed,
    int StateFlags)
{
    /// <summary>True when Steam marks the app as completely installed (state flag 4).</summary>
    public bool IsFullyInstalled => (StateFlags & SteamLibraryRules.FlagFullyInstalled) != 0;
}

/// <summary>
/// Pure rules for Steam's own files: the library list (libraryfolders.vdf, old and new layouts), app manifests,
/// last-played times from localconfig.vdf, which apps are tools rather than games, and where the cover art and shader
/// cache live. Nothing here touches the disk.
/// </summary>
public static class SteamLibraryRules
{
    public const int FlagUpdateRequired = 2;
    public const int FlagFullyInstalled = 4;
    public const int FlagFilesMissing = 32;
    public const int FlagFilesCorrupt = 128;
    public const int FlagUpdateRunning = 256;

    /// <summary>Steam apps that are runtimes or tools, not games (redistributables, Proton, Linux runtimes, SteamVR).</summary>
    public static IReadOnlySet<string> ToolAppIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "228980",  // Steamworks Common Redistributables
        "250820",  // SteamVR
        "1070560", // Steam Linux Runtime 1.0 (scout)
        "1391110", // Steam Linux Runtime 2.0 (soldier)
        "1628350", // Steam Linux Runtime 3.0 (sniper)
        "1493710", // Proton Experimental
        "2180100", // Proton Hotfix
        "1826330", // Proton EasyAntiCheat Runtime
        "1161040", // Proton BattlEye Runtime
    };

    private static readonly string[] ToolNamePrefixes = ["Proton ", "Steam Linux Runtime", "Steamworks Common", "Steamworks Shared", "SteamVR"];

    /// <summary>True for an app that is a runtime, redistributable or tool rather than a game.</summary>
    public static bool IsTool(string? appId, string? name)
    {
        if (!string.IsNullOrWhiteSpace(appId) && ToolAppIds.Contains(appId.Trim()))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var n = name.Trim();
        return n.Equals("Proton", StringComparison.OrdinalIgnoreCase)
               || ToolNamePrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))
               || n.Contains("Redistributable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The library folders listed in libraryfolders.vdf. New files have a block per library with a "path" value; old
    /// ones list the paths directly under numbered keys. Steam's own folder is always a library, so it comes first when
    /// given. Duplicates (any case, trailing slashes) are dropped.
    /// </summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string? text, string? steamPath = null)
    {
        var result = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var clean = NormalizeFolder(path);
            if (!result.Any(r => string.Equals(r, clean, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(clean);
            }
        }

        Add(steamPath);
        var root = VdfParser.Parse(text);
        var list = root["libraryfolders"] ?? root.Children.FirstOrDefault(c => c.Value.IsBlock).Value;
        if (list is null)
        {
            return result;
        }

        foreach (var (key, node) in list.Children)
        {
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            Add(node.IsBlock ? node.Text("path") : node.Value);
        }

        return result;
    }

    /// <summary>Reads an app manifest; null when it has no app id or install folder.</summary>
    public static SteamAppManifest? ParseAppManifest(string? text)
    {
        var root = VdfParser.Parse(text);
        var state = root["AppState"] ?? root.Children.FirstOrDefault(c => c.Value.IsBlock).Value;
        if (state is null)
        {
            return null;
        }

        var appId = state.Text("appid")?.Trim();
        var installDir = state.Text("installdir")?.Trim();
        if (string.IsNullOrEmpty(appId) || !IsAppId(appId) || string.IsNullOrEmpty(installDir))
        {
            return null;
        }

        var name = state.Text("name")?.Trim();
        return new SteamAppManifest(
            appId,
            string.IsNullOrEmpty(name) ? installDir : name,
            installDir,
            ParseLong(state.Text("SizeOnDisk")),
            FromUnix(state.Text("LastUpdated")),
            FromUnix(state.Text("LastPlayed")),
            (int)(ParseLong(state.Text("StateFlags")) ?? 0));
    }

    /// <summary>
    /// The last time each app was played, from one user's localconfig.vdf
    /// (UserLocalConfigStore → Software → Valve → Steam → apps → &lt;appid&gt; → LastPlayed).
    /// </summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> ParseLastPlayed(string? localConfigText)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var root = VdfParser.Parse(localConfigText);
        var store = root["UserLocalConfigStore"] ?? root;
        var apps = store.Path("Software", "Valve", "Steam", "apps");
        if (apps is null)
        {
            return result;
        }

        foreach (var (appId, node) in apps.Children)
        {
            if (IsAppId(appId) && FromUnix(node.Text("LastPlayed")) is { } played)
            {
                result[appId] = played;
            }
        }

        return result;
    }

    /// <summary>Keeps the latest time per app across several Steam accounts on the same PC.</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> MergeLastPlayed(IEnumerable<IReadOnlyDictionary<string, DateTimeOffset>> perUser)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var user in perUser)
        {
            foreach (var (appId, played) in user)
            {
                if (!result.TryGetValue(appId, out var existing) || played > existing)
                {
                    result[appId] = played;
                }
            }
        }

        return result;
    }

    /// <summary>A plain note for a manifest whose state needs attention, or null when it is ready to play.</summary>
    public static string? StateNote(int stateFlags)
    {
        if ((stateFlags & (FlagFilesMissing | FlagFilesCorrupt)) != 0)
        {
            return "Some files are missing or damaged. Verify the files.";
        }

        if ((stateFlags & FlagUpdateRunning) != 0)
        {
            return "Updating now.";
        }

        if ((stateFlags & FlagFullyInstalled) == 0)
        {
            return "Not fully installed yet.";
        }

        return (stateFlags & FlagUpdateRequired) != 0 ? "An update is waiting." : null;
    }

    /// <summary>Where Steam keeps an app's cover art, most wanted first (portrait cover, then the wide header).</summary>
    public static IReadOnlyList<string> CoverCandidates(string steamPath, string appId)
    {
        var cache = GameLibraryMath.JoinPath(steamPath, "appcache", "librarycache");
        return
        [
            GameLibraryMath.JoinPath(cache, appId + "_library_600x900.jpg"),
            GameLibraryMath.JoinPath(cache, appId, "library_600x900.jpg"),
            GameLibraryMath.JoinPath(cache, appId + "_library_600x900_2x.jpg"),
            GameLibraryMath.JoinPath(cache, appId, "library_capsule.jpg"),
            GameLibraryMath.JoinPath(cache, appId + "_header.jpg"),
            GameLibraryMath.JoinPath(cache, appId, "header.jpg"),
        ];
    }

    /// <summary>The app's shader cache folder inside a library: &lt;library&gt;\steamapps\shadercache\&lt;appid&gt;.</summary>
    public static string ShaderCacheFolder(string libraryPath, string appId) => GameLibraryMath.JoinPath(libraryPath, "steamapps", "shadercache", appId);

    /// <summary>
    /// True only for a folder that is exactly &lt;library&gt;\steamapps\shadercache\&lt;appid&gt; for one of the known
    /// libraries: the only thing the shader cache clean-up may ever remove.
    /// </summary>
    public static bool IsShaderCacheFolder(string? path, string appId, IEnumerable<string> libraries)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsAppId(appId) || path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = NormalizeFolder(path);
        return libraries.Any(l => !string.IsNullOrWhiteSpace(l)
                                  && string.Equals(NormalizeFolder(ShaderCacheFolder(NormalizeFolder(l), appId)), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for a Steam app id (digits only, so it is always safe inside a steam:// link).</summary>
    public static bool IsAppId(string? appId) =>
        !string.IsNullOrEmpty(appId) && appId.Length <= 10 && appId.All(char.IsAsciiDigit);

    /// <summary>A Unix time in seconds, as Steam writes it; null for 0, blanks and nonsense.</summary>
    public static DateTimeOffset? FromUnix(string? seconds)
    {
        var value = ParseLong(seconds);
        return value is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(value.Value) : null;
    }

    private static long? ParseLong(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Back-slashes, no trailing slash (except for a drive root), forward slashes turned round.</summary>
    public static string NormalizeFolder(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        while (p.Length > 3 && p.EndsWith('\\'))
        {
            p = p[..^1];
        }

        // Old files sometimes double every back-slash.
        while (p.Contains(@"\\", StringComparison.Ordinal) && !p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            p = p.Replace(@"\\", @"\", StringComparison.Ordinal);
        }

        return p;
    }
}
