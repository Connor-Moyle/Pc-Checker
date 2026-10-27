using System.Globalization;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Health;

/// <summary>
/// Drive health collectors: Win32_DiskDrive inventory, MSFT_PhysicalDisk status, SMART prediction and attribute blobs,
/// LibreHardwareMonitor drive sensors and PowerShell storage reliability counters, merged per drive by index, serial,
/// PNP instance or model and judged by <see cref="SmartRules"/>.
/// </summary>
public sealed partial class WindowsHealthService
{
    // ---------------------------------------------------------------- disks

    private void CollectDisks(AnalysisContext ctx, CancellationToken ct)
    {
        var local = new List<string>();
        foreach (var row in Query(Cimv2, "SELECT Index, Model, SerialNumber, FirmwareRevision, Size, PNPDeviceID, InterfaceType, MediaType FROM Win32_DiskDrive", ct))
        {
            long size = Long(row, "Size") ?? 0;
            if (size <= 0)
            {
                continue; // card readers and empty USB bays
            }

            ctx.DiskBuilders.Add(new DiskBuilder
            {
                Index = (int)(Long(row, "Index") ?? -1),
                Model = Str(row, "Model") ?? "Unknown drive",
                Serial = Str(row, "SerialNumber"),
                Firmware = Str(row, "FirmwareRevision"),
                SizeBytes = size,
                PnpId = Str(row, "PNPDeviceID"),
                MediaType = HealthCodes.GuessMediaType(Str(row, "Model"), Str(row, "InterfaceType"), null),
            });
        }

        if (ctx.DiskBuilders.Count == 0)
        {
            ctx.Warnings.Add("Drive health: Windows did not list any fixed drives.");
            return;
        }

        TryStep(local, "physical disk status", () =>
        {
            foreach (var row in Query(StorageRoot, "SELECT DeviceId, FriendlyName, SerialNumber, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk", ct))
            {
                var disk = FindByIndex(ctx, Str(row, "DeviceId")) ?? FindBySerial(ctx, Str(row, "SerialNumber"));
                if (disk is null)
                {
                    continue;
                }

                var media = HealthCodes.MediaType((int?)Long(row, "MediaType"));
                if (media != "Unspecified")
                {
                    disk.MediaType = media;
                }

                disk.BusType = HealthCodes.BusType((int?)Long(row, "BusType"));
                // Null for Unknown (5) or a missing value: drives behind USB bridges and some NVMe controllers report
                // no health status, which must not be mistaken for a failing drive.
                disk.PhysicalDiskHealth = HealthCodes.PhysicalDiskHealth((int?)Long(row, "HealthStatus"));
            }
        });

        TryStep(local, "SMART failure prediction", () =>
        {
            foreach (var row in Query(WmiRoot, "SELECT InstanceName, PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus", ct))
            {
                var disk = FindByInstance(ctx, Str(row, "InstanceName"));
                if (disk is null)
                {
                    continue;
                }

                disk.SmartSupported = true;
                if (row.TryGetValue("PredictFailure", out var pf) && pf is bool b && b)
                {
                    disk.PredictFailure = true;
                }
            }
        });

        // MSStorageDriver_FailurePredictData carries the same 512-byte attribute blob as MSStorageDriver_ATAPISmartData
        // (the latter is the older name of the same ATA SMART READ DATA buffer), so only one of them is queried.
        TryStep(local, "SMART attributes", () =>
        {
            var thresholds = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in Query(WmiRoot, "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds", ct))
                {
                    if (Str(row, "InstanceName") is { } name && row.GetValueOrDefault("VendorSpecific") is byte[] blob)
                    {
                        thresholds[name] = blob;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SMART thresholds unavailable.");
            }

            foreach (var row in Query(WmiRoot, "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData", ct))
            {
                var name = Str(row, "InstanceName");
                var disk = FindByInstance(ctx, name);
                if (disk is null || row.GetValueOrDefault("VendorSpecific") is not byte[] values || name is null)
                {
                    continue;
                }

                thresholds.TryGetValue(name, out var thresholdBlob);
                var attributes = SmartBlobParser.Parse(values, thresholdBlob ?? []);
                if (attributes.Count == 0)
                {
                    continue;
                }

                disk.Attributes = attributes.ToList();
                var facts = SmartBlobParser.ExtractFacts(attributes);
                disk.SmartTemperatureC ??= facts.TemperatureC;
                disk.PowerOnHours ??= facts.PowerOnHours;
                disk.Reallocated ??= facts.ReallocatedSectors;
                disk.Pending ??= facts.PendingSectors;
                disk.Uncorrectable ??= facts.UncorrectableErrors;
                disk.WearPercent ??= facts.WearPercent;
                disk.HostWritesGb ??= facts.HostWritesGb;
            }
        });

        TryStep(local, "drive sensors", () => ReadMonitorSensors(ctx));

        foreach (var note in local)
        {
            ctx.Warnings.Add("Drive health: " + note);
        }
    }

