using System.Collections.ObjectModel;
using System.Globalization;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.RamTuning;

namespace PcChecker.App.ViewModels;

/// <summary>One module in the "Your memory" table.</summary>
public sealed record RamModuleRow(string Slot, string Size, string Ranks, string Rated, string Running, string Maker, string PartNumber, string Chip, string ChipReason)
{
    public static RamModuleRow From(RamModule m) => new(
        m.Slot,
        m.CapacityBytes is { } c ? ByteSize.Format(c) : "—",
        m.Ranks switch { 1 => Loc.T("Single rank"), 2 => Loc.T("Dual rank"), { } r => r.ToString(CultureInfo.CurrentCulture), _ => "—" },
        m.RatedMts is { } r2 ? $"{r2} MT/s" : "—",
        m.ConfiguredMts is { } c2 ? $"{c2} MT/s" : "—",
        m.Manufacturer ?? "—",
        m.PartNumber ?? "—",
        m.Ic.Confidence == IcConfidence.Likely ? Loc.F("{0} (likely)", m.Ic.Name) : m.Ic.Confidence == IcConfidence.Unknown ? Loc.T("Unknown") : m.Ic.Name,
        m.Ic.Reason);
}

/// <summary>One step of the plan.</summary>
public sealed record RamStepRow(string Number, RamPlanStep Step)
{
    public string Title => Step.Title;
    public string What => Step.WhatItDoes;
    public string SafeRange => Step.SafeRange;
    public string Risk => Step.Risk;
    public string? MenuPath => Step.MenuPath;
    public bool HasMenuPath => !string.IsNullOrWhiteSpace(Step.MenuPath);
    public string? LinkUrl => Step.LinkUrl;
    public string? LinkText => Step.LinkText;
    public bool HasLink => !string.IsNullOrWhiteSpace(Step.LinkUrl);
    public bool IsAdvanced => Step.Advanced;

    public string StateText => Step.State switch
    {
        RamStepState.Done => Loc.T("Done"),
        RamStepState.ToDo => Loc.T("To do"),
        RamStepState.Check => Loc.T("Check"),
        RamStepState.Optional => Loc.T("Optional"),
        _ => Loc.T("Not for this PC"),
    };

    public Severity StateSeverity => Step.State switch
    {
        RamStepState.Done => Severity.Low,
        RamStepState.ToDo => Severity.High,
        RamStepState.Check => Severity.Medium,
        RamStepState.Optional => Severity.Info,
        _ => Severity.Low,
    };

    public bool IsDone => Step.State == RamStepState.Done;
}

/// <summary>A stage of the plan with its visible steps.</summary>
public sealed class RamStageGroup
{
    public RamStageGroup(RamStage stage, string title, string subtitle, IEnumerable<RamStepRow> steps, bool expanded)
    {
        Stage = stage;
        Title = title;
        Subtitle = subtitle;
        IsExpanded = expanded;
        foreach (var s in steps)
        {
            Steps.Add(s);
        }
    }

    public RamStage Stage { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool IsExpanded { get; }
    public ObservableCollection<RamStepRow> Steps { get; } = [];

    public string CountText => Loc.F("{0} of {1} done", Steps.Count(s => s.IsDone), Steps.Count);
}

/// <summary>One row of the voltage table.</summary>
public sealed record VoltageRow(string Name, string Feeds, string Typical, string DailyMax, string Note)
{
    public static VoltageRow From(VoltageLimit v) =>
        new(v.Name, v.Feeds, v.Typical, v.DailyMaxVolts.ToString("0.00", CultureInfo.CurrentCulture) + " V", v.Note);
}

/// <summary>One line of the last check.</summary>
public sealed record CheckItemRow(string Title, string Detail, Severity Severity)
{
    public static CheckItemRow From(RamCheckItem item) => new(item.Title, item.Detail, item.Outcome switch
    {
        CheckOutcome.Ok => Severity.Low,
        CheckOutcome.Warning => Severity.Medium,
        _ => Severity.High,
    });

    public string Badge => Severity switch
    {
        Severity.Low => Loc.T("OK"),
        Severity.Medium => Loc.T("Look"),
        _ => Loc.T("Problem"),
    };
}

/// <summary>A saved check in the history list.</summary>
public sealed record CheckHistoryRow(string Time, string Speed, string Profile, string Result, string Crashes)
{
    public static CheckHistoryRow From(RamCheckRecord r) => new(
        r.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        r.ConfiguredMts is { } m ? $"{m} MT/s" : "—",
        r.XmpState switch { "Enabled" => Loc.T("On"), "Disabled" => Loc.T("Off"), _ => Loc.T("Unknown") },
        r.Overall switch { CheckOutcome.Ok => Loc.T("Worked"), CheckOutcome.Warning => Loc.T("Mostly fine"), _ => Loc.T("Not there yet") },
        r.MemoryBlueScreens + r.WheaErrors == 0 ? Loc.T("None") : Loc.F("{0} blue screen(s), {1} WHEA", r.MemoryBlueScreens, r.WheaErrors));
}

/// <summary>A stability test tool (linked only).</summary>
public sealed record ToolRow(string Name, string What, string Duration, string Url, bool IsAdvanced);

/// <summary>A DIMM temperature.</summary>
public sealed record DimmTempRow(string Name, string Value, bool IsWarm);
