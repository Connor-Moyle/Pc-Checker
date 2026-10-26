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
/// The drive collectors live in <c>WindowsHealthService.Disks.cs</c>.
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

        // Filter server-side: the RAC store can hold years of records and WMI compares DMTF timestamps as strings.
        string since = ManagementDateTimeConverter.ToDmtfDateTime(cutoff.LocalDateTime);
        string wql = $"SELECT SourceName, EventIdentifier, TimeGenerated, Message FROM Win32_ReliabilityRecords WHERE TimeGenerated >= '{since}'";
        foreach (var row in Query(Cimv2, wql, ct))
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

            // Read asynchronously so a hung netsh cannot block the timeout: the pipe drains while we wait, and a
            // process that is still running after NetshTimeout is killed, which also completes the read.
            var readOutput = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NetshTimeout);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                ct.ThrowIfCancellationRequested();
                return null;
            }

            return readOutput.GetAwaiter().GetResult();
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception)
        {
            // Already gone or access denied; nothing else to do.
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
