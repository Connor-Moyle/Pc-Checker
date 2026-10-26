using System.Text;

namespace PcChecker.Core.Software;

/// <summary>
/// Turns Store package names such as <c>Microsoft.WindowsCalculator</c> into readable titles ("Windows Calculator")
/// without the slow manifest lookup, and parses certificate-style publisher strings.
/// </summary>
public static class AppxNameHumanizer
{
    /// <summary>Well-known packages whose technical names say nothing useful.</summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.549981C3F5F10"] = "Cortana",
        ["Microsoft.WindowsStore"] = "Microsoft Store",
        ["Microsoft.GamingApp"] = "Xbox",
        ["Microsoft.XboxGamingOverlay"] = "Xbox Game Bar",
        ["Microsoft.XboxIdentityProvider"] = "Xbox Identity Provider",
        ["Microsoft.XboxSpeechToTextOverlay"] = "Xbox Game Speech Window",
        ["Microsoft.Xbox.TCUI"] = "Xbox Live in-game experience",
        ["Microsoft.WindowsCalculator"] = "Calculator",
        ["Microsoft.WindowsCamera"] = "Camera",
        ["Microsoft.WindowsAlarms"] = "Clock",
        ["Microsoft.WindowsMaps"] = "Maps",
        ["Microsoft.WindowsNotepad"] = "Notepad",
        ["Microsoft.WindowsTerminal"] = "Windows Terminal",
        ["Microsoft.WindowsSoundRecorder"] = "Sound Recorder",
        ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub",
        ["Microsoft.Windows.Photos"] = "Photos",
        ["Microsoft.ZuneMusic"] = "Media Player",
        ["Microsoft.ZuneVideo"] = "Movies & TV",
        ["Microsoft.MicrosoftStickyNotes"] = "Sticky Notes",
        ["Microsoft.MicrosoftSolitaireCollection"] = "Microsoft Solitaire Collection",
        ["Microsoft.MicrosoftOfficeHub"] = "Microsoft 365 (Office)",
        ["Microsoft.MicrosoftEdge.Stable"] = "Microsoft Edge",
        ["Microsoft.BingNews"] = "News",
        ["Microsoft.BingWeather"] = "Weather",
        ["Microsoft.BingSearch"] = "Bing Search",
        ["Microsoft.GetHelp"] = "Get Help",
        ["Microsoft.Getstarted"] = "Tips",
        ["Microsoft.People"] = "People",
        ["Microsoft.Paint"] = "Paint",
        ["Microsoft.ScreenSketch"] = "Snipping Tool",
        ["Microsoft.SecHealthUI"] = "Windows Security",
        ["Microsoft.StorePurchaseApp"] = "Store Purchase App",
        ["Microsoft.Todos"] = "Microsoft To Do",
        ["Microsoft.PowerAutomateDesktop"] = "Power Automate",
        ["Microsoft.OutlookForWindows"] = "Outlook",
        ["Microsoft.YourPhone"] = "Phone Link",
        ["MicrosoftWindows.Client.WebExperience"] = "Windows Web Experience Pack (Widgets)",
        ["MicrosoftCorporationII.QuickAssist"] = "Quick Assist",
        ["MicrosoftTeams"] = "Microsoft Teams",
        ["MSTeams"] = "Microsoft Teams",
        ["Clipchamp.Clipchamp"] = "Clipchamp",
        ["SpotifyAB.SpotifyMusic"] = "Spotify",
        ["Microsoft.DesktopAppInstaller"] = "App Installer (winget)",
        ["Microsoft.HEIFImageExtension"] = "HEIF Image Extensions",
        ["Microsoft.WebMediaExtensions"] = "Web Media Extensions",
        ["Microsoft.VP9VideoExtensions"] = "VP9 Video Extensions",
        ["Microsoft.RawImageExtension"] = "Raw Image Extension",
    };

    /// <summary>Returns a human-readable title for a package name.</summary>
    public static string Humanize(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return "Unknown app";
        }

        var name = packageName.Trim();
        if (KnownNames.TryGetValue(name, out var known))
        {
            return known;
        }

        // Strip the publisher prefix ("Contoso.AppName" → "AppName"); keep the last meaningful segment.
        var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var candidate = segments.Length > 1 ? segments[^1] : name;
        if (segments.Length > 2 && candidate.Length <= 3)
        {
            candidate = segments[^2] + " " + candidate;
        }

        if (candidate.StartsWith("Microsoft", StringComparison.Ordinal) && candidate.Length > 9 && char.IsUpper(candidate[9]))
        {
            candidate = candidate[9..];
        }

        return SplitCamelCase(candidate);
    }

    /// <summary>Resolves the display name: when it is a resource reference (<c>ms-resource:</c>) fall back to the humanized name.</summary>
    public static string ResolveDisplayName(string? displayName, string? packageName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith("@{", StringComparison.Ordinal))
        {
            return Humanize(packageName);
        }

        return displayName.Trim();
    }

    /// <summary>Extracts the common name from "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond" strings.</summary>
    public static string? ParsePublisherCommonName(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return null;
        }

        var text = publisher.Trim();
        var parts = text.Split(',');
        foreach (var part in parts)
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                var cn = p[3..].Trim().Trim('"');
                return cn.Length == 0 ? null : cn;
            }
        }

        foreach (var part in parts)
        {
            var p = part.Trim();
            if (p.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
            {
                var o = p[2..].Trim().Trim('"');
                return o.Length == 0 ? null : o;
            }
        }

        return text;
    }

    /// <summary>"WindowsCalculator" → "Windows Calculator", "XboxGamingOverlay" → "Xbox Gaming Overlay", "GetHelp" → "Get Help".</summary>
    public static string SplitCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 6);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (i > 0)
            {
                char prev = value[i - 1];
                bool boundary =
                    (char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev)))
                    || (char.IsUpper(c) && char.IsUpper(prev) && i + 1 < value.Length && char.IsLower(value[i + 1]))
                    || (char.IsDigit(c) && char.IsLetter(prev));
                if (boundary && prev != ' ')
                {
                    sb.Append(' ');
                }
            }

            sb.Append(c == '_' || c == '-' ? ' ' : c);
        }

        return sb.ToString().Trim();
    }
}
