using PcChecker.Core.Optimization;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>
/// Friendly names for Windows capabilities ("Features on Demand") such as <c>OpenSSH.Client~~~~0.0.1.0</c>.
/// Matching is by the part before the first <c>~</c>; families such as <c>Language.*</c> and <c>Rsat.*</c> are grouped.
/// </summary>
public static class CapabilityCatalog
{
    public sealed record Entry(string Prefix, string DisplayName, string Description, RiskLevel Risk, string? Recommendation, bool AdvancedOnly, string Group);

    public const string GroupGeneral = "General";
    public const string GroupLanguage = "Language";
    public const string GroupRsat = "Server administration (RSAT)";
    public const string GroupOther = "Other";

    public static IReadOnlyList<Entry> All { get; } =
    [
        new("OpenSSH.Client", "OpenSSH Client", "The ssh, scp and sftp command-line tools.", RiskLevel.Safe, "Keep it if you connect to servers or Git over SSH.", false, GroupGeneral),
        new("OpenSSH.Server", "OpenSSH Server", "Lets other computers connect to this PC over SSH.", RiskLevel.Moderate, "Only install if you need remote shell access to this PC; it opens a network port.", false, GroupGeneral),
        new("Media.WindowsMediaPlayer", "Windows Media Player (legacy)", "The classic Windows Media Player.", RiskLevel.Safe, "Safe to remove if you use another player.", false, GroupGeneral),
        new("Media.MediaFeaturePack", "Media Feature Pack", "Media playback components for Windows N editions.", RiskLevel.Moderate, "Needed on Windows N editions to play media.", true, GroupGeneral),
        new("Print.Fax.Scan", "Windows Fax and Scan", "Send faxes and scan documents.", RiskLevel.Safe, "Safe to remove if you never fax or scan from this app.", false, GroupGeneral),
        new("Print.Management.Console", "Print Management console", "Manage printers and print servers.", RiskLevel.Safe, "Safe to remove on a home PC.", true, GroupGeneral),
        new("Microsoft.Windows.Notepad", "Notepad (classic)", "The classic Notepad text editor.", RiskLevel.Moderate, "Keep it; many text files open with Notepad.", false, GroupGeneral),
        new("Microsoft.Windows.WordPad", "WordPad", "Basic word processor (retired in newer Windows 11).", RiskLevel.Safe, "Safe to remove; Word or a free office suite can open .rtf files.", false, GroupGeneral),
        new("Microsoft.Windows.MSPaint", "Paint (classic)", "The classic Paint image editor.", RiskLevel.Safe, "Safe to remove if you use another image editor.", false, GroupGeneral),
        new("Microsoft.Windows.PowerShell.ISE", "PowerShell ISE", "Script editor for Windows PowerShell.", RiskLevel.Safe, "Safe to remove unless you write PowerShell scripts.", false, GroupGeneral),
        new("Microsoft.Windows.SnippingTool", "Snipping Tool (classic)", "Classic screenshot tool.", RiskLevel.Safe, "Safe to remove; the Store Snipping Tool replaces it.", true, GroupGeneral),
        new("Microsoft.Windows.StepsRecorder", "Steps Recorder", "Records your clicks to send to support (retired).", RiskLevel.Safe, "Safe to remove.", false, GroupGeneral),
        new("App.StepsRecorder", "Steps Recorder", "Records your clicks to send to support (retired).", RiskLevel.Safe, "Safe to remove.", false, GroupGeneral),
        new("Browser.InternetExplorer", "Internet Explorer mode support", "Components used by Edge's IE mode.", RiskLevel.Moderate, "Keep if any old website only works in IE mode.", false, GroupGeneral),
        new("Hello.Face", "Windows Hello Face", "Sign in with your face using a compatible camera.", RiskLevel.Safe, "Keep it if you sign in with a camera.", false, GroupGeneral),
        new("MathRecognizer", "Math Recognizer", "Recognizes handwritten maths.", RiskLevel.Safe, "Safe to remove.", false, GroupGeneral),
        new("XPS.Viewer", "XPS Viewer", "Opens the old XPS document format.", RiskLevel.Safe, "Safe to remove; PDF has replaced XPS.", false, GroupGeneral),
        new("Tools.DeveloperMode.Core", "Developer Mode tools", "Device Portal and SSH tooling for developers.", RiskLevel.Safe, "Only needed by app developers.", true, GroupGeneral),
        new("Tools.Graphics.DirectX", "Graphics Tools", "DirectX debugging tools for developers.", RiskLevel.Safe, "Only needed by game developers.", true, GroupGeneral),
        new("Microsoft.Windows.Ethernet.Client", "Ethernet drivers", "Built-in network card drivers.", RiskLevel.Advanced, "Do not remove.", true, GroupGeneral),
        new("Microsoft.Windows.Wifi.Client", "Wi-Fi drivers", "Built-in Wi-Fi drivers.", RiskLevel.Advanced, "Do not remove.", true, GroupGeneral),
        new("DirectX.Configuration.Database", "DirectX configuration database", "Compatibility data for games.", RiskLevel.Advanced, "Do not remove.", true, GroupGeneral),
        new("Windows.Client.ShellComponents", "Windows shell components", "Parts of the Windows desktop.", RiskLevel.Advanced, "Do not remove.", true, GroupGeneral),
        new("Windows.Kernel.LA57", "57-bit addressing support", "Kernel support for very large memory.", RiskLevel.Advanced, "Leave as is.", true, GroupGeneral),
        new("Microsoft.Windows.Sense.Client", "Defender for Endpoint sensor", "Business security sensor.", RiskLevel.Advanced, "Leave as is.", true, GroupGeneral),
        new("Microsoft.Windows.Wifi.Direct.Client", "Wi-Fi Direct", "Direct device-to-device Wi-Fi.", RiskLevel.Safe, null, true, GroupGeneral),
        new("Microsoft.Windows.WirelessDisplay", "Wireless Display", "Project to a TV with Miracast.", RiskLevel.Safe, "Keep it if you cast to a TV.", false, GroupGeneral),
        new("Microsoft.Windows.MixedReality", "Mixed Reality", "Windows Mixed Reality headset support (retired).", RiskLevel.Safe, "Safe to remove.", true, GroupGeneral),
        new("Microsoft.Onecore.StorageManagement", "Storage management tools", "Disk management cmdlets.", RiskLevel.Advanced, "Do not remove.", true, GroupGeneral),
        new("Msix.PackagingTool.Driver", "MSIX Packaging Tool driver", "Driver used by the MSIX Packaging Tool.", RiskLevel.Safe, "Only needed by developers.", true, GroupGeneral),
        new("Language.", "Language component", "Typing, speech, handwriting or display language files.", RiskLevel.Safe, "Remove only languages you do not use.", false, GroupLanguage),
        new("Rsat.", "Remote Server Administration Tools", "Tools to manage Windows Servers from this PC.", RiskLevel.Safe, "Only needed by IT administrators.", true, GroupRsat),
    ];

