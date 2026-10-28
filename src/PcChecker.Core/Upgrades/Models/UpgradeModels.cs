using PcChecker.Core.Common;
using PcChecker.Core.Displays;
using PcChecker.Core.Games;
using PcChecker.Core.Hardware;
using PcChecker.Core.History;
using PcChecker.Core.Security;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Upgrades;

/// <summary>Which part of the PC an item is about.</summary>
public enum UpgradeArea
{
    Memory,
    Storage,
    Graphics,
    Processor,
    PowerSupply,
    Windows,
}

/// <summary>How much difference an upgrade makes, in words (never a made-up FPS number).</summary>
public enum UpgradeImpact
{
    Small,
    Noticeable,
    Big,
}

/// <summary>What held the games back in the recorded history.</summary>
public enum BoundVerdict
{
    /// <summary>Not enough gaming in the history to say.</summary>
    NoData,

    /// <summary>The graphics card was the limit most of the time (the usual, healthy case).</summary>
    GpuLimited,

    /// <summary>The processor was the limit: the graphics card waited for it.</summary>
    CpuLimited,

    /// <summary>Neither part clearly held the other back (or a frame limit was in place).</summary>
    Balanced,
}

/// <summary>How sure a verdict is.</summary>
public enum AdviceConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>The PCIe link a graphics card trained at (generation 1–5, lanes 1–16). Null values were not reported.</summary>
public sealed record PcieLinkInfo(int? CurrentGen, int? MaxGen, int? CurrentWidth, int? MaxWidth);

/// <summary>A setup problem that costs performance and nothing to fix.</summary>
public sealed record FixFirstItem(
    string Id,
    UpgradeArea Area,
    Severity Severity,
    string Title,
    string Detail,
    string? PageKey = null,
    string? ActionText = null);

/// <summary>One ranked upgrade: what to buy, why, and how much difference it makes in words.</summary>
public sealed record UpgradeSuggestion(
    string Id,
    UpgradeArea Area,
    int Score,
    UpgradeImpact Impact,
    string Title,
    string Why,
    string ImpactText,
    IReadOnlyList<string> Options,
    string? Note = null,
    string? PageKey = null,
    string? ActionText = null);

/// <summary>One line of "Your PC at a glance". <paramref name="Key"/> is "cpu", "gpu", "memory", "storage", "board", "psu" or "screen".</summary>
public sealed record GlanceItem(string Key, UpgradeArea Area, string Label, string Value, string? Detail);

/// <summary>A run of recorded gaming (GPU busy) and what limited it.</summary>
public sealed record GamingSession(
    DateTimeOffset Start,
    DateTimeOffset End,
    double Minutes,
    BoundVerdict Verdict,
    double GpuAverage,
    double CpuAverage,
    double? BusiestCoreAverage);

/// <summary>The CPU/GPU balance worked out from the History samples recorded while games ran.</summary>
public sealed record BoundAnalysis(
    BoundVerdict Verdict,
    AdviceConfidence Confidence,
    double GamingMinutes,
    double GpuLimitedShare,
    double CpuLimitedShare,
    bool HasPerCoreData,
    IReadOnlyList<GamingSession> Sessions)
{
    public static BoundAnalysis None { get; } = new(BoundVerdict.NoData, AdviceConfidence.Low, 0, 0, 0, false, []);
}

/// <summary>A rule-of-thumb power supply size. The PSU itself cannot be read by software.</summary>
public sealed record PsuEstimate(int CpuWatts, int GpuWatts, int RestWatts, bool CpuFromTable, bool GpuFromTable)
{
    /// <summary>The highest likely draw of the whole PC with CPU and GPU both fully loaded.</summary>
    public int PeakWatts => CpuWatts + GpuWatts + RestWatts;

    /// <summary>A standard PSU size with about 25 % headroom over <see cref="PeakWatts"/>.</summary>
    public int RecommendedWatts => PsuEstimator.RoundUpToStandard(PeakWatts * PsuEstimator.Headroom);
}

/// <summary>Windows 11 readiness of a PC.</summary>
public enum Win11Status
{
    Unknown,
    AlreadyWindows11,
    Ready,
    ReadyAfterBiosChange,
    NotReady,
}

/// <summary>One requirement of Windows 11. <paramref name="Passed"/> is null when it could not be checked.</summary>
public sealed record ReadinessCheck(string Id, bool? Passed, string Title, string Detail, bool FixableInBios = false);

/// <summary>The Windows 11 verdict and every requirement behind it.</summary>
public sealed record Windows11Readiness(Win11Status Status, string Summary, IReadOnlyList<ReadinessCheck> Checks);

/// <summary>
/// Everything the advisor reads, gathered from the other modules (hardware inventory, drives, games, History,
/// Security, Displays). Lists are empty, never null, when a source could not be read.
/// </summary>
public sealed record UpgradeFacts
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
    public HardwareInventory? Hardware { get; init; }
    public IReadOnlyList<PhysicalDrive> Drives { get; init; } = [];
    public IReadOnlyList<InstalledGame> Games { get; init; } = [];
    public IReadOnlyList<HistorySample> History { get; init; } = [];
    public bool HistoryRecording { get; init; }
    public SecurityFacts? Security { get; init; }
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public PcieLinkInfo? GpuLink { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>The whole answer to "what should I upgrade next, and what is wrong with my current setup?".</summary>
public sealed record UpgradeReport(
    DateTimeOffset Captured,
    bool IsLaptop,
    IReadOnlyList<GlanceItem> Glance,
    IReadOnlyList<FixFirstItem> FixFirst,
    IReadOnlyList<UpgradeSuggestion> Upgrades,
    BoundAnalysis Balance,
    PsuEstimate? Psu,
    Windows11Readiness Windows11,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings)
{
    public static UpgradeReport Empty { get; } = new(DateTimeOffset.MinValue, false, [], [], [], BoundAnalysis.None, null,
        new Windows11Readiness(Win11Status.Unknown, string.Empty, []), [], []);
}
