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
}
