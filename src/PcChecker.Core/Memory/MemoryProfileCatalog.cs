namespace PcChecker.Core.Memory;

/// <summary>Built-in memory profiles. Page file sizes are filled in from the machine's RAM at apply time when null.</summary>
public static class MemoryProfileCatalog
{
    public const string BalancedId = "memory.profile.balanced";
    public const string GamingId = "memory.profile.gaming";
    public const string LowMemoryId = "memory.profile.lowmemory";
    public const string WorkstationId = "memory.profile.workstation";

    private const long Gb = 1024L * 1024 * 1024;

    public static IReadOnlyList<MemoryProfile> All { get; } =
    [
        new(BalancedId, "Balanced (Windows defaults)", "Memory compression on, SysMain on, page file managed by Windows. The right choice for most PCs.", true, true, true, null, null, false),
        new(GamingId, "Gaming", "Keeps compression and SysMain on and sets a fixed page file of 1.5× RAM (at least 8 GB) on the Windows drive, normally the fastest SSD, so games never stutter while the file grows.", true, true, false, null, null, false),
        new(LowMemoryId, "Low-memory PC (8 GB or less)", "Compression on and a 2× RAM page file so a small system stays responsive with many tabs open.", true, true, false, null, null, false),
        new(WorkstationId, "Workstation (advanced)", "Compression off and a 1× RAM page file for machines with lots of RAM that run heavy single applications.", false, true, false, null, null, true),
    ];

    /// <summary>Finds a profile by id (case-insensitive) or null.</summary>
    public static MemoryProfile? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The profile to suggest: Low-memory for 8 GB or less, otherwise Balanced.</summary>
    public static MemoryProfile Recommended(long totalRamBytes) =>
        totalRamBytes > 0 && totalRamBytes <= 8 * Gb ? All[2] : All[0];

    /// <summary>
    /// True when the machine's current settings already match the profile (page file size is compared loosely:
    /// any fixed-size file counts as matching a fixed-size profile).
    /// </summary>
    public static bool Matches(MemoryProfile profile, MemoryOverview overview)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(overview);
        if (profile.MemoryCompression != overview.MemoryCompressionEnabled || profile.SysMain != overview.SysMainEnabled)
        {
            return false;
        }

        if (profile.AutomaticPageFile)
        {
            return overview.AutomaticPageFile;
        }

        if (overview.AutomaticPageFile || overview.PageFiles.Count == 0)
        {
            return false;
        }

        var expected = PageFileRules.SizeForProfile(profile, overview.TotalBytes);
        return expected is null || overview.PageFiles.Any(p => !p.SystemManaged && Math.Abs(p.MaximumMb - expected.Value.MaximumMb) <= 64);
    }
}
