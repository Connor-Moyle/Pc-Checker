using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Converters;
using PcChecker.Core.Common;
using PcChecker.Core.Latency;
using PcChecker.Core.Optimization;

namespace PcChecker.App.ViewModels;

/// <summary>One finding on the Latency page with its fix button state.</summary>
public sealed partial class LatencyFindingItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFix))]
    private bool _isFixing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFix))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isFixed;

    [ObservableProperty]
    private string? _resultMessage;

    public LatencyFindingItem(LatencyFinding finding, OptimizationAction? action)
    {
        Finding = finding;
        Action = action;
    }

    public LatencyFinding Finding { get; }

    /// <summary>The optimization action that fixes this finding, or null when it is advice only.</summary>
    public OptimizationAction? Action { get; }

    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public Severity Severity => Finding.Severity;
    public string SeverityText => Finding.Severity.ToString();
    public Brush SeverityBrush => Finding.Severity switch
    {
        Severity.Critical => SeverityToBrushConverter.Critical,
        Severity.High => SeverityToBrushConverter.High,
        Severity.Medium => SeverityToBrushConverter.Medium,
        Severity.Low => SeverityToBrushConverter.Low,
        _ => SeverityToBrushConverter.Info,
    };

    public bool AdvancedOnly => Finding.AdvancedOnly;
    public string RiskText => Finding.Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Moderate",
        _ => "Advanced",
    };

    public Brush RiskBrush => Finding.Risk switch
    {
        RiskLevel.Safe => HealthStatusToBrushConverter.Good,
        RiskLevel.Moderate => HealthStatusToBrushConverter.Warning,
        _ => HealthStatusToBrushConverter.Critical,
    };

    public bool HasAction => Action is not null;
    public bool IsInfoOnly => Action is not null && !Action.Reversible && !Action.RequiresRestart && Action.Id == LatencyActionIds.TimerResolutionHint;
    public string FixText => IsInfoOnly ? "Explain" : "Fix";
    public bool RequiresRestart => Action?.RequiresRestart == true;
    public bool CanFix => HasAction && !IsFixing && !IsFixed;
    public string StatusText => IsFixed ? (RequiresRestart ? "Fixed — restart to apply" : "Fixed") : string.Empty;
}

/// <summary>One ping target row with a colored badge.</summary>
public sealed class NetworkResultItem
{
    public NetworkResultItem(NetworkLatencyResult result)
    {
        Result = result;
        Status = NetworkLatencyMath.Grade(result);
    }

    public NetworkLatencyResult Result { get; }
    public HealthStatus Status { get; }
    public string Label => Result.Label;
    public string Target => Result.Target == NetworkLatencyProbe.GatewayToken ? "default gateway" : Result.Target;
    public string AvgText => Format(Result.AvgMs);
    public string MinText => Format(Result.MinMs);
    public string MaxText => Format(Result.MaxMs);
    public string JitterText => Format(Result.JitterMs);
    public string LossText => $"{Result.LossPercent:0.#} %";
    public string Verdict => NetworkLatencyMath.Describe(Result);
    public Brush StatusBrush => Status switch
    {
        HealthStatus.Good => HealthStatusToBrushConverter.Good,
        HealthStatus.Warning => HealthStatusToBrushConverter.Warning,
        HealthStatus.Critical => HealthStatusToBrushConverter.Critical,
        _ => HealthStatusToBrushConverter.Unknown,
    };

    private static string Format(double? ms) => ms is null ? "—" : ms < 10 ? $"{ms:0.0} ms" : $"{ms:0} ms";
}

/// <summary>A row in the "recent runs" table.</summary>
public sealed record LatencyRunSummary(DateTimeOffset Time, int Score, string Grade, string DpcText, string PingText, int Findings)
{
    public string TimeText => Time.ToString("g");

    public static LatencyRunSummary From(LatencyReport report)
    {
        var ping = report.Network.FirstOrDefault(n => n.Target != NetworkLatencyProbe.GatewayToken && n.AvgMs is not null) ?? report.Network.FirstOrDefault(n => n.AvgMs is not null);
        return new LatencyRunSummary(
            report.Captured,
            report.Score,
            LatencyRules.Grade(report.Score),
            report.DpcTimePercent is { } d ? $"{d:0.0} %" : "—",
            ping?.AvgMs is { } p ? $"{p:0.#} ms" : "—",
            report.Findings.Count);
    }
}

/// <summary>Key/value row for the "settings at a glance" list.</summary>
public sealed record SettingRow(string Key, string Value);
