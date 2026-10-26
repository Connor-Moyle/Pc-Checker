namespace PcChecker.Core.Memory;

/// <summary>Page file sizing recommendations and misconfiguration checks. Pure.</summary>
public static class PageFileRules
{
    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>Recommended initial/maximum page file size for a fixed-size file: 1.5× RAM (min 8 GB) capped at 2× RAM for small systems.</summary>
    public static (int InitialMb, int MaximumMb) Recommend(long totalRamBytes)
    {
        long ramMb = Math.Max(1, totalRamBytes / (1024 * 1024));
        long initial = Math.Max(8 * 1024, ramMb * 3 / 2);
        long maximum = Math.Max(initial, ramMb * 2);
        if (ramMb >= 32 * 1024)
        {
            // Plenty of RAM: a large page file only wastes disk; keep it at the 1.5× floor.
            maximum = initial;
        }

        return ((int)Math.Min(initial, int.MaxValue), (int)Math.Min(maximum, int.MaxValue));
    }

    /// <summary>Plain-English warnings about the current page file setup.</summary>
    public static IReadOnlyList<string> Evaluate(long totalRamBytes, bool automaticPageFile, IReadOnlyList<PageFileInfo> pageFiles)
    {
        var warnings = new List<string>();
        if (automaticPageFile)
        {
            return warnings;
        }

        if (pageFiles is null || pageFiles.Count == 0)
        {
            warnings.Add("The page file is turned off. Windows needs one for crash dumps and to avoid out-of-memory errors; let Windows manage it.");
            return warnings;
        }

        long ramMb = totalRamBytes / (1024 * 1024);
        foreach (var pf in pageFiles)
        {
            if (pf.SystemManaged)
            {
                continue;
            }

            if (pf.MaximumMb > 0 && pf.MaximumMb < Math.Min(2048, ramMb / 4))
            {
                warnings.Add($"The page file on {pf.Drive} is very small ({pf.MaximumMb} MB). Games and browsers may run out of memory.");
            }

            if (pf.InitialMb != pf.MaximumMb && pf.MaximumMb > 0)
            {
                warnings.Add($"The page file on {pf.Drive} grows on demand ({pf.InitialMb}–{pf.MaximumMb} MB), which can cause stutter; a fixed size is smoother.");
            }

            if (ramMb > 0 && pf.MaximumMb > ramMb * 4)
            {
                warnings.Add($"The page file on {pf.Drive} is unusually large ({pf.MaximumMb} MB); it only wastes disk space.");
            }
        }

        return warnings;
    }

    /// <summary>
    /// The fixed page file size a profile asks for, in MB: explicit values from the profile, otherwise a multiple of RAM
    /// (Gaming 1.5× with an 8 GB floor, Low-memory 2×, Workstation 1×). Returns null for system-managed profiles.
    /// </summary>
    public static (int InitialMb, int MaximumMb)? SizeForProfile(MemoryProfile profile, long totalRamBytes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AutomaticPageFile)
        {
            return null;
        }

        if (profile.PageFileInitialMb is { } initial && profile.PageFileMaxMb is { } max)
        {
            return (Math.Max(16, initial), Math.Max(Math.Max(16, initial), max));
        }

        long ramMb = Math.Max(1024, totalRamBytes / (1024 * 1024));
        long size = profile.Id switch
        {
            "memory.profile.lowmemory" => ramMb * 2,
            "memory.profile.workstation" => ramMb,
            _ => Math.Max(8 * 1024, ramMb * 3 / 2),
        };
        int clamped = (int)Math.Min(size, int.MaxValue);
        return (clamped, clamped);
    }

    /// <summary>Checks a manual page file entry before it is applied. Returns null when the values are acceptable.</summary>
    public static string? Validate(string? drive, bool systemManaged, int? initialMb, int? maxMb, long totalRamBytes)
    {
        if (string.IsNullOrWhiteSpace(drive) || drive.Trim().Length < 2 || !char.IsLetter(drive.Trim()[0]) || drive.Trim()[1] != ':')
        {
            return "Choose a drive letter such as C:.";
        }

        if (systemManaged)
        {
            return null;
        }

        if (initialMb is null || maxMb is null)
        {
            return "Enter both an initial and a maximum size in MB, or let Windows manage the page file.";
        }

        if (initialMb < 16)
        {
            return "The initial size must be at least 16 MB (Windows needs a small page file for crash reports).";
        }

        if (maxMb < initialMb)
        {
            return "The maximum size must be at least as large as the initial size.";
        }

        long ramMb = totalRamBytes / (1024 * 1024);
        if (ramMb > 0 && maxMb > ramMb * 4)
        {
            return $"{maxMb} MB is more than four times your RAM ({ramMb} MB). That only wastes disk space; {Recommend(totalRamBytes).MaximumMb} MB is plenty.";
        }

        return null;
    }

    /// <summary>
    /// Picks the drive for a fixed page file: the drive that already hosts one, otherwise the Windows drive (normally
    /// the fastest SSD in the PC). Drives are "C:" style strings.
    /// </summary>
    public static string ChooseDrive(IReadOnlyList<PageFileInfo> existing, string systemDrive)
    {
        var current = existing?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Drive))?.Drive;
        string chosen = string.IsNullOrWhiteSpace(current) ? systemDrive : current;
        chosen = chosen.Trim();
        if (chosen.Length >= 2 && char.IsLetter(chosen[0]))
        {
            return char.ToUpperInvariant(chosen[0]) + ":";
        }

        return "C:";
    }
}
