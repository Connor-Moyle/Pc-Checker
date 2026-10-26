using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Storage;

namespace PcChecker.App.ViewModels;

/// <summary>A physical drive as shown on the Storage page: the Core record plus ready-made display text.</summary>
public sealed class StorageDeviceItem
{
    public StorageDeviceItem(StorageDevice device, VendorLink? vendorTool)
    {
        Device = device;
        VendorTool = vendorTool;
    }

    public StorageDevice Device { get; }

    public VendorLink? VendorTool { get; }

    public int Number => Device.Number;

    public string Model => Device.Model;

    public string SizeText => ByteSize.Format(Device.SizeBytes);

    /// <summary>"NVMe SSD", "SATA HDD", "USB SSD"…</summary>
    public string TypeText => Device.BusType is "Unknown" or "" ? Device.MediaType : $"{Device.BusType} {Device.MediaType}";

    public string MediaType => Device.MediaType;

    public string BusType => Device.BusType;

    public HealthStatus Health => Device.Health;

    public string HealthText => Device.Health switch
    {
        HealthStatus.Good => "Healthy",
        HealthStatus.Warning => "Warning",
        HealthStatus.Critical => "Failing",
        _ => "Unknown",
    };

    public bool IsSystemDisk => Device.IsSystemDisk;

    public string VolumesText => Device.Volumes.Count == 0 ? "No drive letters" : string.Join("  ", Device.Volumes);

    public string DiskNumberText => $"Disk {Device.Number}";

    public string SerialText => string.IsNullOrWhiteSpace(Device.Serial) ? "—" : Device.Serial;

    public string FirmwareText => string.IsNullOrWhiteSpace(Device.Firmware) ? "—" : Device.Firmware;

    public bool HasVolumes => Device.Volumes.Count > 0;

    public bool HasVendorTool => VendorTool is not null;

    public string VendorToolText => VendorTool is null ? string.Empty : VendorTool.Note.Split(':')[0];

    public override string ToString() => Model;
}

/// <summary>A drive letter on the selected device, for the benchmark and repair tools.</summary>
public sealed record VolumeOption(string Letter, string Label, string MediaType, string BusType, bool IsSystemVolume)
{
    public override string ToString() => Label;
}

/// <summary>One System-log disk event with its plain-English explanation.</summary>
public sealed class DiskEventItem
{
    public DiskEventItem(DiskErrorEvent evt)
    {
        Event = evt;
        Explanation = DiskEventCatalog.Explain(evt.EventId);
    }

    public DiskErrorEvent Event { get; }

    public DiskEventExplanation Explanation { get; }

    public string TimeText => Event.Time == DateTimeOffset.MinValue ? "—" : Event.Time.LocalDateTime.ToString("g");

    public string Source => Event.Source;

    public long EventId => Event.EventId;

    public string IdText => $"Event {Event.EventId}";

    public string Title => Explanation.Title;

    public string Meaning => Explanation.Meaning;

    public string Advice => Explanation.Advice;

    public Severity Severity => Explanation.Severity;

    public string Message => Event.Message;

    public string DeviceText => string.IsNullOrWhiteSpace(Event.Device) ? string.Empty : Event.Device;

    public bool HasDevice => !string.IsNullOrWhiteSpace(Event.Device);
}

/// <summary>Row of the benchmark comparison table with formatted numbers.</summary>
public sealed class BenchmarkRow
{
    public BenchmarkRow(BenchmarkComparison comparison)
    {
        Comparison = comparison;
    }

    public BenchmarkComparison Comparison { get; }

    public string Metric => Comparison.Metric;

    public string MeasuredText => Format(Comparison.Measured);

    public string Verdict => Comparison.Verdict;

    public bool IsGood => Comparison.Verdict == "As expected";

    public bool IsBad => Comparison.Verdict.StartsWith("Much", StringComparison.Ordinal);

    public string HddText => Format(Comparison.TypicalHdd);

    public string SataText => Format(Comparison.TypicalSataSsd);

    public string Gen3Text => Format(Comparison.TypicalNvmeGen3);

    public string Gen4Text => Format(Comparison.TypicalNvmeGen4);

    private static string Format(double value) => value >= 100 ? value.ToString("N0") : value.ToString("0.0");
}
