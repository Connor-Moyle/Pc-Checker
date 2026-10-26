namespace PcChecker.Core.Performance;

/// <summary>
/// Everything the slowdown rules need, gathered by the analyzer. Pure data so the rules can be unit-tested.
/// Null means "unknown"; rules skip what they cannot judge.
/// </summary>
public sealed record PerformanceFacts
{
    public IReadOnlyList<StartupItem> StartupItems { get; init; } = [];
    public IReadOnlyList<ServiceInfo> Services { get; init; } = [];
    public IReadOnlyList<ProcessInfo> TopProcesses { get; init; } = [];
    public double? LastBootSeconds { get; init; }
    public double MemoryUsedPercent { get; init; }
    public long MemoryTotalBytes { get; init; }
    public string? ActivePowerPlan { get; init; }
    public bool? FastStartupEnabled { get; init; }
    public bool? HibernationEnabled { get; init; }
    public bool? VisualEffectsBestAppearance { get; init; }
    public bool? GameModeEnabled { get; init; }
    public bool? GameBarEnabled { get; init; }
    public bool? BackgroundAppsEnabled { get; init; }
    public bool? StorageSenseEnabled { get; init; }
    public bool? TrimEnabled { get; init; }
    public bool? SystemDriveIsSsd { get; init; }
    public bool? PendingReboot { get; init; }
    public int? DaysSinceLastUpdateCheck { get; init; }
    public double? MaxCpuTempC { get; init; }
    public double? MaxGpuTempC { get; init; }
    public bool? OnBattery { get; init; }

    /// <summary>True when a battery is present (laptop), false for desktops, null when unknown.</summary>
    public bool? HasBattery { get; init; }

    /// <summary>Days since Windows last started, or null when unknown.</summary>
    public double? UptimeDays { get; init; }

    /// <summary>Number of third-party antivirus products registered with Windows Security Center.</summary>
    public int? ThirdPartyAntivirusCount { get; init; }

    /// <summary>Commit charge (RAM + page file in use) as a percentage of the commit limit.</summary>
    public double? CommitUsedPercent { get; init; }

    public int LogicalProcessors { get; init; }
    public IReadOnlyList<(string Drive, long Free, long Total, string MediaType)> Drives { get; init; } = [];
}
