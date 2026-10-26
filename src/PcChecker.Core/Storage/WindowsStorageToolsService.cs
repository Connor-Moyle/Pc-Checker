using System.Management;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Storage;

/// <summary>
/// Windows implementation of <see cref="IStorageToolsService"/>: physical disks from the Storage WMI namespace
/// (with a Win32_DiskDrive fallback), a read-only raw surface scan, an unbuffered benchmark, chkdsk / Repair-Volume /
/// Optimize-Volume through the change guard, and disk error events from the System log.
/// </summary>
public sealed partial class WindowsStorageToolsService : IStorageToolsService, ISurfaceScanner
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";
    private readonly IChangeGuard _changeGuard;
    private readonly ILogger<WindowsStorageToolsService> _logger;
    private IReadOnlyList<StorageDevice> _lastDevices = [];

    public WindowsStorageToolsService(IChangeGuard changeGuard, ILogger<WindowsStorageToolsService> logger)
    {
        _changeGuard = changeGuard;
        _logger = logger;
    }

    public async Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        var devices = await Task.Run(() =>
        {
            var systemRoot = SafeSystemRoot();
            try
            {
                var list = ReadStorageNamespace(systemRoot);
                if (list.Count > 0)
                {
                    return list;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MSFT_PhysicalDisk query failed; falling back to Win32_DiskDrive.");
            }

            try
            {
                return ReadWin32DiskDrive(systemRoot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Win32_DiskDrive query failed.");
                return [];
            }
        }, cancellationToken).ConfigureAwait(false);

        _lastDevices = devices;
        _logger.LogInformation("Found {Count} physical drives.", devices.Count);
        return devices;
    }

    public IReadOnlyList<VendorLink> GetVendorTools(IEnumerable<StorageDevice> devices) => StorageVendorRules.ToolsFor(devices);

    public async Task<IReadOnlyList<DiskErrorEvent>> GetDiskErrorEventsAsync(int days, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        days = Math.Clamp(days, 1, 365);
        string providers = string.Join(",", DiskEventCatalog.Providers.Distinct(StringComparer.OrdinalIgnoreCase).Select(PowerShellRunner.Quote));
        string ids = string.Join(",", DiskEventCatalog.EventIds);
        string command =
            $"Get-WinEvent -FilterHashtable @{{LogName='System'; ProviderName=@({providers}); Id=@({ids}); StartTime=(Get-Date).AddDays(-{days})}} -MaxEvents 300 -ErrorAction SilentlyContinue " +
            "| Select-Object @{n='Time';e={$_.TimeCreated.ToString('o')}}, @{n='Source';e={$_.ProviderName}}, @{n='Id';e={$_.Id}}, @{n='Message';e={$_.Message}}";

        try
        {
            var rows = await PowerShellRunner.RunJsonAsync<List<EventRow>>(command, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            if (rows is null)
            {
                return [];
            }

            var events = new List<DiskErrorEvent>(rows.Count);
            foreach (var row in rows)
            {
                if (!DateTimeOffset.TryParse(row.Time, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time))
                {
                    time = DateTimeOffset.MinValue;
                }

                string message = (row.Message ?? string.Empty).Trim();
                events.Add(new DiskErrorEvent(time, row.Source ?? "disk", row.Id, message, DiskEventCatalog.ExtractDevice(message)));
            }

            return events.OrderByDescending(e => e.Time).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read disk events from the System log.");
            return [];
        }
    }

    private sealed class EventRow
    {
        public string? Time { get; set; }
        public string? Source { get; set; }
        public long Id { get; set; }
        public string? Message { get; set; }
    }

    private static string? SafeSystemRoot()
    {
        try
        {
            return Path.GetPathRoot(Environment.SystemDirectory);
        }
        catch
        {
            return null;
        }
    }

    private StorageDevice? FindDevice(int diskNumber) => _lastDevices.FirstOrDefault(d => d.Number == diskNumber);

    private StorageDevice? FindDeviceByVolume(string driveLetter)
    {
        var letter = StorageCodes.NormalizeDriveLetter(driveLetter);
        return letter is null ? null : _lastDevices.FirstOrDefault(d => d.Volumes.Any(v => StorageCodes.NormalizeDriveLetter(v) == letter));
    }

    // ---- WMI readers -------------------------------------------------------------------------------------------

    private List<StorageDevice> ReadStorageNamespace(string? systemRoot)
    {
        var volumesByDisk = new Dictionary<int, List<string>>();
        using (var partitions = new ManagementObjectSearcher(new ManagementScope(StorageScope), new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
        {
            foreach (ManagementBaseObject p in partitions.Get())
            {
                int disk = ToInt(p["DiskNumber"]) ?? -1;
                string? letter = DriveLetterText(p["DriveLetter"]);
                if (disk < 0 || letter is null)
                {
                    continue;
                }

                if (!volumesByDisk.TryGetValue(disk, out var list))
                {
                    volumesByDisk[disk] = list = [];
                }

                list.Add(letter);
            }
        }

        var devices = new List<StorageDevice>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(StorageScope),
            new ObjectQuery("SELECT DeviceId, FriendlyName, SerialNumber, FirmwareVersion, Size, MediaType, BusType, HealthStatus FROM MSFT_PhysicalDisk"));
        foreach (ManagementBaseObject d in searcher.Get())
        {
            int number = ToInt(d["DeviceId"]) ?? -1;
            if (number < 0)
            {
                continue;
            }

            string bus = StorageCodes.BusType(ToInt(d["BusType"]));
            string media = StorageCodes.MediaType(ToInt(d["MediaType"]));
            if (media == "Unknown" && bus == "NVMe")
            {
                media = "SSD";
            }

            var volumes = volumesByDisk.TryGetValue(number, out var v) ? v.OrderBy(x => x).ToList() : [];
            devices.Add(new StorageDevice(
                number,
                Clean(d["FriendlyName"]) ?? $"Disk {number}",
                Clean(d["SerialNumber"]),
                Clean(d["FirmwareVersion"]),
                ToLong(d["Size"]) ?? 0,
                media,
                bus,
                StorageCodes.Health(ToInt(d["HealthStatus"])),
                volumes,
                StorageCodes.ContainsSystemDrive(volumes, systemRoot)));
        }

        return devices.OrderBy(x => x.Number).ToList();
    }

    private List<StorageDevice> ReadWin32DiskDrive(string? systemRoot)
    {
        var devices = new List<StorageDevice>();
        using var searcher = new ManagementObjectSearcher("SELECT Index, DeviceID, Model, SerialNumber, FirmwareRevision, Size, InterfaceType, MediaType, PNPDeviceID, Status FROM Win32_DiskDrive");
        foreach (ManagementBaseObject d in searcher.Get())
        {
            int number = ToInt(d["Index"]) ?? -1;
            if (number < 0)
            {
                continue;
            }

            var volumes = new List<string>();
            try
            {
                string deviceId = (Clean(d["DeviceID"]) ?? string.Empty).Replace("\\", "\\\\");
                using var parts = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{deviceId}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                foreach (ManagementBaseObject part in parts.Get())
                {
                    string partId = Clean(part["DeviceID"]) ?? string.Empty;
                    using var logicals = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                    foreach (ManagementBaseObject logical in logicals.Get())
                    {
                        if (StorageCodes.NormalizeDriveLetter(Clean(logical["DeviceID"])) is { } letter)
                        {
                            volumes.Add(letter);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Partition association lookup failed for disk {Number}.", number);
            }

            string bus = StorageCodes.BusTypeFromInterface(Clean(d["InterfaceType"]), Clean(d["PNPDeviceID"]));
            devices.Add(new StorageDevice(
                number,
                Clean(d["Model"]) ?? $"Disk {number}",
                Clean(d["SerialNumber"]),
                Clean(d["FirmwareRevision"]),
                ToLong(d["Size"]) ?? 0,
                StorageCodes.MediaTypeFromText(Clean(d["MediaType"]), bus),
                bus,
                StorageCodes.HealthFromStatusText(Clean(d["Status"])),
                volumes.OrderBy(x => x).ToList(),
                StorageCodes.ContainsSystemDrive(volumes, systemRoot)));
        }

        return devices.OrderBy(x => x.Number).ToList();
    }

    private static string? Clean(object? value)
    {
        var s = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static int? ToInt(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static long? ToLong(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string? DriveLetterText(object? value) => value switch
    {
        char c when c != '\0' && char.IsLetter(c) => char.ToUpperInvariant(c) + ":",
        string s => StorageCodes.NormalizeDriveLetter(s),
        ushort u when u != 0 => StorageCodes.NormalizeDriveLetter(((char)u).ToString()),
        _ => null,
    };
}
