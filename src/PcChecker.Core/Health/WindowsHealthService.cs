using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Health;

/// <summary>
/// Windows implementation of <see cref="IHealthService"/>. Every analyzer runs on its own and reports failures as
/// warnings, so a blocked WMI class or a missing driver never hides the rest of the report.
/// Sources: WMI (SMART, physical disks, batteries, PnP problems, reliability records, Security Center, BitLocker),
/// the shared LibreHardwareMonitor host (drive sensors), PowerShell storage reliability counters, the event log,
/// crash dump files, the registry (Windows Update) and the live sensor snapshot (thermal).
/// </summary>
public sealed partial class WindowsHealthService : IHealthService
{
    public const int ErrorWindowDays = 7;
    public const int ReliabilityWindowDays = 30;
    public const int MaxErrorEntries = 300;
    public const int MaxReliabilityEntries = 200;

    private const string Cimv2 = @"\\.\root\CIMV2";
    private const string WmiRoot = @"\\.\root\WMI";
    private const string StorageRoot = @"\\.\root\Microsoft\Windows\Storage";
    private const string SecurityCenter = @"\\.\root\SecurityCenter2";
    private const string VolumeEncryption = @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption";

    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(45);

    private readonly IHardwareMonitorHost _monitor;
    private readonly ISensorService _sensors;
    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsHealthService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsHealthService(IHardwareMonitorHost monitor, ISensorService sensors, ISettingsService settings, ILogger<WindowsHealthService> logger)
    {
        _monitor = monitor;
        _sensors = sensors;
        _settings = settings;
        _logger = logger;
    }

    public HealthReport? Last { get; private set; }

    public async Task<HealthReport> AnalyzeAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            Last = HealthReport.Empty("Health analysis is only available on Windows.");
            return Last;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ctx = new AnalysisContext(DateTimeOffset.Now);
            var steps = new (string Stage, Func<CancellationToken, Task> Work)[]
            {
                ("Drive health (SMART)", ct => Task.Run(() => CollectDisks(ctx, ct), ct)),
                ("Drive health (reliability counters)", ct => CollectReliabilityCountersAsync(ctx, ct)),
                ("Battery", ct => Task.Run(() => CollectBatteries(ctx, ct), ct)),
                ("Device problems", ct => Task.Run(() => CollectDeviceProblems(ctx, ct), ct)),
                ("Event log errors", ct => Task.Run(() => CollectSystemErrors(ctx, ct), ct)),
                ("Reliability history", ct => Task.Run(() => CollectReliability(ctx, ct), ct)),
                ("Crash dumps", ct => Task.Run(() => CollectCrashDumps(ctx), ct)),
                ("Memory diagnostic", ct => Task.Run(() => CollectMemoryDiagnostic(ctx), ct)),
                ("Windows Update", ct => Task.Run(() => CollectWindowsUpdate(ctx), ct)),
                ("Free space", ct => Task.Run(() => CollectFreeSpace(ctx), ct)),
                ("Temperatures", ct => Task.Run(() => CollectThermal(ctx), ct)),
                ("Security basics", ct => Task.Run(() => CollectSecurity(ctx, ct), ct)),
            };

            for (int i = 0; i < steps.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (stage, work) = steps[i];
                progress?.Report(new ProgressUpdate(stage, 100.0 * i / steps.Length));
                var watch = Stopwatch.StartNew();
                try
                {
                    await work(cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Health step {Stage} finished in {Ms} ms.", stage, watch.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health step {Stage} failed.", stage);
                    ctx.Warnings.Add($"{stage}: {Friendly(ex)}");
                }
            }

