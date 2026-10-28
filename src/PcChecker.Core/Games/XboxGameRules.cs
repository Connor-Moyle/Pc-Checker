using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PcChecker.Core.Games;

/// <summary>What a game's MicrosoftGame.config says: its package identity, display name, programs and logo.</summary>
public sealed record XboxGameConfig(
    string IdentityName,
    string Publisher,
    string? DisplayName,
    IReadOnlyList<XboxExecutable> Executables,
    string? StoreLogo)
{
    /// <summary>The package family name Windows uses for the game (identity name + publisher id).</summary>
    public string PackageFamilyName => IdentityName + "_" + XboxGameRules.PublisherId(Publisher);

    /// <summary>The program the Xbox app starts: the one marked for PC, else the first.</summary>
    public XboxExecutable? MainExecutable =>
        Executables.FirstOrDefault(e => string.Equals(e.TargetDeviceFamily, "PC", StringComparison.OrdinalIgnoreCase)) ?? Executables.FirstOrDefault();
}

/// <summary>One &lt;Executable&gt; entry of MicrosoftGame.config.</summary>
public sealed record XboxExecutable(string Name, string? Id, string? TargetDeviceFamily);

/// <summary>
/// Pure rules for Xbox app and Microsoft Store games: reading MicrosoftGame.config (the file every PC Game Pass and
/// Store game built on the Gaming Runtime has), working out the package family name from the publisher the same way
/// Windows does, and reading the .GamingRoot marker that tells where a drive keeps its Xbox games.
/// </summary>
public static class XboxGameRules
{
    private const string CrockfordBase32 = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>Reads MicrosoftGame.config; null when it is not a game config (no Game element or no identity).</summary>
    public static XboxGameConfig? ParseConfig(string? xml)
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

        var game = doc.Root;
        if (game is null || !string.Equals(game.Name.LocalName, "Game", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var identity = Child(game, "Identity");
        var name = Attr(identity, "Name");
        var publisher = Attr(identity, "Publisher");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher))
        {
            return null;
        }

        var visuals = Child(game, "ShellVisuals");
        var executables = Child(game, "ExecutableList")?.Elements()
            .Where(e => string.Equals(e.Name.LocalName, "Executable", StringComparison.OrdinalIgnoreCase))
            .Select(e => new XboxExecutable(Attr(e, "Name") ?? string.Empty, Attr(e, "Id"), Attr(e, "TargetDeviceFamily")))
            .Where(e => e.Name.Length > 0)
            .ToList() ?? [];

        var display = Attr(visuals, "DefaultDisplayName");
        return new XboxGameConfig(
            name.Trim(),
            publisher.Trim(),
            string.IsNullOrWhiteSpace(display) || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : display.Trim(),
            executables,
            Attr(visuals, "StoreLogo") ?? Attr(visuals, "Square150x150Logo"));
    }

    /// <summary>
    /// The 13-character publisher id in a package family name ("8wekyb3d8bbwe" for Microsoft): the first 8 bytes of the
    /// SHA-256 of the publisher (UTF-16), written in Crockford base-32.
    /// </summary>
    public static string PublisherId(string publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong bits = 0;
        for (int i = 0; i < 8; i++)
        {
            bits = (bits << 8) | hash[i];
        }

        // 64 bits padded with one zero bit at the end make 13 groups of 5.
        var padded = (UInt128)bits << 1;
        var sb = new StringBuilder(13);
        for (int i = 0; i < 13; i++)
        {
            int index = (int)((padded >> (60 - (i * 5))) & 0x1F);
            sb.Append(CrockfordBase32[index]);
        }

        return sb.ToString();
    }

    /// <summary>True for text shaped like a package family name (Name_publisherid), safe to put in a shell link.</summary>
    public static bool IsPackageFamilyName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128)
        {
            return false;
        }

        int underscore = text.LastIndexOf('_');
        return underscore > 0 && text.Length - underscore - 1 == 13
               && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }

    /// <summary>
    /// The folders a drive's .GamingRoot file lists (relative to the drive, "XboxGames" by default). The file starts with
    /// "RGBX" and a 4-byte count, followed by null-terminated UTF-16 folder names.
    /// </summary>
    public static IReadOnlyList<string> ParseGamingRoot(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || data[0] != (byte)'R' || data[1] != (byte)'G' || data[2] != (byte)'B' || data[3] != (byte)'X')
        {
            return [];
        }

        var text = Encoding.Unicode.GetString(data[8..((data.Length - 8) / 2 * 2 + 8)]);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim('\\', '/'))
            .Where(s => s.Length > 0 && !s.Contains("..", StringComparison.Ordinal) && s.All(c => !char.IsControl(c)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static string? Attr(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
}