    /// <summary>Reads temperature, remaining life and total writes from the shared LibreHardwareMonitor host.</summary>
    private void ReadMonitorSensors(AnalysisContext ctx)
    {
        if (!_monitor.IsOpen)
        {
            if (_monitor.OpenError is { } reason)
            {
                _logger.LogDebug("Hardware monitor closed, skipping drive sensors: {Reason}", reason);
            }

            return;
        }

        _monitor.WithComputer(computer =>
        {
            foreach (var hardware in computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Storage)
                {
                    continue;
                }

                var disk = FindByModel(ctx, hardware.Name);
                if (disk is null)
                {
                    continue;
                }

                try
                {
                    hardware.Update();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not update storage hardware {Name}.", hardware.Name);
                }

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.Value is not { } value || float.IsNaN(value))
                    {
                        continue;
                    }

                    switch (sensor.SensorType)
                    {
                        case SensorType.Temperature:
                            // Only the composite ("Composite Temperature" / "Temperature") is the drive temperature.
                            // "Temperature #2" is often a hotter controller sensor, and "Warning Temperature" /
                            // "Critical Temperature" are the drive's limits (often 70 °C and 80 °C), not readings.
                            var info = DriveTemperatureRules.Classify(sensor.Name);
                            if (info.Role == DriveSensorRole.Drive)
                            {
                                disk.MonitorTemperatureC ??= DriveTemperatureRules.Plausible(value);
                            }
                            else if (info.Role == DriveSensorRole.Limit)
                            {
                                disk.ReportedLimitsC.Add(DriveTemperatureRules.Plausible(value));
                            }

                            break;
                        case SensorType.Level when sensor.Name.Equals("Remaining Life", StringComparison.OrdinalIgnoreCase):
                            disk.WearPercent ??= Math.Clamp(100 - value, 0, 100);
                            break;
                        case SensorType.Level when sensor.Name.Equals("Percentage Used", StringComparison.OrdinalIgnoreCase):
                            disk.WearPercent ??= Math.Clamp(value, 0, 100);
                            break;
                        case SensorType.Data when sensor.Name.Equals("Total Bytes Written", StringComparison.OrdinalIgnoreCase)
                                               || sensor.Name.Equals("Data Written", StringComparison.OrdinalIgnoreCase):
                            disk.HostWritesGb ??= (long)value;
                            break;
                    }
                }
            }
        });
    }

    private async Task CollectReliabilityCountersAsync(AnalysisContext ctx, CancellationToken ct)
    {
        if (ctx.DiskBuilders.Count == 0)
        {
            return;
        }

        const string script = "Get-PhysicalDisk | ForEach-Object { $c = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
                              "[pscustomobject]@{ DeviceId = [string]$_.DeviceId; SerialNumber = [string]$_.SerialNumber; " +
                              "Temperature = $c.Temperature; TemperatureMax = $c.TemperatureMax; Wear = $c.Wear; PowerOnHours = $c.PowerOnHours; " +
                              "ReadErrorsTotal = $c.ReadErrorsTotal; WriteErrorsTotal = $c.WriteErrorsTotal; " +
                              "ReadErrorsUncorrected = $c.ReadErrorsUncorrected; WriteErrorsUncorrected = $c.WriteErrorsUncorrected } }";
        var rows = await PowerShellRunner.RunJsonAsync<List<ReliabilityCounterRow>>(script, PowerShellTimeout, ct).ConfigureAwait(false);
        if (rows is null)
        {
            ctx.Warnings.Add("Drive health: storage reliability counters were not available (PowerShell storage module).");
            return;
        }

        foreach (var row in rows)
        {
            var disk = FindByIndex(ctx, row.DeviceId) ?? FindBySerial(ctx, row.SerialNumber);
            if (disk is null)
            {
                continue;
            }

            // Temperature is the current value (0 = not reported); TemperatureMax is a limit and is never the reading.
            disk.ReliabilityTemperatureC ??= DriveTemperatureRules.FromReliabilityCounter(row.Temperature, row.TemperatureMax);

            if (row.Wear is >= 0)
            {
                disk.WearPercent ??= row.Wear;
            }

            if (row.PowerOnHours is > 0)
            {
                disk.PowerOnHours ??= row.PowerOnHours;
            }

            long uncorrected = (row.ReadErrorsUncorrected ?? 0) + (row.WriteErrorsUncorrected ?? 0);
            if (uncorrected > 0)
            {
                disk.Uncorrectable ??= uncorrected;
            }
        }
    }

    private void FinalizeDisks(AnalysisContext ctx)
    {
        var monitoring = _settings.Current.Monitoring;
        foreach (var b in ctx.DiskBuilders.OrderBy(d => d.Index))
        {
            b.TemperatureC = DriveTemperatureRules.Choose(
                [
                    (DriveTemperatureSource.HardwareMonitor, b.MonitorTemperatureC),
                    (DriveTemperatureSource.Smart, b.SmartTemperatureC),
                    (DriveTemperatureSource.ReliabilityCounter, b.ReliabilityTemperatureC),
                ],
                [.. b.ReportedLimitsC]);
            string? smartText = b.PredictFailure ? "Predicting failure" : b.PhysicalDiskHealth ?? (b.SmartSupported ? "OK" : null);
            var draft = new DiskHealth(b.Model, b.Serial, b.Firmware, b.SizeBytes, b.MediaType, HealthStatus.Unknown, smartText,
                b.TemperatureC, b.PowerOnHours, b.Reallocated, b.Pending, b.Uncorrectable, b.WearPercent, b.HostWritesGb, b.Attributes);
            var verdict = SmartRules.Evaluate(draft, monitoring.DiskWarnC, monitoring.DiskCritC);
            bool anyData = b.SmartSupported || b.PhysicalDiskHealth is not null || b.TemperatureC is not null || b.Attributes.Count > 0 || b.WearPercent is not null;
            var status = anyData ? verdict.Status : HealthStatus.Unknown;
            var disk = draft with { Status = status };
            ctx.Disks.Add(disk);
            ctx.Items.Add(HealthItemRules.Disk(disk, anyData ? verdict : new SmartRules.Verdict(HealthStatus.Unknown, "No health data was available for this drive.", [])));
        }
    }

    private static DiskBuilder? FindByIndex(AnalysisContext ctx, string? deviceId) =>
        int.TryParse(deviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? ctx.DiskBuilders.FirstOrDefault(d => d.Index == index) : null;

    private static DiskBuilder? FindBySerial(AnalysisContext ctx, string? serial)
    {
        var key = NormalizeSerial(serial);
        return key.Length == 0 ? null : ctx.DiskBuilders.FirstOrDefault(d => NormalizeSerial(d.Serial) == key);
    }

    private static DiskBuilder? FindByInstance(AnalysisContext ctx, string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return null;
        }

        // "SCSI\Disk&Ven_...\4&1a2b3c&0&000000_0" → strip the "_0" suffix to get the PNP device id.
        var trimmed = InstanceSuffix().Replace(instanceName.Trim(), string.Empty);
        return ctx.DiskBuilders.FirstOrDefault(d => d.PnpId is { } p && p.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ?? ctx.DiskBuilders.FirstOrDefault(d => d.PnpId is { } p && trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static DiskBuilder? FindByModel(AnalysisContext ctx, string? name)
    {
        var key = NormalizeModel(name);
        if (key.Length == 0)
        {
            return null;
        }

        return ctx.DiskBuilders.FirstOrDefault(d => NormalizeModel(d.Model) == key)
            ?? ctx.DiskBuilders.FirstOrDefault(d => NormalizeModel(d.Model).Contains(key, StringComparison.Ordinal) || key.Contains(NormalizeModel(d.Model), StringComparison.Ordinal));
    }

    public static string NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? string.Empty : NonAlphanumeric().Replace(model, string.Empty).ToUpperInvariant();

    public static string NormalizeSerial(string? serial) =>
        string.IsNullOrWhiteSpace(serial) ? string.Empty : NonAlphanumeric().Replace(serial, string.Empty).ToUpperInvariant();

    [GeneratedRegex(@"_\d+$")]
    private static partial Regex InstanceSuffix();

    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    private sealed class DiskBuilder
    {
        public int Index { get; set; }
        public string Model { get; set; } = string.Empty;
        public string? Serial { get; set; }
        public string? Firmware { get; set; }
        public long SizeBytes { get; set; }
        public string? PnpId { get; set; }
        public string MediaType { get; set; } = "Unknown";
        public string? BusType { get; set; }
        public string? PhysicalDiskHealth { get; set; }
        public bool SmartSupported { get; set; }
        public bool PredictFailure { get; set; }
        public double? TemperatureC { get; set; }
        public double? MonitorTemperatureC { get; set; }
        public double? SmartTemperatureC { get; set; }
        public double? ReliabilityTemperatureC { get; set; }
        public List<double?> ReportedLimitsC { get; } = [];
        public long? PowerOnHours { get; set; }
        public long? Reallocated { get; set; }
        public long? Pending { get; set; }
        public long? Uncorrectable { get; set; }
        public double? WearPercent { get; set; }
        public long? HostWritesGb { get; set; }
        public List<SmartAttribute> Attributes { get; set; } = [];
    }

    private sealed class ReliabilityCounterRow
    {
        public string? DeviceId { get; set; }
        public string? SerialNumber { get; set; }
        public double? Temperature { get; set; }
        public double? TemperatureMax { get; set; }
        public double? Wear { get; set; }
        public long? PowerOnHours { get; set; }
        public long? ReadErrorsTotal { get; set; }
        public long? WriteErrorsTotal { get; set; }
        public long? ReadErrorsUncorrected { get; set; }
        public long? WriteErrorsUncorrected { get; set; }
    }
}
