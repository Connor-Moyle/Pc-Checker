namespace PcChecker.Core.RamTuning;

/// <summary>The stages of the plan, from the safest to the most advanced.</summary>
public enum RamStage
{
    UpdateBios = 1,
    EnableProfile = 2,
    PlatformTuning = 3,
    ManualTimings = 4,
}

/// <summary>Where a step stands on this PC.</summary>
public enum RamStepState
{
    /// <summary>Recommended and not done yet.</summary>
    ToDo,

    /// <summary>Already done, as far as Windows can tell.</summary>
    Done,

    /// <summary>Worth checking; Windows cannot tell whether it is done.</summary>
    Check,

    /// <summary>Optional: only for more speed, with more risk.</summary>
    Optional,

    /// <summary>Does not apply here (with the reason in the step).</summary>
    NotApplicable,
}

/// <summary>How the headline should look.</summary>
public enum RamPlanTone
{
    Good,
    Info,
    Warning,
}

/// <summary>One step of the plan: what to change, where, the safe range and the risk.</summary>
public sealed record RamPlanStep(
    RamStage Stage,
    string Id,
    string Title,
    string WhatItDoes,
    string SafeRange,
    string Risk,
    RamStepState State)
{
    /// <summary>Where the setting is in this board's BIOS ("Ai Tweaker > Ai Overclock Tuner"), or null.</summary>
    public string? MenuPath { get; init; }

    /// <summary>Only shown in Advanced mode.</summary>
    public bool Advanced { get; init; }

    /// <summary>A page to open (the BIOS download page), or null.</summary>
    public string? LinkUrl { get; init; }

    public string? LinkText { get; init; }
}

/// <summary>One row of the voltage limits table.</summary>
public sealed record VoltageLimit(string Name, string Feeds, string Typical, double DailyMaxVolts, string Note);

/// <summary>The speed the plan aims for.</summary>
public sealed record RamTarget(
    int SafeMts,
    int? StretchMts,
    string RatioText,
    int? FclkMhz,
    int? FclkStretchMhz,
    int FourModuleMts,
    int? OfficialMts);

/// <summary>A memory stability test tool, linked only (never downloaded by PC Checker).</summary>
public sealed record StabilityTool(string Name, string WhatItDoes, string Duration, string Url, bool Advanced);

/// <summary>The personalised plan.</summary>
public sealed record RamTuningPlan
{
    public required string Headline { get; init; }

    public required string HeadlineDetail { get; init; }

    public RamPlanTone Tone { get; init; } = RamPlanTone.Info;

    public IReadOnlyList<RamPlanStep> Steps { get; init; } = [];

    public IReadOnlyList<VoltageLimit> VoltageLimits { get; init; } = [];

    public RamTarget? Target { get; init; }

    /// <summary>The speed Windows should report once stage 2 (and 3) are done; null when unknown.</summary>
    public int? ExpectedMts { get; init; }

    /// <summary>The profile's name on this platform ("EXPO", "XMP", "DOCP / XMP").</summary>
    public string ProfileName { get; init; } = "XMP";

    public IReadOnlyList<string> IfItDoesNotBoot { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The steps of one stage, without the Advanced ones unless <paramref name="advanced"/>.</summary>
    public IEnumerable<RamPlanStep> StepsFor(RamStage stage, bool advanced) =>
        Steps.Where(s => s.Stage == stage && (advanced || !s.Advanced));
}
