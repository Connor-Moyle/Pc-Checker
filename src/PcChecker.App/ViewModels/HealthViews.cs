using System.IO;
using PcChecker.Core.Common;
using PcChecker.Core.Health;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>One finding with display helpers.</summary>
public sealed class HealthItemView
{
    public HealthItemView(HealthItem item)
    {
        Item = item;
        MetricsText = item.Metrics.Count == 0 ? null : string.Join("   ·   ", item.Metrics.Select(m => $"{m.Key}: {m.Value}"));
        StatusText = item.Status switch
        {
            HealthStatus.Critical => "Critical",
            HealthStatus.Warning => "Warning",
            HealthStatus.Good => "Good",
            _ => "Unknown",
        };
    }

    public HealthItem Item { get; }
    public HealthStatus Status => Item.Status;
    public string StatusText { get; }
    public string Component => Item.Component;
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public string? Recommendation => Item.Recommendation;
    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Item.Recommendation);
    public string? MetricsText { get; }
    public bool HasMetrics => MetricsText is not null;
}

/// <summary>A drive with formatted facts and the SMART verdict's reasons.</summary>
public sealed class DiskHealthView
{
    public DiskHealthView(DiskHealth disk, MonitoringSettings monitoring)
    {
        Disk = disk;
        var verdict = SmartRules.Evaluate(disk, monitoring.DiskWarnC, monitoring.DiskCritC);
        VerdictSummary = disk.Status == HealthStatus.Unknown ? "The drive did not report SMART data (common behind USB adapters and RAID controllers)." : verdict.Summary;
        Reasons = verdict.Reasons;
        Attributes = disk.Attributes.Select(a => new SmartAttributeView(a)).ToList();
        SizeText = ByteSize.Format(disk.SizeBytes);
        TemperatureText = disk.TemperatureC is { } t ? $"{t:0} °C" : "—";
        HoursText = disk.PowerOnHours is { } h ? h.ToString("N0") : "—";
        WearText = disk.WearPercent is { } w ? $"{w:0}% used" : disk.MediaType == "HDD" ? "n/a" : "—";
        WrittenText = disk.HostWritesGb is { } g ? (g >= 1024 ? $"{g / 1024.0:0.0} TB" : $"{g:N0} GB") : "—";
        SmartStatusText = disk.SmartStatusText ?? "Not reported";
        StatusText = disk.Status switch
        {
            HealthStatus.Critical => "Failing",
            HealthStatus.Warning => "Caution",
            HealthStatus.Good => "Healthy",
            _ => "Unknown",
        };
        Subtitle = string.Join(" · ", new[] { disk.MediaType, SizeText, disk.Serial is null ? null : "S/N " + disk.Serial, disk.Firmware is null ? null : "FW " + disk.Firmware }.Where(s => !string.IsNullOrEmpty(s)));
    }

    public DiskHealth Disk { get; }
    public string Model => Disk.Model;
    public HealthStatus Status => Disk.Status;
    public string StatusText { get; }
    public string Subtitle { get; }
    public string SizeText { get; }
    public string TemperatureText { get; }
    public double? TemperatureC => Disk.TemperatureC;
    public string HoursText { get; }
    public string WearText { get; }
    public string WrittenText { get; }
    public string SmartStatusText { get; }
    public string VerdictSummary { get; }
    public IReadOnlyList<string> Reasons { get; }
    public bool HasReasons => Reasons.Count > 0;
    public IReadOnlyList<SmartAttributeView> Attributes { get; }
    public bool HasAttributes => Attributes.Count > 0;
}

public sealed class SmartAttributeView
{
    public SmartAttributeView(SmartAttribute attribute)
    {
        Attribute = attribute;
        IdText = attribute.Id.ToString("X2");
        CurrentText = attribute.Current?.ToString() ?? "—";
        WorstText = attribute.Worst?.ToString() ?? "—";
        ThresholdText = attribute.Threshold?.ToString() ?? "—";
        RawText = attribute.RawValue?.ToString("N0") ?? "—";
        StateText = attribute.Failed ? "Failed" : "OK";
    }