    /// <summary>Finds the catalog entry for a capability name such as "OpenSSH.Client~~~~0.0.1.0".</summary>
    public static Entry? Find(string? capabilityName)
    {
        var baseName = BaseName(capabilityName);
        if (baseName.Length == 0)
        {
            return null;
        }

        foreach (var entry in All)
        {
            bool family = entry.Prefix.EndsWith('.');
            if (family ? baseName.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase)
                       : baseName.Equals(entry.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The part before the first "~" (version and architecture suffix stripped).</summary>
    public static string BaseName(string? capabilityName)
    {
        if (string.IsNullOrWhiteSpace(capabilityName))
        {
            return string.Empty;
        }

        int tilde = capabilityName.IndexOf('~');
        return (tilde >= 0 ? capabilityName[..tilde] : capabilityName).Trim();
    }

    /// <summary>Friendly name; language and RSAT entries append the specific component (e.g. "Language component: Basic (en-GB)").</summary>
    public static string FriendlyName(string capabilityName)
    {
        var baseName = BaseName(capabilityName);
        var entry = Find(capabilityName);
        if (entry is null)
        {
            return baseName.Replace('.', ' ');
        }

        if (!entry.Prefix.EndsWith('.'))
        {
            return entry.DisplayName;
        }

        var detail = baseName[entry.Prefix.Length..];
        var language = LanguageTag(capabilityName);
        return language is null ? $"{entry.DisplayName}: {detail}" : $"{entry.DisplayName}: {detail} ({language})";
    }

    /// <summary>Extracts the language tag from "Language.Basic~~~en-GB~0.0.1.0" (null when absent).</summary>
    public static string? LanguageTag(string? capabilityName)
    {
        if (string.IsNullOrWhiteSpace(capabilityName))
        {
            return null;
        }

        var parts = capabilityName.Split('~');
        return parts.Length >= 4 && parts[3].Length > 0 ? parts[3] : null;
    }

    /// <summary>Group heading for the capability (General, Language, RSAT, Other).</summary>
    public static string GroupFor(string capabilityName) => Find(capabilityName)?.Group ?? GroupOther;
}
