namespace PcChecker.Core.Memory;

/// <summary>Built-in memory profiles. Page file sizes are filled in from the machine's RAM at apply time when null.</summary>
public static class MemoryProfileCatalog
{
    public static IReadOnlyList<MemoryProfile> All { get; } =
    [
        new("memory.profile.balanced", "Balanced (Windows defaults)", "Memory compression on, SysMain on, page file managed by Windows. The right choice for most PCs.", true, true, true, null, null, false),
        new("memory.profile.gaming", "Gaming", "Keeps compression and SysMain on and sets a fixed page file of 1.5× RAM (at least 8 GB) on the fastest SSD so games never stutter while the file grows.", true, true, false, null, null, false),
        new("memory.profile.lowmemory", "Low-memory PC (8 GB or less)", "Compression on and a 2× RAM page file so a small system stays responsive with many tabs open.", true, true, false, null, null, false),
        new("memory.profile.workstation", "Workstation (advanced)", "Compression off and a 1× RAM page file for machines with lots of RAM that run heavy single applications.", false, true, false, null, null, true),
    ];
}