    public SmartAttribute Attribute { get; }
    public string IdText { get; }
    public string Name => Attribute.Name;
    public string CurrentText { get; }
    public string WorstText { get; }
    public string ThresholdText { get; }
    public string RawText { get; }
    public string StateText { get; }
    public bool Failed => Attribute.Failed;
}

public sealed class BatteryHealthView
{
    public BatteryHealthView(BatteryHealth battery)
    {
        Battery = battery;
        Status = HealthScoring.BatteryStatus(battery.WearPercent);
        StatusText = Status switch
        {
            HealthStatus.Critical => "Critical",
            HealthStatus.Warning => "Warning",
            HealthStatus.Good => "Good",
            _ => "Unknown",
        };
        WearText = battery.WearPercent is { } w ? $"{w:0}%" : "—";
        HealthPercent = battery.WearPercent is { } wear ? Math.Clamp(100 - wear, 0, 100) : 0;
        CyclesText = battery.CycleCount?.ToString("N0") ?? "—";
        DesignText = battery.DesignCapacityMwh is { } d ? $"{d / 1000.0:0.0} Wh" : "—";
        FullText = battery.FullChargeCapacityMwh is { } f ? $"{f / 1000.0:0.0} Wh" : "—";
        ChargeText = battery.ChargePercent is { } c ? $"{c}%" : "—";
        Subtitle = string.Join(" · ", new[] { battery.Chemistry, battery.Status }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public BatteryHealth Battery { get; }
    public string Name => Battery.Name;
    public HealthStatus Status { get; }
    public string StatusText { get; }
    public string Subtitle { get; }
    public string WearText { get; }
    public double HealthPercent { get; }
    public string CyclesText { get; }
    public string DesignText { get; }
    public string FullText { get; }
    public string ChargeText { get; }
}

public sealed class SystemErrorView
{
    public SystemErrorView(SystemErrorEntry entry)
    {
        Entry = entry;
        TimeText = entry.Time.ToString("yyyy-MM-dd HH:mm");
        LevelText = entry.Level == Severity.Critical ? "Critical" : "Error";
    }

    public SystemErrorEntry Entry { get; }
    public DateTimeOffset Time => Entry.Time;
    public string TimeText { get; }
    public string Log => Entry.Log;
    public string Source => Entry.Source;
    public Severity Level => Entry.Level;
    public string LevelText { get; }
    public long EventId => Entry.EventId;
    public string Message => Entry.Message;
}

public sealed record SourceCountView(string Source, int Count)
{
    public string Label => $"{Source} ({Count})";
}

public sealed class ReliabilityEventView
{
    public ReliabilityEventView(ReliabilityEvent e)
    {
        Event = e;
        TimeText = e.Time.ToString("yyyy-MM-dd HH:mm");
        IsFailure = ReliabilityClassifier.IsFailure(e.Type);
        Severity = IsFailure ? Severity.High : Severity.Info;
    }

    public ReliabilityEvent Event { get; }
    public string TimeText { get; }
    public string Type => Event.Type;
    public string Source => Event.Source;
    public string Message => Event.Message;
    public bool IsFailure { get; }
    public Severity Severity { get; }
}

public sealed class CrashDumpView
{
    public CrashDumpView(CrashDumpInfo dump)
    {
        Dump = dump;
        FileName = Path.GetFileName(dump.Path);
        TimeText = dump.Time.ToString("yyyy-MM-dd HH:mm");
        SizeText = ByteSize.Format(dump.SizeBytes);
        IsRecent = (DateTimeOffset.Now - dump.Time).TotalDays <= HealthItemRules.CrashWindowDays;
    }

    public CrashDumpInfo Dump { get; }
    public string FullPath => Dump.Path;
    public string FileName { get; }
    public string TimeText { get; }
    public string SizeText { get; }
    public bool IsRecent { get; }
}
