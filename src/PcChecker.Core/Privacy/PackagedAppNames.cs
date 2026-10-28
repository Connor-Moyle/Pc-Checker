using System.Text;

namespace PcChecker.Core.Privacy;

/// <summary>
/// Readable names for Store app package family names when Windows cannot give the real display name
/// ("Microsoft.WindowsCamera_8wekyb3d8bbwe" → "Camera"). Pure.
/// </summary>
public static class PackagedAppNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.WindowsCamera"] = "Camera",
        ["Microsoft.WindowsSoundRecorder"] = "Sound Recorder",
        ["Microsoft.WindowsMaps"] = "Maps",
        ["Microsoft.BingWeather"] = "Weather",
        ["Microsoft.SkypeApp"] = "Skype",
        ["MicrosoftTeams"] = "Microsoft Teams",
        ["MSTeams"] = "Microsoft Teams",
        ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub",
        ["Microsoft.YourPhone"] = "Phone Link",
        ["Microsoft.People"] = "People",
        ["Microsoft.Windows.Photos"] = "Photos",
        ["Microsoft.WindowsAlarms"] = "Clock",
        ["Microsoft.GetHelp"] = "Get Help",
        ["Microsoft.XboxGamingOverlay"] = "Xbox Game Bar",
        ["Microsoft.GamingApp"] = "Xbox",
        ["Microsoft.Windows.Cortana"] = "Cortana",
        ["Microsoft.Copilot"] = "Copilot",
        ["Microsoft.ScreenSketch"] = "Snipping Tool",
        ["Microsoft.WindowsStore"] = "Microsoft Store",
        ["Microsoft.MicrosoftEdge.Stable"] = "Microsoft Edge",
        ["Microsoft.Windows.ShellExperienceHost"] = "Windows Shell",
        ["Microsoft.Windows.CloudExperienceHost"] = "Windows setup and sign-in",
        ["Microsoft.Win32WebViewHost"] = "Desktop web viewer",
        ["5319275A.WhatsAppDesktop"] = "WhatsApp",
        ["SpotifyAB.SpotifyMusic"] = "Spotify",
        ["Clipchamp.Clipchamp"] = "Clipchamp",
        ["Microsoft.OutlookForWindows"] = "Outlook",
    };

    /// <summary>The package name part of a family name (everything before the publisher hash after '_').</summary>
    public static string PackageName(string familyName)
    {
        ArgumentNullException.ThrowIfNull(familyName);
        int underscore = familyName.IndexOf('_');
        return underscore > 0 ? familyName[..underscore] : familyName;
    }

    public static string FriendlyName(string familyName)
    {
        ArgumentNullException.ThrowIfNull(familyName);
        var package = PackageName(familyName);
        if (Known.TryGetValue(package, out var known))
        {
            return known;
        }

        // Last dotted part, with spaces between words: "Contoso.VideoChatPro" → "Video Chat Pro".
        var last = package.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? package;
        return SplitWords(last);
    }

    /// <summary>Inserts a space between a lower-case letter or digit and the next capital ("VideoChat" → "Video Chat").</summary>
    public static string SplitWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new StringBuilder(text.Length + 4);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1])))
            {
                result.Append(' ');
            }

            result.Append(c);
        }

        return result.ToString();
    }
}
