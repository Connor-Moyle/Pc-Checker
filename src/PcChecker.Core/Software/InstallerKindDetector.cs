using System.Text;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Software;

/// <summary>
/// Pure heuristics that recognise the installer technology from registry strings and, optionally, the first bytes of
/// the uninstaller executable. Knowing the technology lets us pick the right "silent" switches.
/// </summary>
public static partial class InstallerKindDetector
{
    private const string NsisMarker = "Nullsoft";

    [GeneratedRegex(@"unins\d{3}\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex InnoUninstaller();

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCode();

    /// <summary>
    /// Detects the installer kind. <paramref name="uninstallerHeader"/> is the first chunk (up to 64 KB) of the
    /// uninstaller file when the caller could read it; it is used to confirm NSIS.
    /// </summary>
    public static InstallerKind Detect(string? uninstallString, string? quietUninstallString, bool windowsInstallerFlag, ReadOnlySpan<byte> uninstallerHeader = default)
    {
        var text = (uninstallString ?? string.Empty) + " " + (quietUninstallString ?? string.Empty);

        if (windowsInstallerFlag || text.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerKind.Msi;
        }

        if (InnoUninstaller().IsMatch(text))
        {
            return InstallerKind.InnoSetup;
        }

        if (text.Contains("InstallShield", StringComparison.OrdinalIgnoreCase)
            || text.Contains("-runfromtemp", StringComparison.OrdinalIgnoreCase)
            || text.Contains("_isdel", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("setup.exe", StringComparison.OrdinalIgnoreCase) && ProductCode().IsMatch(text)))
        {
            return InstallerKind.InstallShield;
        }

        if (text.Contains("Update.exe", StringComparison.OrdinalIgnoreCase) && text.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerKind.Squirrel;
        }

        bool genericUninstaller = text.Contains("uninst.exe", StringComparison.OrdinalIgnoreCase)
            || text.Contains("uninstall.exe", StringComparison.OrdinalIgnoreCase)
            || text.Contains("uninstaller.exe", StringComparison.OrdinalIgnoreCase);

        if (!uninstallerHeader.IsEmpty)
        {
            if (LooksLikeNsis(uninstallerHeader))
            {
                return InstallerKind.Nsis;
            }

            if (LooksLikeInno(uninstallerHeader))
            {
                return InstallerKind.InnoSetup;
            }

            return InstallerKind.Unknown;
        }

        // Without the file header the common uninstaller names are most often NSIS.
        return genericUninstaller ? InstallerKind.Nsis : InstallerKind.Unknown;
    }

    /// <summary>True when the executable header carries the NSIS "Nullsoft" marker.</summary>
    public static bool LooksLikeNsis(ReadOnlySpan<byte> header) => Contains(header, NsisMarker);

    /// <summary>True when the executable header carries the Inno Setup marker.</summary>
    public static bool LooksLikeInno(ReadOnlySpan<byte> header) => Contains(header, "Inno Setup");

    private static bool Contains(ReadOnlySpan<byte> haystack, string marker)
    {
        if (haystack.IsEmpty)
        {
            return false;
        }

        var ascii = Encoding.ASCII.GetBytes(marker);
        if (haystack.IndexOf(ascii) >= 0)
        {
            return true;
        }

        var unicode = Encoding.Unicode.GetBytes(marker);
        return haystack.IndexOf(unicode) >= 0;
    }
}
