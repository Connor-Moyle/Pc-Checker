using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PcChecker.Core.Games;

/// <summary>One GOG game as its registry key (HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\&lt;id&gt;) describes it.</summary>
public sealed record GogGameEntry(string GameId, string Name, string InstallPath, string? ExePath, string? WorkingDirectory, string? LaunchArguments);

/// <summary>What an EA game's __Installer\installerdata.xml says: its title, content ids and program.</summary>
public sealed record EaInstallerData(string? Title, IReadOnlyList<string> ContentIds, string? ExeRelativePath);

/// <summary>The product and patch line of a Riot game, read from its uninstall command.</summary>
public sealed record RiotProduct(string ClientPath, string Product, string Patchline);

/// <summary>
/// Pure rules for the launchers that keep their games in the registry or in small files: GOG Galaxy, the EA app,
/// Ubisoft Connect, Battle.net and the Riot Client.
/// </summary>
public static partial class LauncherRules
{
    /// <summary>Battle.net product codes for the games whose page can be opened with battlenet://&lt;code&gt;.</summary>
    private static readonly (string NameStart, string Code)[] BattleNetCodes =
    [
        ("World of Warcraft", "WoW"),
        ("Diablo IV", "Fen"),
        ("Diablo III", "D3"),
        ("Diablo II: Resurrected", "OSI"),
        ("Diablo Immortal", "ANBS"),
        ("Overwatch", "Pro"),
        ("Hearthstone", "WTCG"),
        ("Heroes of the Storm", "Hero"),
        ("StarCraft II", "S2"),
        ("StarCraft", "S1"),
        ("Warcraft III", "W3"),
        ("Call of Duty: Modern Warfare", "ODIN"),
        ("Call of Duty: Black Ops Cold War", "ZEUS"),
        ("Call of Duty: Vanguard", "FORE"),
        ("Call of Duty", "AUKS"),
    ];

    private static readonly string[] EaLauncherNames = ["EA app", "EA Desktop", "Origin", "EA Origin", "EA Play"];

    private static readonly string[] BlizzardLauncherNames = ["Battle.net", "Blizzard Battle.net", "Battle.net App"];

    /// <summary>
    /// Maps a GOG registry key's values; null for a DLC (it names the game it depends on) or an entry without a folder.
    /// </summary>
    public static GogGameEntry? MapGog(string keyName, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string? Get(string name) => values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var path = Get("path");
        var id = Get("gameID") ?? keyName;
        if (path is null || !id.All(char.IsAsciiDigit) || Get("dependsOn") is not null)
        {
            return null;
        }

        var name = Get("gameName") ?? Get("startMenu") ?? GameLibraryMath.LastSegment(path);
        var exe = Get("exe");
        if (exe is not null && !IsAbsoluteWindowsPath(exe))
        {
            exe = GameLibraryMath.JoinPath(path, exe);
        }

        return new GogGameEntry(id, name, SteamLibraryRules.NormalizeFolder(path), exe, Get("workingDir"), Get("launchParam"));
    }

