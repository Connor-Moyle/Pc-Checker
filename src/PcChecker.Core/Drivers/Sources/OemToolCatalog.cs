using System.Text;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>A PC maker's or chip maker's update tool and how to find it.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Vendor">Maker key, matched against the PC or board maker (see <see cref="OemToolCatalog.VendorKey"/>).</param>
/// <param name="Name">Product name shown to the user.</param>
/// <param name="DisplayNames">Uninstall-entry names that identify an install (exact match, ignoring case and ®/™).</param>
/// <param name="ExecutablePaths">Known program paths relative to Program Files or Program Files (x86).</param>
/// <param name="StorePackageNames">Microsoft Store package names (the part before the publisher hash).</param>
/// <param name="WingetId">winget package id, or null when the tool is not on winget.</param>
/// <param name="LaptopOnly">Only offered when missing on laptops (the maker's desktop boards use another tool).</param>
/// <param name="OpenUrl">A page that is the tool's own interface (Intel DSA runs in the browser), or null.</param>
public sealed record OemTool(
    string Id,
    string Vendor,
    string Name,
    IReadOnlyList<string> DisplayNames,
    IReadOnlyList<string> ExecutablePaths,
    IReadOnlyList<string> StorePackageNames,
    string? WingetId,
    bool LaptopOnly = false,
    string? OpenUrl = null);

/// <summary>An installed tool: where it was found and how to start it.</summary>
/// <param name="ExecutablePath">Program to start (only set when it is in a folder only administrators can change).</param>
/// <param name="AppUserModelId">Store app id for <c>shell:AppsFolder</c>, when installed from the Store.</param>
public sealed record OemToolInstall(OemTool Tool, string? ExecutablePath, string? AppUserModelId);

/// <summary>The PC maker and model to use for support links and tools.</summary>
public sealed record OemIdentity(string VendorKey, string VendorName, string? Model, bool IsBoard);

/// <summary>
/// PC maker tools that keep drivers, BIOS and firmware current (Dell Command | Update, HP Support Assistant and HP
/// Image Assistant, Lenovo Vantage and System Update, MyASUS and Armoury Crate, MSI Center, GIGABYTE Control Center)
/// plus Intel's Driver &amp; Support Assistant; the maker's support page for the exact model; and the winget command
/// that installs a missing tool. Pure.
/// </summary>
public static partial class OemToolCatalog
{
    public static IReadOnlyList<OemTool> Tools { get; } =
    [
        new("dell-command-update", "dell", "Dell Command | Update", ["Dell Command | Update", "Dell Command | Update for Windows Universal", "Dell Command | Update for Windows 10"],
            [@"Dell\CommandUpdate\DellCommandUpdate.exe"], ["DellInc.DellCommandUpdate"], "Dell.CommandUpdate"),
        new("dell-supportassist", "dell", "Dell SupportAssist", ["Dell SupportAssist", "SupportAssist"],
            [@"Dell\SupportAssistAgent\bin\SupportAssist.exe"], ["DellInc.DellSupportAssistforPCs"], null, LaptopOnly: true),
        new("hp-support-assistant", "hp", "HP Support Assistant", ["HP Support Assistant"],
            [@"HP\HP Support Framework\HPSF.exe"], ["AD2F1837.HPSupportAssistant"], null),
        new("hp-image-assistant", "hp", "HP Image Assistant", ["HP Image Assistant"],
            [@"HP\HPIA\HPImageAssistant.exe"], [], "HP.ImageAssistant"),
        new("lenovo-vantage", "lenovo", "Lenovo Vantage", ["Lenovo Vantage", "Lenovo Commercial Vantage"],
            [], ["E046963F.LenovoCompanion", "E046963F.LenovoSettingsforEnterprise"], null),
        new("lenovo-system-update", "lenovo", "Lenovo System Update", ["Lenovo System Update"],
            [@"Lenovo\System Update\tvsu.exe"], [], "Lenovo.SystemUpdate"),
        new("myasus", "asus", "MyASUS", ["MyASUS"],
            [], ["B9ECED6F.ASUSPCAssistant"], null, LaptopOnly: true),
        new("armoury-crate", "asus", "Armoury Crate", ["ARMOURY CRATE", "Armoury Crate"],
            [], ["B9ECED6F.ArmouryCrate"], null),
        new("msi-center", "msi", "MSI Center", ["MSI Center"],
            [@"MSI\MSI Center\MSI Center.exe"], ["9426MICRO-STARINTERNATION.MSICenter"], null),
        new("gigabyte-control-center", "gigabyte", "GIGABYTE Control Center", ["GIGABYTE Control Center"],
            [@"GIGABYTE\Control Center\GCC.exe"], [], null),
        new("intel-dsa", "intel", "Intel Driver & Support Assistant", ["Intel Driver & Support Assistant", "Intel(R) Driver & Support Assistant"],
            [], [], "Intel.IntelDriverAndSupportAssistant", OpenUrl: IntelDriverCatalog.SupportAssistantPage),
    ];

