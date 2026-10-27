namespace PcChecker.Core.Shell;

/// <summary>
/// Simple versus Advanced mode in the shell: which pages mostly matter to experienced users (the menu marks them with an
/// "Advanced" chip; every page stays visible either way), and when the one-time "You're in Simple mode" hint shows.
/// </summary>
public static class SimpleModeRules
{
    /// <summary>
    /// Pages whose main job is a deep or risky change (services, overclocking, registry, Windows features, driver store
    /// clean-up) or a measurement only enthusiasts need. Keys are the navigation catalog's file names.
    /// </summary>
    public static IReadOnlyList<string> AdvancedPageKeys { get; } =
    [
        "services",
        "old-drivers",
        "windows-features",
        "registry",
        "overclock",
        "latency",
    ];

    private static readonly HashSet<string> AdvancedSet = new(AdvancedPageKeys, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the page with <paramref name="pageKey"/> gets the "Advanced" chip in the menu.</summary>
    public static bool IsAdvancedPage(string? pageKey) => pageKey is not null && AdvancedSet.Contains(pageKey);

    /// <summary>
    /// The hint shows while Advanced mode is off, until the user closes it once. Screenshot runs never show it, so the
    /// README images stay clean.
    /// </summary>
    public static bool ShowSimpleModeHint(bool advancedMode, bool dismissed, bool screenshotRun) =>
        !advancedMode && !dismissed && !screenshotRun;
}
