using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
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
/// The drive collectors live in <c>WindowsHealthService.Disks.cs</c>, the event-log/reliability/crash-dump ones in
/// <c>WindowsHealthService.Events.cs</c> and the Windows Update/security ones in <c>WindowsHealthService.Security.cs</c>.
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
    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(10);

    private readonly IHardwareMonitorHost _monitor;
    private readonly ISensorService _sensors;
    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsHealthService> _logger;
    private readonly Storage.IDriveInventoryService? _drives;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsHealthService(IHardwareMonitorHost monitor, ISensorService sensors, ISettingsService settings, ILogger<WindowsHealthService> logger,
        Storage.IDriveInventoryService? drives = null)
    {
        _monitor = monitor;
        _sensors = sensors;
        _settings = settings;
        _logger = logger;
        _drives = drives;
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
                ("Drive health (SMART)", ct => CollectDisksAsync(ctx, ct)),
                ("Drive health (reliability counters)", ct => CollectReliabilityCountersAsync(ctx, ct)),
                ("Battery", ct => Task.Run(() => CollectBatteries(ctx, ct), ct)),
                ("Device problems", ct => Task.Run(() => CollectDeviceProblems(ctx, ct), ct)),
                ("Event log errors", ct => Task.Run(() => CollectSystemErrors(ctx, ct), ct)),
                ("Reliability history", ct => Task.Run(() => CollectReliability(ctx, ct), ct)),
                ("Crash dumps", ct => Task.Run(() => CollectCrashDumps(ctx), ct)),
                ("Hardware errors (WHEA) and graphics driver resets", ct => Task.Run(() => CollectHardwareErrors(ctx, ct), ct)),
                ("AMD chipset driver", ct => Task.Run(() => CollectAmdPlatform(ctx, ct), ct)),
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

}