    /// <summary>
    /// Reads installerdata.xml from an EA game's __Installer folder (both the newer DiPManifest and the older game
    /// layouts). The program path loses its leading [registry] placeholder, which stands for the install folder.
    /// </summary>
    public static EaInstallerData? ParseEaInstallerData(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var all = doc.Descendants().ToList();
        var titles = all.Where(e => Is(e, "gameTitle") || (Is(e, "title") && Is(e.Parent, "localeInfo"))).ToList();
        var title = titles.FirstOrDefault(t => Locale(t) is { } l && l.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? titles.FirstOrDefault();
        var ids = all.Where(e => Is(e, "contentID")).Select(e => e.Value.Trim()).Where(v => v.Length > 0).Distinct().ToList();

        // The first launcher that is not a trial; its filePath is "[HKEY…\Install Dir]bin\game.exe".
        var launcher = all.Where(e => Is(e, "launcher"))
            .FirstOrDefault(l => !l.Elements().Any(c => Is(c, "trial") && c.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)));
        var filePath = launcher?.Elements().FirstOrDefault(c => Is(c, "filePath"))?.Value.Trim();
        string? relative = null;
        if (!string.IsNullOrEmpty(filePath))
        {
            relative = PlaceholderPattern().Replace(filePath, string.Empty).Trim().TrimStart('\\', '/');
            if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal) || IsAbsoluteWindowsPath(relative)
                || !relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                relative = null;
            }
        }

        var titleText = title?.Value.Trim();
        return new EaInstallerData(string.IsNullOrEmpty(titleText) ? null : titleText, ids, relative);
    }

    /// <summary>
    /// Reads the product and patch line from a Riot game's uninstall command, for example
    /// "C:\Riot Games\Riot Client\RiotClientServices.exe" --uninstall-product=valorant --uninstall-patchline=live.
    /// </summary>
    public static RiotProduct? ParseRiotUninstall(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        var match = RiotPattern().Match(uninstallString);
        if (!match.Success)
        {
            return null;
        }

        var client = match.Groups["exe"].Value.Trim().Trim('"');
        return client.EndsWith("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase)
            ? new RiotProduct(client, match.Groups["product"].Value, match.Groups["patch"].Value)
            : null;
    }

    /// <summary>The Riot Client arguments that start a product.</summary>
    public static string RiotLaunchArguments(RiotProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return $"--launch-product={product.Product} --launch-patchline={product.Patchline}";
    }

    /// <summary>The battlenet:// code for a Blizzard game's name, or null when it is not known.</summary>
    public static string? BattleNetCode(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var name = displayName.Trim();
        foreach (var (start, code) in BattleNetCodes)
        {
            if (name.StartsWith(start, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }

        return null;
    }

    /// <summary>True for an Uninstall entry of an EA game (not the EA app or Origin itself).</summary>
    public static bool IsEaGameEntry(string? publisher, string? displayName) =>
        IsPublisher(publisher, "Electronic Arts") && !string.IsNullOrWhiteSpace(displayName)
        && !EaLauncherNames.Any(n => displayName.Trim().Equals(n, StringComparison.OrdinalIgnoreCase))
        && !displayName.Contains("Redistributable", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for an Uninstall entry of a Battle.net game (not the Battle.net app itself). Activision games count only when
    /// their uninstaller is Battle.net's, so the same publisher's Steam games are not listed twice.
    /// </summary>
    public static bool IsBlizzardGameEntry(string? publisher, string? displayName, string? uninstallString = null) =>
        !string.IsNullOrWhiteSpace(displayName)
        && (IsPublisher(publisher, "Blizzard Entertainment")
            || (IsPublisher(publisher, "Activision") && uninstallString?.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) == true))
        && !BlizzardLauncherNames.Any(n => displayName.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True for an Uninstall entry that Steam wrote for one of its games ("Steam App 1234", or removed through a
    /// steam:// link). Those games are already listed from Steam's own files, whoever published them.
    /// </summary>
    public static bool IsSteamUninstallEntry(string? keyName, string? uninstallString) =>
        (keyName?.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) ?? false)
        || (uninstallString?.Contains("steam://", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>The Ubisoft install id from an Uninstall key name ("Uplay Install 5059"), or null.</summary>
    public static string? UbisoftIdFromUninstallKey(string? keyName)
    {
        const string prefix = "Uplay Install ";
        if (keyName is null || !keyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = keyName[prefix.Length..].Trim();
        return id.Length > 0 && id.All(char.IsAsciiDigit) ? id : null;
    }

    /// <summary>
    /// The program an Uninstall entry's DisplayIcon points at ("C:\Games\X\x.exe,0" → C:\Games\X\x.exe), when it is an
    /// .exe inside <paramref name="installFolder"/>; null otherwise (an uninstaller or a launcher's icon is no use).
    /// </summary>
    public static string? ExeFromDisplayIcon(string? displayIcon, string? installFolder)
    {
        if (string.IsNullOrWhiteSpace(displayIcon) || string.IsNullOrWhiteSpace(installFolder))
        {
            return null;
        }

        var icon = displayIcon.Trim();
        int comma = icon.LastIndexOf(',');
        if (comma > 0 && icon[(comma + 1)..].Trim().TrimStart('-').All(char.IsAsciiDigit))
        {
            icon = icon[..comma];
        }

        icon = icon.Trim().Trim('"');
        var name = Path.GetFileName(icon.Replace('\\', '/'));
        if (!icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var folder = SteamLibraryRules.NormalizeFolder(installFolder);
        var normalized = icon.Replace('/', '\\');
        return normalized.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) ? normalized : null;
    }

    /// <summary>True for a full Windows path ("C:\…" or "\\server\…").</summary>
    public static bool IsAbsoluteWindowsPath(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') || path.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool IsPublisher(string? publisher, string name) =>
        publisher is not null && publisher.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static bool Is(XElement? e, string name) => e is not null && string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static string? Locale(XElement e) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("locale", StringComparison.OrdinalIgnoreCase))?.Value
        ?? e.Parent?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("locale", StringComparison.OrdinalIgnoreCase))?.Value;

    [GeneratedRegex(@"^\s*\[[^\]]*\]")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^\s*(?<exe>""[^""]+""|\S+)\s+.*?--uninstall-product=(?<product>[a-z0-9_]+)\s+.*?--uninstall-patchline=(?<patch>[a-z0-9_]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RiotPattern();
}
