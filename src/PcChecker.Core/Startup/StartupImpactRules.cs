using PcChecker.Core.Performance;

namespace PcChecker.Core.Startup;

/// <summary>What one startup entry looks like once resolved: the program file, whether it exists and a rough impact.</summary>
public sealed record StartupInsight(string? ExecutablePath, bool? FileExists, long? FileSizeBytes, string Impact, string ImpactReason);

/// <summary>
/// Estimates how much a startup entry slows logon: a short list of known heavy launchers, then the executable size,
/// then "Low". Honest about what it cannot measure (Windows measures real impact only in Task Manager).
/// </summary>
public static class StartupImpactRules
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";
    public const string NotMeasured = "Not measured";

    /// <summary>Programs that are well known for long startup work or background services.</summary>
    private static readonly string[] HeavyKeywords =
    [
        "steam", "discord", "epicgameslauncher", "epic games", "armourycrate", "armoury crate", "icue", "lghub", "g hub",
        "adobe", "creative cloud", "onedrive", "teams", "skype", "spotify", "dropbox", "razer synapse", "battle.net",
        "ubisoft connect", "ea desktop", "origin", "gog galaxy", "msi center", "mystic light", "wallpaper engine",
    ];

    private static readonly string[] MediumKeywords =
    [
        "nvidia", "radeon", "intel", "realtek", "steelseries", "slack", "zoom", "updater", "update", "cloud", "sync", "backup",
    ];

    private const long MediumSizeBytes = 20L * 1024 * 1024;
    private const long HighSizeBytes = 120L * 1024 * 1024;

    /// <summary>Builds the insight for an item. <paramref name="sizeProbe"/> returns the file size or null when unknown/missing.</summary>
    public static StartupInsight Describe(StartupItem item, Func<string, long?>? sizeProbe = null, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        string? exe = StartupCommandParser.ExtractExecutable(item.Command, environment);
        long? size = null;
        bool? exists = null;
        if (exe is not null && sizeProbe is not null)
        {
            size = sizeProbe(exe);
            exists = size is not null;
        }

        var (impact, reason) = Estimate(item.Name, exe ?? item.Command, size);
        return new StartupInsight(exe, exists, size, impact, reason);
    }

    /// <summary>Impact label and the reason shown as a tooltip.</summary>
    public static (string Impact, string Reason) Estimate(string? name, string? commandOrPath, long? fileSizeBytes)
    {
        string haystack = ((name ?? string.Empty) + " " + (commandOrPath ?? string.Empty)).ToLowerInvariant();
        foreach (var k in HeavyKeywords)
        {
            if (haystack.Contains(k, StringComparison.Ordinal))
            {
                return (High, "Known to do a lot of work at logon (launcher, sync or background service).");
            }
        }

        if (fileSizeBytes is { } size)
        {
            if (size >= HighSizeBytes)
            {
                return (High, "Large program file (over 120 MB) takes longer to load.");
            }

            if (size >= MediumSizeBytes)
            {
                return (Medium, "Medium-sized program file (over 20 MB).");
            }
        }

        foreach (var k in MediumKeywords)
        {
            if (haystack.Contains(k, StringComparison.Ordinal))
            {
                return (Medium, "Helper or updater that runs in the background.");
            }
        }

        return fileSizeBytes is null
            ? (NotMeasured, "The program file could not be read, so its size is unknown.")
            : (Low, "Small program; little effect on startup time.");
    }

    /// <summary>Sort weight so High sorts first.</summary>
    public static int Rank(string impact) => impact switch
    {
        High => 0,
        Medium => 1,
        Low => 2,
        _ => 3,
    };
}
