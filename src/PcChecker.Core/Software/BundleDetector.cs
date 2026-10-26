namespace PcChecker.Core.Software;

/// <summary>
/// Pure heuristics that group programs installed together: same install time with a different publisher,
/// install folder nested under another program, and a list of known bundled extras.
/// </summary>
public static class BundleDetector
{
    public static readonly TimeSpan InstallWindow = TimeSpan.FromMinutes(3);

    /// <summary>Names (substring, case-insensitive) that are commonly installed alongside other software.</summary>
    public static IReadOnlyList<string> KnownBundledExtras { get; } =
    [
        "McAfee WebAdvisor", "McAfee Security Scan", "Norton Security Scan", "Avast Secure Browser", "AVG Secure Browser",
        "Opera GX", "Opera Browser", "Opera Stable", "Yandex Browser", "Ask Toolbar", "Bing Bar", "Babylon Toolbar",
        "Conduit", "MyWebSearch", "Search Protect", "Driver Booster", "WinZip Driver Updater", "PC Accelerate",
        "Advanced SystemCare", "Web Companion", "Lavasoft", "CCleaner Browser", "Dropbox Promotion", "ByteFence",
        "Chromium", "Weather Bug", "TuneUp Utilities", "Reimage", "Segurazo", "OneLaunch", "Wave Browser",
    ];

    /// <summary>Publishers whose "bundled" installs are runtime dependencies rather than bloat.</summary>
    public static bool IsRuntimeDependency(InstalledProgram program)
    {
        var n = program.Name;
        return n.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
            || n.Contains(".NET Runtime", StringComparison.OrdinalIgnoreCase)
            || n.Contains(".NET Framework", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Windows Desktop Runtime", StringComparison.OrdinalIgnoreCase)
            || n.Contains("ASP.NET Core", StringComparison.OrdinalIgnoreCase)
            || n.Contains("DirectX", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Vulkan Run Time", StringComparison.OrdinalIgnoreCase)
            || n.Contains("OpenAL", StringComparison.OrdinalIgnoreCase)
            || n.Contains("PhysX", StringComparison.OrdinalIgnoreCase)
            || n.Contains("WebView2", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsKnownBundledExtra(InstalledProgram program) =>
        KnownBundledExtras.Any(k => program.Name.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the install date carries a time of day, so it can be compared to the minute.</summary>
    public static bool HasInstallTime(InstalledProgram program) =>
        program.InstallDate is { } date && date.TimeOfDay != TimeSpan.Zero;

    /// <summary>Finds bundle groups. The parent is the program that is not itself a known extra and has the earliest install time or the outer folder.</summary>
    public static IReadOnlyList<BundleGroup> Detect(IReadOnlyList<InstalledProgram> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        var groups = new Dictionary<string, (InstalledProgram Parent, List<InstalledProgram> Children, List<string> Reasons)>();

        void Add(InstalledProgram parent, InstalledProgram child, string reason)
        {
            if (parent.Id == child.Id)
            {
                return;
            }

            if (!groups.TryGetValue(parent.Id, out var g))
            {
                g = (parent, [], []);
                groups[parent.Id] = g;
            }

            if (g.Children.All(c => c.Id != child.Id))
            {
                g.Children.Add(child);
                g.Reasons.Add(reason);
            }
        }

        // 1. Install folder nested under another program's folder.
        foreach (var child in programs)
        {
            var childLocation = Normalize(child.InstallLocation);
            if (childLocation is null)
            {
                continue;
            }

            InstalledProgram? best = null;
            int bestLength = -1;
            foreach (var parent in programs)
            {
                var parentLocation = Normalize(parent.InstallLocation);
                if (parentLocation is null || parent.Id == child.Id || parentLocation.Length >= childLocation.Length)
                {
                    continue;
                }

                if (childLocation.StartsWith(parentLocation + "\\", StringComparison.OrdinalIgnoreCase) && parentLocation.Length > bestLength)
                {
                    best = parent;
                    bestLength = parentLocation.Length;
                }
            }

            if (best is not null)
            {
                Add(best, child, "installed inside the same folder");
            }
        }

        // 2. Installed within a few minutes of another program by a different publisher. The registry's InstallDate is
        // usually a bare date (yyyyMMdd), which says nothing about minutes: two programs installed on the same day are
        // not a bundle, so only timestamps that carry a time of day take part.
        var dated = programs.Where(p => HasInstallTime(p) && p.Source != SoftwareSource.StoreApp).OrderBy(p => p.InstallDate).ToList();
        for (int i = 0; i < dated.Count; i++)
        {
            for (int j = i + 1; j < dated.Count; j++)
            {
                var a = dated[i];
                var b = dated[j];
                if (b.InstallDate!.Value - a.InstallDate!.Value > InstallWindow)
                {
                    break;
                }

                // The same publisher usually means one suite, except when one half is a known bundled extra
                // (an antivirus that brings its own browser, for example).
                if (SamePublisher(a, b) && !IsKnownBundledExtra(a) && !IsKnownBundledExtra(b))
                {
                    continue;
                }

                // The known extra (or the later one) is the child.
                var (parent, child) = IsKnownBundledExtra(a) && !IsKnownBundledExtra(b) ? (b, a) : (a, b);
                var reason = IsKnownBundledExtra(child)
                    ? "known bundled extra installed at the same time"
                    : IsRuntimeDependency(child) ? "runtime dependency installed at the same time" : "installed at the same time by a different publisher";
                Add(parent, child, reason);
            }
        }

        return groups.Values
            .Select(g => new BundleGroup(g.Parent, g.Children, string.Join("; ", g.Reasons.Distinct())))
            .OrderBy(g => g.Parent.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SamePublisher(InstalledProgram a, InstalledProgram b)
    {
        if (string.IsNullOrWhiteSpace(a.Publisher) || string.IsNullOrWhiteSpace(b.Publisher))
        {
            return false;
        }

        return string.Equals(a.Publisher.Trim(), b.Publisher.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        return p.Length == 0 ? null : p;
    }
}