            FinalizeDisks(ctx);
            int score = HealthScoring.Score(ctx.Items);
            Last = new HealthReport(score, ctx.Items, ctx.Disks, ctx.Batteries, ctx.SystemErrors, ctx.DeviceProblems,
                ctx.Reliability, ctx.CrashDumps, ctx.Now, ctx.Warnings);
            progress?.Report(new ProgressUpdate("Done", 100));
            _logger.LogInformation("Health analysis finished: score {Score}, {Items} findings, {Warnings} warnings.", score, ctx.Items.Count, ctx.Warnings.Count);
            return Last;
        }
        finally
        {
            _gate.Release();
        }
    }

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
                disk.TemperatureC ??= facts.TemperatureC;
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
                        case SensorType.Temperature when sensor.Name.StartsWith("Temperature", StringComparison.OrdinalIgnoreCase):
                            disk.TemperatureC = value;
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
                              "Temperature = $c.Temperature; Wear = $c.Wear; PowerOnHours = $c.PowerOnHours; " +
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

            if (row.Temperature is > 0)
            {
                disk.TemperatureC ??= row.Temperature;
            }

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

    // ---------------------------------------------------------------- batteries

    private void CollectBatteries(AnalysisContext ctx, CancellationToken ct)
    {
        var batteries = Query(Cimv2, "SELECT Name, DeviceID, Chemistry, EstimatedChargeRemaining, BatteryStatus, DesignCapacity, FullChargeCapacity FROM Win32_Battery", ct);
        if (batteries.Count == 0)
        {
            return; // desktop
        }

        var statics = SafeQuery(WmiRoot, "SELECT InstanceName, DesignedCapacity FROM BatteryStaticData", ct);
        var fulls = SafeQuery(WmiRoot, "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity", ct);
        var cycles = SafeQuery(WmiRoot, "SELECT InstanceName, CycleCount FROM BatteryCycleCount", ct);

        for (int i = 0; i < batteries.Count; i++)
        {
            var row = batteries[i];
            int? design = (int?)Long(row, "DesignCapacity") ?? (int?)Long(statics.ElementAtOrDefault(i), "DesignedCapacity");
            int? full = (int?)Long(row, "FullChargeCapacity") ?? (int?)Long(fulls.ElementAtOrDefault(i), "FullChargedCapacity");
            int? cycle = (int?)Long(cycles.ElementAtOrDefault(i), "CycleCount");
            if (cycle is 0)
            {
                cycle = null; // many firmwares report 0 when they do not count cycles
            }

            double? wear = HealthScoring.BatteryWear(design, full);
            var battery = new BatteryHealth(
                Str(row, "Name") ?? $"Battery {i + 1}",
                design, full, wear, cycle,
                HealthCodes.BatteryChemistry((int?)Long(row, "Chemistry")),
                HealthCodes.BatteryStatus((int?)Long(row, "BatteryStatus")),
                (int?)Long(row, "EstimatedChargeRemaining"));
            ctx.Batteries.Add(battery);
            ctx.Items.Add(HealthItemRules.Battery(battery));
        }
    }

    // ---------------------------------------------------------------- devices

    private void CollectDeviceProblems(AnalysisContext ctx, CancellationToken ct)
    {
        foreach (var row in Query(Cimv2, "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0", ct))
        {
            int code = (int)(Long(row, "ConfigManagerErrorCode") ?? 0);
            if (code == 0)
            {
                continue;
            }

            var description = DeviceProblemCodes.Describe(code);
            ctx.DeviceProblems.Add(new DeviceProblem(Str(row, "Name") ?? "Unknown device", Str(row, "DeviceID") ?? string.Empty, code, description.Title, description.Suggestion));
        }

        ctx.DeviceProblems.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        ctx.Items.Add(HealthItemRules.DeviceProblems(ctx.DeviceProblems));
    }

    // ---------------------------------------------------------------- event log

    private void CollectSystemErrors(AnalysisContext ctx, CancellationToken ct)
    {
        var all = new List<SystemErrorEntry>();
        var notes = new List<string>();
        foreach (var log in new[] { "System", "Application" })
        {
            try
            {
                all.AddRange(ReadErrors(log, ErrorWindowDays, MaxErrorEntries, ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the {Log} event log.", log);
                notes.Add($"{log} log: {Friendly(ex)}");
            }
        }

        ctx.SystemErrors.AddRange(all.OrderByDescending(e => e.Time).Take(MaxErrorEntries));
        ctx.Items.Add(HealthItemRules.SystemErrors(ctx.SystemErrors, ErrorWindowDays));
        foreach (var n in notes)
        {
            ctx.Warnings.Add("Event log errors: " + n);
        }
    }

    private static List<SystemErrorEntry> ReadErrors(string log, int days, int max, CancellationToken ct)
    {
        long windowMs = days * 86_400_000L;
        var query = new EventLogQuery(log, PathType.LogName, $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]")
        {
            ReverseDirection = true,
        };
        var result = new List<SystemErrorEntry>();
        using var reader = new EventLogReader(query);
        for (EventRecord? record = reader.ReadEvent(); record is not null && result.Count < max; record = reader.ReadEvent())
        {
            ct.ThrowIfCancellationRequested();
            using (record)
            {
                result.Add(new SystemErrorEntry(
                    record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.Now,
                    log,
                    record.ProviderName ?? "Unknown",
                    record.Level == 1 ? Severity.Critical : Severity.High,
                    record.Id,
                    DescribeEvent(record)));
            }
        }

        return result;
    }

    private static string DescribeEvent(EventRecord record)
    {
        string? text = null;
        try
        {
            text = record.FormatDescription();
        }
        catch (Exception)
        {
            // The provider's message DLL is missing or the event is malformed; fall back below.
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var props = record.Properties.Select(p => p.Value?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).Take(4);
                text = $"Event {record.Id} from {record.ProviderName}" + (props.Any() ? ": " + string.Join(", ", props) : string.Empty);
            }
            catch (Exception)
            {
                text = $"Event {record.Id} from {record.ProviderName}";
            }
        }

        var oneLine = Whitespace().Replace(text!, " ").Trim();
        return oneLine.Length <= 600 ? oneLine : oneLine[..600] + "…";
    }

    private void CollectMemoryDiagnostic(AnalysisContext ctx)
    {
        int? eventId = null;
        DateTimeOffset? when = null;
        string? message = null;
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1201 or EventID=1202)]]")
        {
            ReverseDirection = true,
        };
        using (var reader = new EventLogReader(query))
        using (var record = reader.ReadEvent())
        {
            if (record is not null)
            {
                eventId = record.Id;
                when = record.TimeCreated is { } t ? new DateTimeOffset(t) : null;
                message = DescribeEvent(record);
            }
        }

        ctx.Items.Add(HealthItemRules.MemoryDiagnostic(eventId, when, message, ctx.Now));
    }

    // ---------------------------------------------------------------- reliability

    private void CollectReliability(AnalysisContext ctx, CancellationToken ct)
    {
        var cutoff = ctx.Now.AddDays(-ReliabilityWindowDays);
        var events = new List<ReliabilityEvent>();
        foreach (var row in Query(Cimv2, "SELECT SourceName, EventIdentifier, TimeGenerated, Message FROM Win32_ReliabilityRecords", ct))
        {
            if (!WmiPropertyMapper.TryParseDmtfDate(Str(row, "TimeGenerated"), out var time))
            {
                continue;
            }

            var when = new DateTimeOffset(time);
            if (when < cutoff)
            {
                continue;
            }

            var source = Str(row, "SourceName") ?? "Unknown";
            long id = Long(row, "EventIdentifier") ?? 0;
            var message = Whitespace().Replace(Str(row, "Message") ?? string.Empty, " ").Trim();
            events.Add(new ReliabilityEvent(when, source, ReliabilityClassifier.Classify(source, id), message.Length <= 400 ? message : message[..400] + "…"));
        }

        ctx.Reliability.AddRange(events.OrderByDescending(e => e.Time).Take(MaxReliabilityEntries));
    }

    // ---------------------------------------------------------------- crash dumps

    private void CollectCrashDumps(AnalysisContext ctx)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            windows = @"C:\Windows";
        }

        var files = new List<FileInfo>();
        var miniDir = Path.Combine(windows, "Minidump");
        try
        {
            if (Directory.Exists(miniDir))
            {
                files.AddRange(new DirectoryInfo(miniDir).EnumerateFiles("*.dmp"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Minidump folder could not be read.");
        }

        try
        {
            var memory = new FileInfo(Path.Combine(windows, "MEMORY.DMP"));
            if (memory.Exists)
            {
                files.Add(memory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MEMORY.DMP could not be inspected.");
        }

        foreach (var f in files.OrderByDescending(f => f.LastWriteTime))
        {
            ctx.CrashDumps.Add(new CrashDumpInfo(f.FullName, new DateTimeOffset(f.LastWriteTime), f.Length));
        }

        ctx.Items.Add(HealthItemRules.CrashDumps(ctx.CrashDumps, ctx.Now));
    }

    // ---------------------------------------------------------------- windows update

    private void CollectWindowsUpdate(AnalysisContext ctx)
    {
        DateTimeOffset? lastSuccess = null;
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install"))
        {
            if (key?.GetValue("LastSuccessTime") is string text && TryParseUpdateTime(text, out var parsed))
            {
                lastSuccess = parsed;
            }
        }

        bool rebootPending = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
                             || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        ctx.Items.Add(HealthItemRules.WindowsUpdate(lastSuccess, rebootPending, ctx.Now));
    }

    /// <summary>Parses the registry's "yyyy-MM-dd HH:mm:ss" UTC stamp.</summary>
    public static bool TryParseUpdateTime(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        {
            value = new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime();
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value))
        {
            value = value.ToLocalTime();
            return true;
        }

        return false;
    }

    private static bool KeyExists(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- free space

    private void CollectFreeSpace(AnalysisContext ctx)
    {
        var drives = new List<FreeSpaceRules.DriveSpace>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                drives.Add(new FreeSpaceRules.DriveSpace(drive.Name.TrimEnd('\\'), drive.TotalFreeSpace, drive.TotalSize));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Drive {Drive} could not be inspected.", drive.Name);
            }
        }

        ctx.Items.AddRange(FreeSpaceRules.Items(drives));
    }

    // ---------------------------------------------------------------- thermal

    private void CollectThermal(AnalysisContext ctx)
    {
        var snapshot = _sensors.Latest;
        if (snapshot is null || snapshot.Readings.Count == 0)
        {
            if (!_sensors.IsAvailable && _sensors.UnavailableReason is { } reason)
            {
                ctx.Warnings.Add("Temperatures: " + reason);
            }

            return;
        }

        ctx.Items.AddRange(HealthItemRules.Thermal(snapshot.Readings, _settings.Current.Monitoring));
    }

    // ---------------------------------------------------------------- security

    private void CollectSecurity(AnalysisContext ctx, CancellationToken ct)
    {
        var notes = new List<string>();
        TryStep(notes, "antivirus", () =>
        {
            var products = Query(SecurityCenter, "SELECT displayName, productState FROM AntiVirusProduct", ct)
                .Select(r => new SecurityRules.AntivirusProduct(Str(r, "displayName") ?? "Antivirus", (int)(Long(r, "productState") ?? 0)))
                .ToList();
            ctx.Items.Add(SecurityRules.AntivirusItem(products));
        });

        TryStep(notes, "firewall", () =>
        {
            IReadOnlyDictionary<string, bool> profiles = ReadFirewallFromRegistry();
            if (profiles.Count == 0)
            {
                profiles = SecurityRules.ParseNetshProfiles(RunNetsh("advfirewall show allprofiles", ct));
            }

            ctx.Items.Add(SecurityRules.FirewallItem(profiles));
        });

        TryStep(notes, "drive encryption", () =>
        {
            var volumes = new List<(string Drive, int ProtectionStatus)>();
            foreach (var row in Query(VolumeEncryption, "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume", ct))
            {
                var letter = Str(row, "DriveLetter");
                if (!string.IsNullOrWhiteSpace(letter))
                {
                    volumes.Add((letter, (int)(Long(row, "ProtectionStatus") ?? 2)));
                }
            }

            ctx.Items.Add(SecurityRules.BitLockerItem(volumes, ctx.Batteries.Count > 0));
        });

        foreach (var n in notes)
        {
            ctx.Warnings.Add("Security basics: " + n);
        }
    }

    private static Dictionary<string, bool> ReadFirewallFromRegistry()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var profiles = new (string Key, string Name)[] { ("DomainProfile", "Domain"), ("StandardProfile", "Private"), ("PublicProfile", "Public") };
        foreach (var (key, name) in profiles)
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + key);
                if (reg?.GetValue("EnableFirewall") is int enabled)
                {
                    result[name] = enabled != 0;
                }
            }
            catch (Exception)
            {
                // Policy-managed profiles can hide the value; netsh is the fallback.
            }
        }

        return result;
    }

    private static string? RunNetsh(string arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("netsh.exe", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(true);
            }

            ct.ThrowIfCancellationRequested();
            return output;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    private void TryStep(List<string> notes, string what, Action action)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Health sub-step {What} failed.", what);
            notes.Add($"{what} unavailable ({Friendly(ex)})");
        }
    }

    private static List<Dictionary<string, object?>> Query(string scopePath, string wql, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        var scope = new ManagementScope(scopePath);
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results)
        {
            ct.ThrowIfCancellationRequested();
            using (item)
            {
                var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in item.Properties)
                {
                    bag[property.Name] = property.Value;
                }

                rows.Add(bag);
            }
        }

        return rows;
    }

    private List<Dictionary<string, object?>> SafeQuery(string scopePath, string wql, CancellationToken ct)
    {
        try
        {
            return Query(scopePath, wql, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WMI query failed: {Query}", wql);
            return [];
        }
    }

    private static string? Str(Dictionary<string, object?>? row, string key)
    {
        if (row is null || !row.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var text = value is string s ? s : WmiPropertyMapper.FormatValue(key, value);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static long? Long(Dictionary<string, object?>? row, string key)
    {
        if (row is null || !row.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        try
        {
            return value switch
            {
                bool b => b ? 1 : 0,
                string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
                IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        ManagementException m => $"WMI reported {m.ErrorCode}",
        UnauthorizedAccessException => "access denied (run PC Checker as administrator)",
        EventLogException e => "event log: " + e.Message,
        _ => ex.Message,
    };

    [GeneratedRegex(@"_\d+$")]
    private static partial Regex InstanceSuffix();

    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class AnalysisContext(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; } = now;
        public List<DiskBuilder> DiskBuilders { get; } = [];
        public List<HealthItem> Items { get; } = [];
        public List<DiskHealth> Disks { get; } = [];
        public List<BatteryHealth> Batteries { get; } = [];
        public List<SystemErrorEntry> SystemErrors { get; } = [];
        public List<DeviceProblem> DeviceProblems { get; } = [];
        public List<ReliabilityEvent> Reliability { get; } = [];
        public List<CrashDumpInfo> CrashDumps { get; } = [];
        public List<string> Warnings { get; } = [];
    }

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
        public double? Wear { get; set; }
        public long? PowerOnHours { get; set; }
        public long? ReadErrorsTotal { get; set; }
        public long? WriteErrorsTotal { get; set; }
        public long? ReadErrorsUncorrected { get; set; }
        public long? WriteErrorsUncorrected { get; set; }
    }
}
