using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

/// <summary>Which servicing track an edition follows: Home and Pro get 24 months per Windows 11 version, Enterprise and Education 36.</summary>
public enum ServicingTrack
{
    Unknown,
    HomePro,
    EnterpriseEducation,
}

/// <summary>The answer for one Windows version: the end of security updates, or null when the table does not know it.</summary>
public sealed record ServicingInfo(string VersionName, DateOnly? EndOfServicing, ServicingTrack Track)
{
    public bool IsKnown => EndOfServicing is not null;
}

/// <summary>
/// A small end-of-servicing table for the Windows versions people still run, from Microsoft's lifecycle pages. When a
/// version or edition is not in the table the page says to check Microsoft's lifecycle page instead of guessing. Pure.
/// </summary>
public static class WindowsServicing
{
    /// <summary>Microsoft's lifecycle page for Windows 11 releases.</summary>
    public const string LifecycleUrl = "https://learn.microsoft.com/lifecycle/products/windows-11-home-and-pro";

    /// <summary>Days before the end date when the item turns yellow.</summary>
    public const int WarningDays = 90;

    // (Windows major, version) -> (Home/Pro end, Enterprise/Education end)
    private static readonly Dictionary<(int Major, string Version), (DateOnly HomePro, DateOnly EnterpriseEducation)> Table = new()
    {
        [(10, "21H2")] = (new DateOnly(2023, 6, 13), new DateOnly(2024, 6, 11)),
        [(10, "22H2")] = (new DateOnly(2025, 10, 14), new DateOnly(2025, 10, 14)),
        [(11, "21H2")] = (new DateOnly(2023, 10, 10), new DateOnly(2024, 10, 8)),
        [(11, "22H2")] = (new DateOnly(2024, 10, 8), new DateOnly(2025, 10, 14)),
        [(11, "23H2")] = (new DateOnly(2025, 11, 11), new DateOnly(2026, 11, 10)),
        [(11, "24H2")] = (new DateOnly(2026, 10, 13), new DateOnly(2027, 10, 12)),
        [(11, "25H2")] = (new DateOnly(2027, 10, 12), new DateOnly(2028, 10, 10)),
    };

    // Build number -> version name, for systems that do not store DisplayVersion.
    private static readonly Dictionary<int, string> Builds = new()
    {
        [19044] = "21H2",
        [19045] = "22H2",
        [22000] = "21H2",
        [22621] = "22H2",
        [22631] = "23H2",
        [26100] = "24H2",
        [26200] = "25H2",
    };

    /// <summary>Windows 11 starts at build 22000; the registry's ProductName still says "Windows 10" there.</summary>
    public static int MajorVersion(int build) => build >= 22000 ? 11 : 10;

    /// <summary>The version name ("24H2"): DisplayVersion when present, otherwise looked up from the build.</summary>
    public static string? VersionName(string? displayVersion, int build)
    {
        if (!string.IsNullOrWhiteSpace(displayVersion))
        {
            return displayVersion.Trim().ToUpperInvariant();
        }

        return Builds.TryGetValue(build, out var name) ? name : null;
    }

    /// <summary>Maps an EditionID ("Core", "Professional", "Enterprise"…) to its servicing track. LTSC and server editions are unknown.</summary>
    public static ServicingTrack TrackOf(string? editionId)
    {
        if (string.IsNullOrWhiteSpace(editionId))
        {
            return ServicingTrack.Unknown;
        }

        var id = editionId.Trim();
        if (id.StartsWith("EnterpriseS", StringComparison.OrdinalIgnoreCase) || id.StartsWith("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Server", StringComparison.OrdinalIgnoreCase))
        {
            return ServicingTrack.Unknown;
        }

        if (id.StartsWith("Core", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Professional", StringComparison.OrdinalIgnoreCase)
            || id.Equals("Home", StringComparison.OrdinalIgnoreCase))
        {
            return ServicingTrack.HomePro;
        }

        if (id.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Education", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase))
        {
            return ServicingTrack.EnterpriseEducation;
        }

        return ServicingTrack.Unknown;
    }

    /// <summary>The end of security updates for this Windows build, version and edition.</summary>
    public static ServicingInfo Lookup(int build, string? displayVersion, string? editionId)
    {
        int major = MajorVersion(build);
        var version = VersionName(displayVersion, build);
        var track = TrackOf(editionId);
        string name = version is null ? $"Windows {major} (build {build})" : $"Windows {major} {version}";
        if (version is null || track == ServicingTrack.Unknown || !Table.TryGetValue((major, version), out var ends))
        {
            return new ServicingInfo(name, null, track);
        }

        return new ServicingInfo(name, track == ServicingTrack.HomePro ? ends.HomePro : ends.EnterpriseEducation, track);
    }

    /// <summary>Red after the end date, yellow within <see cref="WarningDays"/>, green otherwise, grey when unknown.</summary>
    public static HealthStatus Status(ServicingInfo info, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.EndOfServicing is not { } end)
        {
            return HealthStatus.Unknown;
        }

        if (today > end)
        {
            return HealthStatus.Critical;
        }

        return end.DayNumber - today.DayNumber <= WarningDays ? HealthStatus.Warning : HealthStatus.Good;
    }
}
