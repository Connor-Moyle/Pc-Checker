using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Storage;

/// <summary>
/// Windows drive inventory: MSFT_Disk, MSFT_PhysicalDisk and Win32_DiskDrive joined per drive (see
/// <see cref="DriveInventoryJoin"/>), Storage Spaces pools from the storage PowerShell module when present, then per drive
/// the seek penalty and power state, and SMART only when the drive is awake (or the person asked to wake it).
/// </summary>
public sealed class WindowsDriveInventoryService : IDriveInventoryService
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";
    private static readonly TimeSpan PoolTimeout = TimeSpan.FromSeconds(45);

    private readonly ILogger<WindowsDriveInventoryService> _logger;
    private readonly ISettingsService? _settings;
    private readonly DriveReadingCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<PhysicalDrive> _last = [];

    public WindowsDriveInventoryService(ILogger<WindowsDriveInventoryService> logger, ISettingsService? settings = null)
    {
        _logger = logger;
        _settings = settings;
        _cache = new DriveReadingCache(DriveReadingCache.DefaultPath());
    }

    public IReadOnlyList<PhysicalDrive> Last => _last;

    public async Task<IReadOnlyList<PhysicalDrive>> GetDrivesAsync(DriveReadMode mode, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var joined = await JoinAsync(cancellationToken).ConfigureAwait(false);
            var drives = await Task.Run(() => joined.Select(j => Finish(j, mode, cancellationToken)).ToList(), cancellationToken).ConfigureAwait(false);
            _cache.Save();
            _last = drives;
            _logger.LogInformation("Drive inventory: {Count} drives ({Asleep} asleep).", drives.Count, drives.Count(d => d.Power == DrivePowerState.Asleep));
            return drives;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PhysicalDrive?> RefreshDriveAsync(string key, DriveReadMode mode, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var joined = await JoinAsync(cancellationToken).ConfigureAwait(false);
            PhysicalDrive? refreshed = null;
            var drives = await Task.Run(() => joined.Select(j =>
            {
                // Only the chosen drive gets the requested mode; the others stay routine so they are not woken.
                var routine = Finish(j, DriveReadMode.Routine, cancellationToken);
                if (routine.Key != key)
                {
                    return routine;
                }

                refreshed = mode == DriveReadMode.Routine ? routine : Finish(j, mode, cancellationToken);
                return refreshed;
            }).ToList(), cancellationToken).ConfigureAwait(false);
            _cache.Save();
            _last = drives;
            return refreshed;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- join -------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<JoinedDrive>> JoinAsync(CancellationToken ct)
    {
        var (disks, physical, win32, partitions) = await Task.Run(() =>
        {
            var d = Guard("MSFT_Disk", () => ReadDisks(ct));
            var p = Guard("MSFT_PhysicalDisk", () => ReadPhysicalDisks(ct));
            var w = Guard("Win32_DiskDrive", () => ReadWin32(ct));
            var parts = Guard("MSFT_Partition", () => ReadPartitions(ct));
            return (d, p, w, parts);
        }, ct).ConfigureAwait(false);

        IReadOnlyList<StoragePoolRow> pools = [];
        bool hasPools = disks.Any(d => d.BusType == DriveMediaRules.BusSpaces)
                        || physical.Any(p => p.CannotPoolReasons?.Contains(DriveInventoryJoin.CannotPoolInPool) ?? false);
        if (hasPools)
        {
            pools = await ReadPoolsAsync(ct).ConfigureAwait(false);
        }

        return DriveInventoryJoin.Join(disks, physical, win32, partitions, pools);
    }

    private PhysicalDrive Finish(JoinedDrive joined, DriveReadMode mode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var monitoring = _settings?.Current.Monitoring;
        double warn = monitoring?.DiskWarnC ?? 55, crit = monitoring?.DiskCritC ?? 65;
        string? systemRoot = SafeSystemRoot();

        if (joined.Role == DriveRole.StorageSpace)
        {
            return DriveFactsBuilder.Build(joined, new DriveProbe(Power: DrivePowerState.Unknown), SmartAccess.NotApplicable, null, systemRoot, warn, crit);
        }

        if (joined.Number < 0)
        {
            const string note = "Windows hides drives inside a storage pool, so SMART cannot be read directly. The health Windows reports is shown.";
            return DriveFactsBuilder.Build(joined, new DriveProbe(), SmartAccess.NotSupported, null, systemRoot, warn, crit, note);
        }

        bool? seekPenalty = null;
        var power = DrivePowerState.Unknown;
        try
        {
            using var query = DriveNative.OpenQueryOnly(joined.Number);
            if (query is not null)
            {
                seekPenalty = DriveNative.SeekPenalty(query);
                power = DriveNative.PowerState(query);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Query handle for disk {Number} failed.", joined.Number);
        }

        string? pcie = joined.Bus == "NVMe" ? DriveNative.PcieLink(joined.PnpDeviceId) : null;
        string key = KeyFor(joined);

        if (!DriveSleepRules.ShouldReadSmart(power, mode))
        {
            var last = _cache.Get(key);
            var probe = new DriveProbe(seekPenalty, power, null, pcie);
            string note = DriveSleepRules.Describe(power, last?.ReadAt, DateTimeOffset.Now)
                          + ". It is not woken for routine checks; use Read now to wake it.";
            return DriveFactsBuilder.Build(joined, probe, SmartAccess.SkippedAsleep, last, systemRoot, warn, crit, note);
        }

        var (access, reading, identity, smartNote) = ReadSmart(joined, ct);
        if (reading is not null)
        {
            _cache.Put(key, reading);
        }

        if (mode == DriveReadMode.WakeIfAsleep && power == DrivePowerState.Asleep)
        {
            power = DrivePowerState.Active;
        }

        return DriveFactsBuilder.Build(joined, new DriveProbe(seekPenalty, power, identity, pcie), access, reading, systemRoot, warn, crit, smartNote);
    }

    private (SmartAccess Access, SmartReading? Reading, AtaIdentity? Identity, string? Note) ReadSmart(JoinedDrive joined, CancellationToken ct)
    {
        using var handle = DriveNative.OpenReadWrite(joined.Number, out var open);
        if (handle is null)
        {
            if (open == DriveNative.OpenResult.AccessDenied)
            {
                return (SmartAccess.NeedsAdmin, null, null, null);
            }

            return (SmartAccess.NotSupported, null, null, "Windows could not open this drive to read SMART.");
        }

        var now = DateTimeOffset.Now;
        try
        {
            if (joined.Bus == "NVMe")
            {
                var reading = DriveFactsBuilder.FromNvmeLog(DriveNative.NvmeHealthLog(handle), now, "NVMe health log");
                return reading is null
                    ? (SmartAccess.NotSupported, null, null, "The NVMe driver did not return the health log. Vendor drivers (for example some Intel and Samsung drivers) can hide it.")
                    : (SmartAccess.Read, reading, null, null);
            }

            if (joined.BusTypeCode is DriveMediaRules.BusSd or DriveMediaRules.BusMmc)
            {
                return (SmartAccess.NotSupported, null, null, "Memory cards do not report SMART health.");
            }

            if (joined.IsUsb)
            {
                return ReadViaSat(handle, joined, now, ct);
            }

            foreach (var (name, read) in new (string, Func<AtaCommands.TaskFile, byte[]?>)[]
                     {
                         ("SMART", tf => DriveNative.SmartRcv(handle, tf)),
                         ("ATA pass-through", tf => DriveNative.AtaPassThrough(handle, tf)),
                     })
            {
                ct.ThrowIfCancellationRequested();
                var values = read(AtaCommands.SmartRead(AtaCommands.SmartReadData));
                if (values is null || !AtaCommands.LooksLikeSmartData(values))
                {
                    continue;
                }

                var thresholds = read(AtaCommands.SmartRead(AtaCommands.SmartReadThresholds));
                var identity = AtaCommands.ParseIdentify(read(AtaCommands.Identify()) ?? []);
                var reading = DriveFactsBuilder.FromAtaSectors(values, thresholds, joined.Model, now, name);
                if (reading is not null)
                {
                    return (SmartAccess.Read, reading, identity, null);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SMART read failed for disk {Number}.", joined.Number);
        }

        if (joined.Role == DriveRole.RaidArray || joined.BusTypeCode == DriveMediaRules.BusRaid)
        {
            return (SmartAccess.HiddenByRaid, null, null, null);
        }

        return (SmartAccess.NotSupported, null, null, null);
    }

    private (SmartAccess, SmartReading?, AtaIdentity?, string?) ReadViaSat(Microsoft.Win32.SafeHandles.SafeFileHandle handle, JoinedDrive joined, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (name, build) in new (string, Func<AtaCommands.TaskFile, byte[]>)[]
                 {
                     ("SAT (16)", AtaCommands.SatCdb16),
                     ("SAT (12)", AtaCommands.SatCdb12),
                 })
        {
            ct.ThrowIfCancellationRequested();
            var values = DriveNative.ScsiPassThrough(handle, build(AtaCommands.SmartRead(AtaCommands.SmartReadData)));
            if (values is null || !AtaCommands.LooksLikeSmartData(values))
            {
                continue;
            }

            var thresholds = DriveNative.ScsiPassThrough(handle, build(AtaCommands.SmartRead(AtaCommands.SmartReadThresholds)));
            var identity = AtaCommands.ParseIdentify(DriveNative.ScsiPassThrough(handle, build(AtaCommands.Identify())) ?? []);
            var reading = DriveFactsBuilder.FromAtaSectors(values, thresholds, identity?.Model is { Length: > 0 } m ? m : joined.Model, now, "USB " + name);
            if (reading is not null)
            {
                return (SmartAccess.Read, reading, identity, null);
            }
        }

        return (SmartAccess.BlockedByEnclosure, null, null, null);
    }

    // ---- WMI ----------------------------------------------------------------------------------------------------

    private List<MsftDiskRow> ReadDisks(CancellationToken ct) =>
        Query(StorageScope, "SELECT Number, FriendlyName, SerialNumber, Size, BusType, PartitionStyle, IsOffline, IsReadOnly, IsSystem, IsBoot, HealthStatus, UniqueId FROM MSFT_Disk", ct,
            o => ToInt(o["Number"]) is { } n
                ? new MsftDiskRow(n, Str(o["FriendlyName"]), Str(o["SerialNumber"]), ToLong(o["Size"]) ?? 0, ToInt(o["BusType"]), ToInt(o["PartitionStyle"]),
                    ToBool(o["IsOffline"]), ToBool(o["IsReadOnly"]), ToBool(o["IsSystem"]), ToBool(o["IsBoot"]), ToInt(o["HealthStatus"]), Str(o["UniqueId"]))
                : null);

    private List<MsftPhysicalDiskRow> ReadPhysicalDisks(CancellationToken ct) =>
        Query(StorageScope, "SELECT DeviceId, FriendlyName, SerialNumber, FirmwareVersion, Size, MediaType, BusType, SpindleSpeed, HealthStatus, CannotPoolReason, UniqueId FROM MSFT_PhysicalDisk", ct,
            o => new MsftPhysicalDiskRow(Str(o["DeviceId"]), Str(o["FriendlyName"]), Str(o["SerialNumber"]), Str(o["FirmwareVersion"]), ToLong(o["Size"]) ?? 0,
                ToInt(o["MediaType"]), ToInt(o["BusType"]), ToLong(o["SpindleSpeed"]), ToInt(o["HealthStatus"]), ToIntArray(o["CannotPoolReason"]), Str(o["UniqueId"])));

    private List<Win32DiskRow> ReadWin32(CancellationToken ct) =>
        Query(null, "SELECT Index, Model, SerialNumber, FirmwareRevision, Size, InterfaceType, MediaType, PNPDeviceID, Status FROM Win32_DiskDrive", ct,
            o => ToInt(o["Index"]) is { } i
                ? new Win32DiskRow(i, Str(o["Model"]), Str(o["SerialNumber"]), Str(o["FirmwareRevision"]), ToLong(o["Size"]) ?? 0, Str(o["InterfaceType"]),
                    Str(o["MediaType"]), Str(o["PNPDeviceID"]), Str(o["Status"]))
                : null);

    private List<PartitionRow> ReadPartitions(CancellationToken ct) =>
        Query(StorageScope, "SELECT DiskNumber, DriveLetter, MbrType, GptType FROM MSFT_Partition", ct,
            o => ToInt(o["DiskNumber"]) is { } n ? new PartitionRow(n, Letter(o["DriveLetter"]), ToInt(o["MbrType"]), Str(o["GptType"])) : null);

    private async Task<IReadOnlyList<StoragePoolRow>> ReadPoolsAsync(CancellationToken ct)
    {
        const string script =
            "$ErrorActionPreference = 'SilentlyContinue'; @(Get-StoragePool -IsPrimordial $false | ForEach-Object { $p = $_; [pscustomobject]@{ " +
            "PoolName = [string]$p.FriendlyName; " +
            "Members = @($p | Get-PhysicalDisk | ForEach-Object { [pscustomobject]@{ DeviceId = [string]$_.DeviceId; SerialNumber = [string]$_.SerialNumber; UniqueId = [string]$_.UniqueId } }); " +
            "Spaces = @($p | Get-VirtualDisk | ForEach-Object { $d = $_ | Get-Disk; [pscustomobject]@{ Name = [string]$_.FriendlyName; Resiliency = [string]$_.ResiliencySettingName; " +
            "DataCopies = [int]$_.NumberOfDataCopies; DiskNumber = $(if ($d) { [int]$d.Number } else { -1 }) } }) } })";
        try
        {
            var rows = await PowerShellRunner.RunJsonAsync<List<PoolDto>>(script, PoolTimeout, ct).ConfigureAwait(false);
            return rows?.Where(r => !string.IsNullOrWhiteSpace(r.PoolName)).Select(r => new StoragePoolRow(
                r.PoolName!,
                (r.Members ?? []).Select(m => new PoolMemberKey(m.DeviceId, m.SerialNumber, m.UniqueId)).ToList(),
                (r.Spaces ?? []).Select(s => new VirtualDiskRow(s.Name, s.Resiliency, s.DataCopies, s.DiskNumber ?? -1)).ToList())).ToList() ?? [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Storage pool details unavailable.");
            return [];
        }
    }

    private List<T> Query<T>(string? scope, string wql, CancellationToken ct, Func<ManagementBaseObject, T?> map)
        where T : class
    {
        var list = new List<T>();
        using var searcher = scope is null
            ? new ManagementObjectSearcher(wql)
            : new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
        searcher.Options.Timeout = TimeSpan.FromSeconds(30);
        foreach (ManagementBaseObject row in searcher.Get())
        {
            ct.ThrowIfCancellationRequested();
            using (row)
            {
                try
                {
                    if (map(row) is { } item)
                    {
                        list.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipped a row of {Query}.", wql);
                }
            }
        }

        return list;
    }

    private List<T> Guard<T>(string what, Func<List<T>> read)
    {
        try
        {
            return read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drive inventory: {What} could not be read.", what);
            return [];
        }
    }

    private static string KeyFor(JoinedDrive joined) => new PhysicalDrive { Number = joined.Number, Model = joined.Model ?? string.Empty, Serial = joined.Serial, SizeBytes = joined.SizeBytes }.Key;

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

    private static string? Str(object? value)
    {
        var s = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static int? ToInt(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
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
            return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool ToBool(object? value) => value is bool b && b;

    private static IReadOnlyList<int> ToIntArray(object? value) => value switch
    {
        ushort[] u => u.Select(x => (int)x).ToList(),
        int[] i => i,
        uint[] ui => ui.Select(x => (int)x).ToList(),
        Array a => a.Cast<object>().Select(ToInt).Where(x => x is not null).Select(x => x!.Value).ToList(),
        _ => [],
    };

    private static string? Letter(object? value) => value switch
    {
        char c when c != '\0' && char.IsLetter(c) => char.ToUpperInvariant(c) + ":",
        ushort u when u != 0 && char.IsLetter((char)u) => char.ToUpperInvariant((char)u) + ":",
        string s => StorageCodes.NormalizeDriveLetter(s),
        _ => null,
    };

    private sealed class PoolDto
    {
        public string? PoolName { get; set; }
        public List<MemberDto>? Members { get; set; }
        public List<SpaceDto>? Spaces { get; set; }
    }

    private sealed class MemberDto
    {
        public string? DeviceId { get; set; }
        public string? SerialNumber { get; set; }
        public string? UniqueId { get; set; }
    }

    private sealed class SpaceDto
    {
        public string? Name { get; set; }
        public string? Resiliency { get; set; }
        public int? DataCopies { get; set; }
        public int? DiskNumber { get; set; }
    }
}
