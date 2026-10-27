using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Health;
using PcChecker.Core.Power;

namespace PcChecker.Core.Battery;

/// <summary>
/// Windows implementation of <see cref="IBatteryService"/>. Sources: <c>Win32_Battery</c> and
/// <c>Win32_ComputerSystem</c> (root\CIMV2); <c>BatteryStaticData</c>, <c>BatteryFullChargedCapacity</c> and
/// <c>BatteryCycleCount</c> (root\WMI); the live reading shared with the Power page (<see cref="WindowsBatteryReader"/>);
/// and <c>powercfg /batteryreport /xml</c> for the history. Every source is optional: a failure becomes a warning and
/// the page shows what the others returned. Nothing here changes the PC.
/// </summary>
public sealed class WindowsBatteryService : IBatteryService
{
    /// <summary>Battery reports are a few hundred kilobytes; anything far larger is not read.</summary>
    public const long MaxReportBytes = 32L * 1024 * 1024;

    private const string Cimv2 = @"\\.\root\CIMV2";
    private const string WmiRoot = @"\\.\root\WMI";

    private readonly ILogger<WindowsBatteryService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsBatteryService(ILogger<WindowsBatteryService> logger)
    {
        _logger = logger;
    }

    public BatteryOverview? Last { get; private set; }

    public string ReportsFolder => BatterySources.DefaultReportsFolder();

    public async Task<BatteryOverview> LoadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            Last = BatteryOverview.NoBattery(PcFormFactor.Unknown, DateTimeOffset.Now, ["Battery information is only available on Windows."]);
            return Last;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var warnings = new List<string>();
            progress?.Report(new ProgressUpdate("Reading the battery", 5));
            var formFactor = await Task.Run(() => ReadFormFactor(cancellationToken), cancellationToken).ConfigureAwait(false);
            var reading = await Task.Run(WindowsBatteryReader.Read, cancellationToken).ConfigureAwait(false);
            var wmiPacks = await Task.Run(() => ReadPacks(warnings, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (!reading.Present && wmiPacks.Count == 0)
            {
                _logger.LogInformation("No battery found ({FormFactor}).", formFactor);
                Last = BatteryOverview.NoBattery(formFactor, DateTimeOffset.Now);
                return Last;
            }

            if (reading.Note is { } note)
            {
                warnings.Add(note);
            }

            if (formFactor is PcFormFactor.Desktop or PcFormFactor.Server)
            {
                // A desktop's "battery" is a UPS on USB; its history is not worth a battery report run.
                Last = new BatteryOverview(true, formFactor, wmiPacks, BatterySources.Live(reading), BatteryReportData.Empty, DateTimeOffset.Now, []);
                return Last;
            }

            progress?.Report(new ProgressUpdate("Reading Windows' battery report", 40));
            var report = await ReadReportAsync(warnings, cancellationToken).ConfigureAwait(false);
            var packs = BatterySources.MergePacks(wmiPacks, report.Batteries);
            if (packs.Count == 0)
            {
                // GetSystemPowerStatus saw a battery that neither WMI nor the report describes.
                packs = [new BatteryPackInfo("Battery", null, null, null, reading.FullChargeCapacityMwh, null)];
            }

            Last = new BatteryOverview(true, formFactor, packs, BatterySources.Live(reading), report, DateTimeOffset.Now, warnings);
            progress?.Report(new ProgressUpdate("Done", 100));
            _logger.LogInformation("Battery read: {Packs} pack(s), {History} capacity rows, {Usage} usage rows, {Warnings} warnings.",
                packs.Count, report.CapacityHistory.Count, report.RecentUsage.Count, warnings.Count);
            return Last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<string>> SaveFullReportAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult<string>.Fail("The battery report is only available on Windows.");
        }

        string path;
        try
        {
            var folder = ReportsFolder;
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, BatterySources.ReportFileName(DateTime.Now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "The reports folder could not be created.");
            return OperationResult<string>.Fail("The reports folder could not be created: " + ex.Message, ex);
        }

        var result = await PowerCfg.RunAsync(["/batteryreport", "/output", path], cancellationToken).ConfigureAwait(false);
        if (!result.Success || !File.Exists(path))
        {
            _logger.LogWarning("powercfg /batteryreport failed with {Code}: {Error}", result.ExitCode, result.StdErr.Trim());
            return OperationResult<string>.Fail("Windows could not create the battery report." + Detail(result));
        }

        _logger.LogInformation("Battery report written to {Path}.", path);
        return OperationResult<string>.Ok(path, "The battery report was saved to " + path + ".");
    }

    // ---------------------------------------------------------------- battery report