    /// <summary>Generic placeholders firmware leaves in the manufacturer and model fields.</summary>
    private static readonly string[] Placeholders =
    [
        "system manufacturer", "system product name", "to be filled by o.e.m.", "to be filled by oem", "default string",
        "o.e.m.", "oem", "not applicable", "none", "unknown", "system version", "type1productconfigid", "all series",
    ];

    /// <summary>The maker key for a manufacturer name ("ASUSTeK COMPUTER INC." → "asus"), or null when not one PC Checker knows.</summary>
    public static string? VendorKey(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return null;
        }

        var m = manufacturer.Trim().ToLowerInvariant();
        if (m.StartsWith("dell", StringComparison.Ordinal) || m.StartsWith("alienware", StringComparison.Ordinal)) { return "dell"; }
        if (m == "hp" || m.StartsWith("hp ", StringComparison.Ordinal) || m.StartsWith("hewlett", StringComparison.Ordinal)) { return "hp"; }
        if (m.StartsWith("lenovo", StringComparison.Ordinal)) { return "lenovo"; }
        if (m.StartsWith("asus", StringComparison.Ordinal)) { return "asus"; }
        if (m.StartsWith("micro-star", StringComparison.Ordinal) || m == "msi" || m.StartsWith("msi ", StringComparison.Ordinal)) { return "msi"; }
        if (m.StartsWith("gigabyte", StringComparison.Ordinal) || m.StartsWith("aorus", StringComparison.Ordinal)) { return "gigabyte"; }
        return null;
    }

    /// <summary>Display name for a maker key.</summary>
    public static string VendorName(string key) => key switch
    {
        "dell" => "Dell",
        "hp" => "HP",
        "lenovo" => "Lenovo",
        "asus" => "ASUS",
        "msi" => "MSI",
        "gigabyte" => "GIGABYTE",
        "intel" => "Intel",
        _ => key,
    };

    /// <summary>True for an empty or firmware placeholder value ("System Product Name", "To Be Filled By O.E.M.").</summary>
    public static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var v = value.Trim().ToLowerInvariant();
        return Placeholders.Contains(v) || v.All(c => c == '0' || c == ' ' || c == '-' || c == '.');
    }

    /// <summary>
    /// Who to ask for drivers: the PC maker and model when they are real values, otherwise the motherboard maker and
    /// board (self-built desktops report "System Product Name"). Null when neither is a known maker.
    /// </summary>
    public static OemIdentity? Resolve(DriverSystemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var systemKey = IsPlaceholder(facts.Manufacturer) ? null : VendorKey(facts.Manufacturer);
        if (systemKey is not null && !IsPlaceholder(facts.Model))
        {
            return new OemIdentity(systemKey, VendorName(systemKey), facts.Model!.Trim(), IsBoard: false);
        }

        var boardKey = IsPlaceholder(facts.BoardManufacturer) ? null : VendorKey(facts.BoardManufacturer);
        if (boardKey is not null)
        {
            return new OemIdentity(boardKey, VendorName(boardKey), IsPlaceholder(facts.BoardProduct) ? null : facts.BoardProduct!.Trim(), IsBoard: true);
        }

        return systemKey is null ? null : new OemIdentity(systemKey, VendorName(systemKey), null, IsBoard: false);
    }

    /// <summary>The maker's driver support page for the model, or the maker's general driver page without one.</summary>
    public static string SupportUrl(OemIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var model = identity.Model;
        var q = model is null ? null : Uri.EscapeDataString(model);
        return identity.VendorKey switch
        {
            "dell" => model is null ? "https://www.dell.com/support/home/en-us?app=drivers" : $"https://www.dell.com/support/home/en-us/product-support/product/{Slug(model)}/drivers",
            "hp" => model is null ? "https://support.hp.com/us-en/drivers" : $"https://support.hp.com/us-en/search?q={q}",
            "lenovo" => model is null ? "https://pcsupport.lenovo.com/" : $"https://pcsupport.lenovo.com/us/en/search?query={q}",
            "asus" => model is null ? "https://www.asus.com/support/download-center/" : $"https://www.asus.com/searchresult?searchType=support&searchKey={q}",
            "msi" => model is null ? "https://www.msi.com/support/download" : $"https://www.msi.com/search/{q}",
            "gigabyte" => model is null ? "https://www.gigabyte.com/Support" : $"https://www.gigabyte.com/Search?kw={q}",
            "intel" => IntelDriverCatalog.SupportAssistantPage,
            _ => DriverRules.OptionalUpdatesUri,
        };
    }

    /// <summary>Dell's product slug: "XPS 15 9530" → "xps-15-9530".</summary>
    public static string Slug(string model)
    {
        var sb = new StringBuilder();
        foreach (char c in model.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        return sb.ToString().Trim('-');
    }

    /// <summary>The tools worth showing for a maker: all of its tools, minus the laptop-only ones on a desktop unless installed.</summary>
    public static IReadOnlyList<OemTool> ToolsFor(string vendorKey, bool isLaptop) =>
        Tools.Where(t => t.Vendor == vendorKey && (isLaptop || !t.LaptopOnly)).ToList();

    /// <summary>True when an uninstall entry's DisplayName is this tool (exact, ignoring case, ®, ™ and repeated spaces).</summary>
    public static bool MatchesDisplayName(OemTool tool, string? displayName)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        var name = CleanName(displayName);
        return tool.DisplayNames.Any(n => string.Equals(CleanName(n), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The program path inside a DisplayIcon value ("\"C:\\…\\Tool.exe\",0" → C:\…\Tool.exe), when it is an .exe.</summary>
    public static string? ExecutableFromDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var t = displayIcon.Trim();
        if (t.StartsWith('"'))
        {
            int end = t.IndexOf('"', 1);
            t = end > 1 ? t[1..end] : t.Trim('"');
        }
        else
        {
            int comma = t.LastIndexOf(',');
            if (comma > 0 && t[(comma + 1)..].Trim().TrimStart('-').All(char.IsAsciiDigit))
            {
                t = t[..comma];
            }
        }

        t = t.Trim();
        return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsDrivePath(t) ? t : null;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a full path inside one of <paramref name="protectedRoots"/> (Program Files,
    /// Program Files (x86), Windows Apps): folders only administrators can change. PC Checker runs elevated, so it
    /// never starts a program from anywhere else. Relative segments ("..") are refused.
    /// </summary>
    public static bool IsInProtectedFolder(string? path, IEnumerable<string?> protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(protectedRoots);
        if (string.IsNullOrWhiteSpace(path) || !IsDrivePath(path) || path.IndexOf(':', 2) >= 0
            || path.Split('\\', '/').Any(s => s == ".." || s == "."))
        {
            return false;
        }

        foreach (var root in protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var r = root.TrimEnd('\\', '/') + "\\";
            if (path.Replace('/', '\\').StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A Windows path on a local drive ("C:\…"), checked the same way on every OS so the rule is testable.</summary>
    public static bool IsDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    /// <summary>Joins a Windows folder and a relative path with a backslash.</summary>
    public static string JoinWindowsPath(string root, string relative) => root.TrimEnd('\\', '/') + "\\" + relative.TrimStart('\\', '/');

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex WingetIdPattern();

    /// <summary>True for a plain winget package id (letters, digits, dots, dashes, underscores).</summary>
    public static bool IsValidWingetId(string? id) => id is not null && WingetIdPattern().IsMatch(id);

    /// <summary>The winget arguments that install a tool, one per element (passed with ArgumentList, never joined).</summary>
    public static IReadOnlyList<string> WingetInstallArguments(string wingetId)
    {
        if (!IsValidWingetId(wingetId))
        {
            throw new ArgumentException("Not a valid winget package id.", nameof(wingetId));
        }

        return ["install", "--id", wingetId, "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"];
    }

    /// <summary>The command line shown to the user before winget runs.</summary>
    public static string WingetCommandText(string wingetId) => "winget " + string.Join(' ', WingetInstallArguments(wingetId));

    /// <summary>Reads winget's exit code for an install.</summary>
    public static (bool Success, string Text) InterpretWingetExitCode(int exitCode) => exitCode switch
    {
        0 => (true, "Installed."),
        unchecked((int)0x8A150014) => (false, "winget could not find this package. Install it from the maker's page instead."),
        unchecked((int)0x8A15000F) => (false, "winget could not reach its package source. Check the internet connection."),
        unchecked((int)0x8A150011) => (false, "The installer's checksum did not match, so winget did not run it."),
        _ => (false, $"winget ended with code 0x{exitCode:X8}."),
    };

    private static string CleanName(string name) =>
        Regex.Replace(name.Replace("®", string.Empty, StringComparison.Ordinal).Replace("™", string.Empty, StringComparison.Ordinal)
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase),
            @"\s+", " ").Trim();
}
