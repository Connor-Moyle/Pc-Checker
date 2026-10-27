using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>One package listed in a Lenovo model catalog: where its description is and its category.</summary>
public sealed record LenovoCatalogPackage(string Location, string Category);

/// <summary>
/// Lenovo's public per-model catalogs, the ones Lenovo System Update reads: <c>download.lenovo.com/catalog/&lt;MT&gt;_Win11.xml</c>
/// lists every package for a machine type with its category; each package has a small XML description with its
/// version, release date, the hardware ids it installs on and the driver version it installs. PC Checker reads the
/// catalog and the BIOS, chipset, network, audio, Bluetooth and graphics packages only, never runs them, and
/// compares versions. XML is parsed without DTDs or external entities. Pure.
/// </summary>
public static class LenovoCatalog
{
    public const string Host = "download.lenovo.com";

    /// <summary>At most this many package descriptions are read per check.</summary>
    public const int MaxPackages = 16;

    /// <summary>The Windows 11 catalog for a machine type, or null for a value that is not one.</summary>
    public static string? CatalogUrl(string? machineType) =>
        machineType is { Length: 4 } mt && mt.All(char.IsAsciiLetterOrDigit)
            ? $"https://{Host}/catalog/{mt.ToUpperInvariant()}_Win11.xml"
            : null;

    /// <summary>The categories worth reading: BIOS, chipset, network, audio, Bluetooth and graphics.</summary>
    public static bool IsWanted(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        string[] words = ["BIOS", "chipset", "Networking", "Audio", "Bluetooth", "Display and Video"];
        return words.Any(w => category.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The packages in a catalog (only HTTPS locations on Lenovo's download server). Empty for anything unreadable.</summary>
    public static IReadOnlyList<LenovoCatalogPackage> ParseCatalog(string? xml)
    {
        var doc = Load(xml);
        if (doc?.Root is null)
        {
            return [];
        }

        var list = new List<LenovoCatalogPackage>();
        foreach (var package in doc.Root.Elements().Where(e => e.Name.LocalName.Equals("package", StringComparison.OrdinalIgnoreCase)))
        {
            var location = Child(package, "location")?.Trim();
            var category = Child(package, "category")?.Trim() ?? string.Empty;
            if (location is not null && IsLenovoUrl(location))
            {
                list.Add(new LenovoCatalogPackage(location, category));
            }
        }

        return list;
    }

    /// <summary>
    /// The wanted packages, BIOS first, at most <see cref="MaxPackages"/>, so the check stays a handful of small
    /// requests.
    /// </summary>
    public static IReadOnlyList<LenovoCatalogPackage> PickPackages(IEnumerable<LenovoCatalogPackage> packages) =>
        packages.Where(p => IsWanted(p.Category))
            .OrderBy(p => p.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(MaxPackages)
            .ToList();

    /// <summary>Reads one package description; null when it is not a Lenovo package description.</summary>
    public static MakerCatalogEntry? ParsePackage(string? xml, string location, string category)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(category);
        var root = Load(xml)?.Root;
        if (root is null || !root.Name.LocalName.Equals("Package", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = root.Attribute("version")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var title = root.Element("Title")?.Elements("Desc").FirstOrDefault(d => (string?)d.Attribute("id") == "EN")?.Value.Trim()
            ?? root.Element("Title")?.Elements("Desc").FirstOrDefault()?.Value.Trim()
            ?? root.Attribute("name")?.Value
            ?? category;
        DateTime? date = DateTime.TryParseExact(root.Element("ReleaseDate")?.Value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var detect = root.Element("DetectInstall");
        var driver = detect?.Descendants("_Driver").FirstOrDefault();
        var hardwareIds = driver?.Elements("HardwareID").Select(h => h.Value.Trim()).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        var driverVersion = driver?.Element("Version")?.Value.Trim().TrimEnd('^').Trim();
        bool isBios = category.Contains("BIOS", StringComparison.OrdinalIgnoreCase)
            || (root.Element("PackageType")?.Attribute("type")?.Value == "3" && detect?.Descendants("_Bios").Any() == true);
        var levels = root.Descendants("_Bios").Elements("Level").Select(l => l.Value.Trim().TrimEnd('*'))
            .Where(l => l.Length >= 5).Select(l => l[..5].ToUpperInvariant()).Distinct().ToList();
        var readme = root.Element("Files")?.Element("Readme")?.Elements("File").FirstOrDefault()?.Element("Name")?.Value.Trim();
        return new MakerCatalogEntry(category, title, isBios ? BiosPart(version) : version, date, ReadmeUrl(location, readme))
        {
            HardwareIds = hardwareIds,
            DriverVersion = string.IsNullOrWhiteSpace(driverVersion) ? null : driverVersion,
            IsBios = isBios,
            BiosLevels = isBios ? levels : [],
        };
    }

    /// <summary>"1.48/1.28" (BIOS / embedded controller) → "1.48".</summary>
    public static string BiosPart(string version) => version.Split('/')[0].Trim();

    /// <summary>The BIOS package for this PC's BIOS id ("N3BET"), or the only BIOS package when the id is unknown.</summary>
    public static MakerCatalogEntry? BiosFor(IEnumerable<MakerCatalogEntry> entries, string? biosId)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var bios = entries.Where(e => e.IsBios).ToList();
        if (biosId is not null && bios.FirstOrDefault(b => b.BiosLevels.Contains(biosId, StringComparer.OrdinalIgnoreCase)) is { } match)
        {
            return match;
        }

        return bios.Count == 1 ? bios[0] : null;
    }

    /// <summary>The readme next to the package description: same folder, the file name the package names.</summary>
    public static string? ReadmeUrl(string location, string? readmeName)
    {
        if (string.IsNullOrWhiteSpace(readmeName) || readmeName.IndexOfAny(['/', '\\', ':', '?', '#']) >= 0 || !IsLenovoUrl(location))
        {
            return null;
        }

        int slash = location.LastIndexOf('/');
        return slash < 0 ? null : location[..(slash + 1)] + Uri.EscapeDataString(readmeName);
    }

    /// <summary>True for an HTTPS URL on Lenovo's download server.</summary>
    public static bool IsLenovoUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    private static XDocument? Load(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿', ' ', '\r', '\n', '\t')), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
