using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// Compares driver versions across vendor numbering schemes. Windows reports every driver with a four-part version;
/// NVIDIA publishes a release number that is the last five digits of that version (32.0.15.6094 is 560.94); AMD
/// publishes an Adrenalin version (24.12.1) that cannot be derived from the Windows version (32.0.21013.1000), so
/// AMD comparisons need the Adrenalin version the AMD driver records in the registry. Pure.
/// </summary>
public static class DriverVersionMath
{
    /// <summary>
    /// NVIDIA release number from a Windows driver version: the third part and the four-digit fourth part joined,
    /// last five digits, dot before the last two. 32.0.15.6094 → 560.94; 32.0.16.2101 → 621.01. Null when the text is
    /// not a four-part version.
    /// </summary>
    public static string? NvidiaReleaseFromWindows(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion))
        {
            return null;
        }

        var parts = windowsVersion.Trim().Split('.');
        if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        if (parts[3].Length > 4 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5)
        {
            return null;
        }

        var last5 = digits[^5..];
        int major = int.Parse(last5[..3], CultureInfo.InvariantCulture);
        return major.ToString(CultureInfo.InvariantCulture) + "." + last5[3..];
    }

    /// <summary>NVIDIA release number as a sortable integer (621.01 → 62101). Null when not of the form NNN.NN.</summary>
    public static int? NvidiaKey(string? release)
    {
        if (string.IsNullOrWhiteSpace(release))
        {
            return null;
        }

        var parts = release.Trim().Split('.');
        if (parts.Length != 2 || parts[0].Length is < 1 or > 4 || parts[1].Length is < 1 or > 2
            || !parts.All(p => p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        int major = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int minor = int.Parse(parts[1].PadRight(2, '0'), CultureInfo.InvariantCulture);
        return major * 100 + minor;
    }

    /// <summary>True for an Adrenalin-style version (YY.M.N with a two-digit year from 17), false for a Windows version (31.0.x.y).</summary>
    public static bool IsAdrenalinVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var parts = version.Trim().Split('.');
        if (parts.Length is < 2 or > 3 || !parts.All(p => p.Length is > 0 and <= 2 && p.All(char.IsAsciiDigit)))
        {
            return false;
        }

        int year = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int month = int.Parse(parts[1], CultureInfo.InvariantCulture);
        return year is >= 17 and <= 99 && month is >= 1 and <= 12;
    }

    /// <summary>
    /// Compares an offered version with what the device has installed, in the offer's scheme. Null when the two cannot
    /// be compared (an Adrenalin version against a device whose Adrenalin version is unknown, an unparseable value).
    /// Positive when the offer is newer.
    /// </summary>
    public static int? Compare(string? offered, DriverVersionScheme scheme, DriverDeviceRef device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(offered))
        {
            return null;
        }

        switch (scheme)
        {
            case DriverVersionScheme.Windows:
                return VersionText.TryParse(offered, out _) && VersionText.TryParse(device.InstalledVersion, out _)
                    ? Math.Sign(VersionText.Compare(offered, device.InstalledVersion))
                    : null;

            case DriverVersionScheme.NvidiaRelease:
            {
                var installed = device.InstalledVendorScheme == DriverVersionScheme.NvidiaRelease
                    ? device.InstalledVendorVersion
                    : NvidiaReleaseFromWindows(device.InstalledVersion);
                return NvidiaKey(offered) is { } o && NvidiaKey(installed) is { } i ? Math.Sign(o.CompareTo(i)) : null;
            }

            case DriverVersionScheme.Adrenalin:
            {
                var installed = device.InstalledVendorScheme == DriverVersionScheme.Adrenalin ? device.InstalledVendorVersion : null;
                return IsAdrenalinVersion(offered) && IsAdrenalinVersion(installed) ? Math.Sign(VersionText.Compare(offered, installed)) : null;
            }

            case DriverVersionScheme.Vendor:
            {
                var installed = device.InstalledVendorScheme == DriverVersionScheme.Vendor ? device.InstalledVendorVersion : null;
                return Platform.VendorVersionText.Compare(offered, installed);
            }

            default:
                return null;
        }
    }

    /// <summary>Maps <see cref="Compare"/> to the comparison shown to the user.</summary>
    public static DriverVersionComparison Classify(string? offered, DriverVersionScheme scheme, DriverDeviceRef device) =>
        Compare(offered, scheme, device) switch
        {
            > 0 => DriverVersionComparison.Newer,
            0 => DriverVersionComparison.Same,
            < 0 => DriverVersionComparison.Older,
            null => DriverVersionComparison.Unknown,
        };

    /// <summary>
    /// Orders two offers for the same device: positive when <paramref name="a"/> is newer. Versions are compared when
    /// both can be put in one scheme (a Windows Update NVIDIA driver is converted to NVIDIA's numbering); otherwise
    /// the release dates decide. Null when neither works.
    /// </summary>
    public static int? CompareOffers(DriverOffer a, DriverOffer b, DriverDeviceRef device)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(device);
        if (a.Scheme == DriverVersionScheme.Vendor && b.Scheme == DriverVersionScheme.Vendor
            && Platform.VendorVersionText.Compare(a.Version, b.Version) is { } vendor)
        {
            return vendor;
        }

        if (ComparableKey(a, b.Scheme) is { } ka && ComparableKey(b, b.Scheme) is { } kb)
        {
            return Math.Sign(VersionText.Compare(ka, kb));
        }

        if (ComparableKey(b, a.Scheme) is { } kb2 && ComparableKey(a, a.Scheme) is { } ka2)
        {
            return Math.Sign(VersionText.Compare(ka2, kb2));
        }

        if (a.ReleaseDate is { } da && b.ReleaseDate is { } db)
        {
            return Math.Sign(da.Date.CompareTo(db.Date));
        }

        return null;
    }

    /// <summary>The offer's version expressed in <paramref name="target"/>, or null when it cannot be.</summary>
    private static string? ComparableKey(DriverOffer offer, DriverVersionScheme target)
    {
        if (offer.Version is null || offer.Scheme == DriverVersionScheme.None || target == DriverVersionScheme.None)
        {
            return null;
        }

        if (target == DriverVersionScheme.Vendor)
        {
            // Vendor numbers are compared by their own rules (CompareOffers handles two of them), not by VersionText.
            return null;
        }

        if (offer.Scheme == target)
        {
            return target == DriverVersionScheme.NvidiaRelease
                ? NvidiaKey(offer.Version)?.ToString(CultureInfo.InvariantCulture)
                : offer.Version;
        }

        if (offer.Scheme == DriverVersionScheme.Windows && target == DriverVersionScheme.NvidiaRelease)
        {
            return NvidiaKey(NvidiaReleaseFromWindows(offer.Version))?.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }
}