    private async Task<BatteryReportData> ReadReportAsync(List<string> warnings, CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"pc-checker-battery-{Guid.NewGuid():N}.xml");
        try
        {
            var result = await PowerCfg.RunAsync(["/batteryreport", "/xml", "/output", path], ct).ConfigureAwait(false);
            if (!result.Success || !File.Exists(path))
            {
                _logger.LogWarning("powercfg /batteryreport /xml failed with {Code}: {Error}", result.ExitCode, result.StdErr.Trim());
                warnings.Add("Windows' battery report could not be created, so capacity history and recent usage are missing." + Detail(result));
                return BatteryReportData.Empty;
            }

            var length = new FileInfo(path).Length;
            if (length > MaxReportBytes)
            {
                warnings.Add("Windows' battery report is unusually large and was not read.");
                return BatteryReportData.Empty;
            }

            var xml = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return BatteryReportParser.Parse(xml);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "The battery report could not be read.");
            warnings.Add("Windows' battery report could not be read: " + ex.Message);
            return BatteryReportData.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The battery report file could not be opened.");
            warnings.Add("Windows' battery report could not be opened: " + ex.Message);
            return BatteryReportData.Empty;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Temporary battery report {Path} could not be deleted.", path);
            }
        }
    }

    private static string Detail(PowerCfgResult result)
    {
        var line = (result.StdErr.Length > 0 ? result.StdErr : result.StdOut)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? string.Empty : " (" + line + ")";
    }

    // ---------------------------------------------------------------- WMI

    private PcFormFactor ReadFormFactor(CancellationToken ct)
    {
        var row = SafeQuery(Cimv2, "SELECT PCSystemType FROM Win32_ComputerSystem", ct).FirstOrDefault();
        return BatteryPageRules.FormFactor((int?)Long(row, "PCSystemType"));
    }

    private List<BatteryPackInfo> ReadPacks(List<string> warnings, CancellationToken ct)
    {
        var batteries = SafeQuery(Cimv2, "SELECT Name, DeviceID, Chemistry, DesignCapacity, FullChargeCapacity FROM Win32_Battery", ct);
        var statics = SafeQuery(WmiRoot, "SELECT InstanceName, DesignedCapacity, ManufactureName, DeviceName, Chemistry FROM BatteryStaticData", ct);
        var fulls = SafeQuery(WmiRoot, "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity", ct);
        var cycles = SafeQuery(WmiRoot, "SELECT InstanceName, CycleCount FROM BatteryCycleCount", ct);

        int count = Math.Max(batteries.Count, statics.Count);
        var packs = new List<BatteryPackInfo>(count);
        for (int i = 0; i < count; i++)
        {
            var row = batteries.ElementAtOrDefault(i);
            var stat = statics.ElementAtOrDefault(i);
            var instance = Str(stat, "InstanceName");
            var full = Match(fulls, instance, i);
            var cycle = Match(cycles, instance, i);

            int? design = Positive(Long(stat, "DesignedCapacity")) ?? Positive(Long(row, "DesignCapacity"));
            int? fullCharge = Positive(Long(full, "FullChargedCapacity")) ?? Positive(Long(row, "FullChargeCapacity"));
            int? cycleCount = Positive(Long(cycle, "CycleCount")); // 0 means the firmware does not count cycles
            string? chemistry = BatterySources.DecodeChemistry(Long(stat, "Chemistry"));
            if (chemistry is null && Long(row, "Chemistry") is { } code)
            {
                chemistry = HealthCodes.BatteryChemistry((int)code);
            }

            var name = Str(stat, "DeviceName") ?? Str(row, "Name") ?? $"Battery {i + 1}";
            packs.Add(new BatteryPackInfo(name, Str(stat, "ManufactureName"), chemistry, design, fullCharge, cycleCount));
        }

        if (count > 0 && statics.Count == 0 && fulls.Count == 0)
        {
            warnings.Add("The battery driver does not share detailed data, so some values come from the battery report only.");
        }

        return packs;
    }

    private static Dictionary<string, object?>? Match(List<Dictionary<string, object?>> rows, string? instance, int index)
    {
        if (instance is not null)
        {
            var match = rows.FirstOrDefault(r => string.Equals(Str(r, "InstanceName"), instance, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return rows.ElementAtOrDefault(index);
    }

    private List<Dictionary<string, object?>> SafeQuery(string scopePath, string wql, CancellationToken ct)
    {
        try
        {
            var rows = new List<Dictionary<string, object?>>();
            var scope = new ManagementScope(scopePath);
            scope.Connect();
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = true };
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), options);
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // root\WMI battery classes are missing on desktops and on some drivers; that is expected, not an error.
            _logger.LogDebug(ex, "WMI query failed: {Query}", wql);
            return [];
        }
    }

    private static string? Str(Dictionary<string, object?>? row, string key) =>
        row is not null && row.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

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
                string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
                IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
        {
            return null;
        }
    }

    private static int? Positive(long? value) => value is > 0 and <= int.MaxValue ? (int)value.Value : null;
}
