using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// Friendly names for the apps in App history. Windows' records name programs by their device path
/// (<c>\device\harddiskvolume3\program files\...\steam.exe</c>), Store apps by package name
/// (<c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0</c>) and some services by their short name (<c>wuauserv</c>); this turns
/// them into a drive-letter path, a key that groups the same app, and a readable name. Windows adds the description
/// and publisher from the program file when it can read it.
/// </summary>
public static partial class AppNames
{
    private static readonly Dictionary<string, string> KnownServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wuauserv"] = "Windows Update",
        ["usosvc"] = "Windows Update",
        ["bits"] = "Background Intelligent Transfer Service",
        ["dosvc"] = "Delivery Optimization",
        ["wsappx"] = "Microsoft Store installs",
        ["cryptsvc"] = "Cryptographic Services",
        ["diagtrack"] = "Connected User Experiences and Telemetry",
        ["dnscache"] = "DNS Client",
        ["system"] = "System",
        ["windefend"] = "Microsoft Defender Antivirus",
    };

    /// <summary>
    /// Replaces a device path's volume (<c>\Device\HarddiskVolume3</c>) by its drive letter using
    /// <paramref name="deviceToDrive"/> (device name → "C:", as QueryDosDevice reports it). Already-DOS paths come back
    /// unchanged; null when the volume is not mounted under a letter.
    /// </summary>
    public static string? ToDosPath(string? path, IReadOnlyDictionary<string, string> deviceToDrive)
    {
        ArgumentNullException.ThrowIfNull(deviceToDrive);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            return path;
        }

        foreach (var (device, drive) in deviceToDrive)
        {
            var prefix = device.TrimEnd('\\') + "\\";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return drive.TrimEnd('\\') + "\\" + path[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>The grouping key of an app: its path or name in lower case, so the same program always lands on one row.</summary>
    public static string KeyFor(string value) => value.Trim().ToLowerInvariant();

    /// <summary>Who an id stands for, before the program file is looked at.</summary>
    public static AppIdentity Describe(SrumIdEntry entry, IReadOnlyDictionary<string, string> deviceToDrive)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var value = entry.Value;
        if (value.StartsWith("!!", StringComparison.Ordinal))
        {
            // "!!name.exe!date!hash!": the program's file name sits between the first marks.
            int end = value.IndexOf('!', 2);
            value = end > 2 ? value[2..end] : value.Trim('!');
        }

        if (entry.Kind == SrumIdKind.Program || value.Contains('\\', StringComparison.Ordinal) || value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var path = ToDosPath(value, deviceToDrive);
            return new AppIdentity(KeyFor(path ?? value), ProgramName(path ?? value), path, null);
        }

        return new AppIdentity(KeyFor(value), FriendlyName(value), null, null);
    }

    /// <summary>A program's name from its file: "chrome.exe" → "Chrome", "steamwebhelper.exe" → "Steamwebhelper".</summary>
    public static string ProgramName(string path)
    {
        var file = path;
        int slash = file.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            file = file[(slash + 1)..];
        }

        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            file = file[..^4];
        }

        if (file.Length == 0)
        {
            return path;
        }

        return char.ToUpper(file[0], CultureInfo.InvariantCulture) + file[1..];
    }

    /// <summary>
    /// A readable name for a service or Store package: "wuauserv" → "Windows Update",
    /// "SpotifyAB.SpotifyMusic_zpdnekdrzrea0" → "Spotify Music", "Microsoft.WindowsStore_8wekyb3d8bbwe" → "Windows Store".
    /// </summary>
    public static string FriendlyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        var trimmed = value.Trim();
        if (KnownServices.TryGetValue(trimmed, out var known))
        {
            return known;
        }

        if (!PackageName().IsMatch(trimmed))
        {
            return trimmed;
        }

        var name = trimmed[..trimmed.IndexOf('_', StringComparison.Ordinal)];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
        {
            name = name[(dot + 1)..];
        }

        return SplitWords(name);
    }

    /// <summary>"SpotifyMusic" → "Spotify Music"; runs of capitals stay together ("XboxGameUI" → "Xbox Game UI").</summary>
    public static string SplitWords(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool boundary = i > 0 && char.IsUpper(c)
                && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (boundary)
            {
                text.Append(' ');
            }

            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>A package family or full name: "Name.Part_publisherid" or "Name_1.2.3.4_x64__publisherid".</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]*_[A-Za-z0-9._\-]*[a-z0-9]{13}$")]
    private static partial Regex PackageName();
}
