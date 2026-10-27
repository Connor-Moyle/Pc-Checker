using System.Globalization;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Health;
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

    /// <summary>The full drive facts, when the drive inventory supplied them.</summary>
    public PhysicalDrive? Drive => Device.Drive;

    public VendorLink? VendorTool { get; }

    public int Number => Device.Number;

    public string Model => Device.Model;

    /// <summary>Capacity as printed on the box (decimal units).</summary>
    public string SizeText => Device.SizeBytes > 0 ? DriveText.Decimal(Device.SizeBytes) : Loc.T("Unknown size");

    /// <summary>"4.00 TB (3.64 TiB)": both units, correctly labelled.</summary>
    public string CapacityText => DriveText.Capacity(Device.SizeBytes);

    /// <summary>"NVMe SSD", "SATA HDD · 7,200 RPM", "USB HDD"…</summary>
    public string TypeText
    {
        get
        {
            string media = Device.MediaType is "Unknown" or "" ? Loc.T("drive") : Device.MediaType;
            string text = Device.BusType is "Unknown" or "" ? media : $"{Device.BusType} {media}";
            return Drive?.Rpm is { } rpm ? text + " · " + Loc.F("{0} RPM", rpm.ToString("N0", CultureInfo.CurrentCulture)) : text;
        }
    }

    public string MediaType => Device.MediaType;

    public string BusType => Device.BusType;

    public HealthStatus Health => Device.Health;

    /// <summary>"Good", "Caution", "Bad" or "Unknown" (the drive's own verdict), else the health Windows reports.</summary>
    public string HealthText => Drive is { } drive
        ? Loc.T(drive.Verdict.Label)
        : Device.Health switch
        {
            HealthStatus.Good => Loc.T("Healthy"),
            HealthStatus.Warning => Loc.T("Warning"),
            HealthStatus.Critical => Loc.T("Failing"),
            _ => Loc.T("Unknown"),
        };

    /// <summary>The one-line reason behind the verdict.</summary>
    public string HealthReason => Drive?.Verdict.Reason ?? Loc.T("Health as reported by Windows from the drive's own self-check (SMART).");

    public bool IsSystemDisk => Device.IsSystemDisk;

    public bool IsAsleep => Drive?.Power == DrivePowerState.Asleep;

    public string AsleepText => Drive is { } d ? DriveSleepRules.Describe(d.Power, d.Smart?.ReadAt, DateTimeOffset.Now) : string.Empty;

    public bool IsOffline => Drive?.IsOffline ?? false;

    /// <summary>"Pool member", "Storage space", "RAID volume" or empty.</summary>
    public string RoleText => Drive?.Role switch
    {
        DriveRole.PoolMember => Loc.T("Pool member"),
        DriveRole.StorageSpace => Loc.T("Storage space"),
        DriveRole.RaidArray => Loc.T("RAID volume"),
        _ => string.Empty,
    };

    public string RoleHelp => Drive?.Role switch
    {
        DriveRole.PoolMember => Loc.F("Part of the storage pool {0}. Windows hides it as a disk of its own.", Drive.PoolName ?? string.Empty),
        DriveRole.StorageSpace => Loc.F("A storage space ({0}) made from {1} drives.", Drive.Resiliency ?? Loc.T("pooled"), Drive.Members.Count),
        DriveRole.RaidArray => Loc.T("A RAID volume. Its member drives sit behind the RAID driver."),
        _ => string.Empty,
    };

    public bool HasRole => RoleText.Length > 0;

    /// <summary>Scans and benchmarks need a disk number; Storage Spaces pool members have none.</summary>
    public bool CanUseTools => Device.HasDiskNumber;

    public string VolumesText
    {
        get
        {
            if (Drive?.Role == DriveRole.PoolMember)
            {
                return Loc.T("In a storage pool");
            }

            return Device.Volumes.Count == 0 ? Loc.T("No drive letters") : string.Join("  ", Device.Volumes);
        }
    }

    public string DiskNumberText => Device.Number >= 0 ? Loc.F("Disk {0}", Device.Number) : Loc.T("Hidden disk");

    public string SerialText => string.IsNullOrWhiteSpace(Device.Serial) ? "—" : Device.Serial;

    public string FirmwareText => string.IsNullOrWhiteSpace(Device.Firmware) ? "—" : Device.Firmware;

    public bool HasVolumes => Device.Volumes.Count > 0;

    public bool HasVendorTool => VendorTool is not null;

    public string VendorToolText => VendorTool is null ? string.Empty : VendorTool.Note.Split(':')[0];

    public override string ToString() => Model;
}

/// <summary>One fact about the selected drive: label, value and a one-line explanation.</summary>
public sealed record DriveFactRow(string Label, string Value, string? Help = null)
{
    public bool HasHelp => !string.IsNullOrEmpty(Help);
}

/// <summary>One SMART attribute row with the raw value decoded for people.</summary>
public sealed class SmartRowItem
{
    public SmartRowItem(SmartAttribute attribute, SmartVendor vendor, bool isNvme)
    {
        Attribute = attribute;
        IdText = isNvme ? string.Empty : attribute.Id.ToString(CultureInfo.InvariantCulture);
        Name = attribute.Name;
        CurrentText = attribute.Current?.ToString(CultureInfo.CurrentCulture) ?? "—";
        WorstText = attribute.Worst?.ToString(CultureInfo.CurrentCulture) ?? "—";
        ThresholdText = attribute.Threshold?.ToString(CultureInfo.CurrentCulture) ?? "—";
        RawText = isNvme
            ? attribute.RawValue?.ToString("N0", CultureInfo.CurrentCulture) ?? "—"
            : SmartDecoder.DisplayRaw(attribute.Id, attribute.RawValue, vendor);

        bool belowThreshold = attribute.Threshold is > 0 && attribute.Current is { } c && c <= attribute.Threshold;
        long? decoded = attribute.RawValue is { } raw && !isNvme ? SmartDecoder.Decode(attribute.Id, raw, vendor) : null;
        bool watch = !isNvme && decoded is > 0 && attribute.Id is SmartDecoder.ReallocatedId or SmartDecoder.PendingId
            or SmartDecoder.OfflineUncorrectableId or SmartDecoder.SpinRetryId or SmartDecoder.ReportedUncorrectableId or SmartDecoder.CrcErrorsId;
        if (isNvme && attribute.Id is 1 or 10 && attribute.RawValue is > 0)
        {
            watch = true;
        }

        IsProblem = attribute.Failed || belowThreshold;
        IsWatch = !IsProblem && watch;
        StatusText = attribute.Failed ? Loc.T("Failed") : belowThreshold ? Loc.T("Below threshold") : watch ? Loc.T("Watch") : Loc.T("OK");
    }

    public SmartAttribute Attribute { get; }

    public string IdText { get; }

    public string Name { get; }

    public string CurrentText { get; }

    public string WorstText { get; }

    public string ThresholdText { get; }

    public string RawText { get; }

    public string StatusText { get; }

    public bool IsProblem { get; }

    public bool IsWatch { get; }
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
