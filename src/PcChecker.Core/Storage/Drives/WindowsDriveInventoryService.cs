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
    private static readonly TimeSpan ReuseWindow = TimeSpan.FromSeconds(15);

    private IReadOnlyList<PhysicalDrive> _last = [];
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

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

        var requested = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The Health check, the Storage page and the surface scan often ask within moments of each other; a routine
            // request that waited for (or just followed) a finished refresh gets that result instead of a second pass of
            // WMI queries and SMART reads.
            if (mode == DriveReadMode.Routine && _last.Count > 0 && _lastAt >= requested - ReuseWindow)
            {
                return _last;
            }

            var joined = await JoinAsync(cancellationToken).ConfigureAwait(false);
            var drives = await Task.Run(() => joined.Select(j => Finish(j, mode, cancellationToken)).ToList(), cancellationToken).ConfigureAwait(false);
            _cache.Save();
            _last = drives;
            _lastAt = DateTimeOffset.UtcNow;
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
            var previous = _last.GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.First());
            PhysicalDrive? refreshed = null;
            var drives = await Task.Run(() => joined.Select(j =>
            {
                // Only the chosen drive is read (with the requested mode); the others keep their last result, so they
                // are neither woken nor sent another round of SMART commands.
                string driveKey = KeyFor(j);
                if (driveKey != key)
                {
                    return previous.TryGetValue(driveKey, out var kept) ? kept : Finish(j, DriveReadMode.Routine, cancellationToken);
                }

                refreshed = Finish(j, mode, cancellationToken);
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

        var joined = DriveInventoryJoin.Join(disks, physical, win32, partitions, pools);
        if (joined.Any(d => d.IsDynamic && d.Volumes.Count == 0))
        {
            // Dynamic (LDM) volumes have no letter in MSFT_Partition; the volume extents say which disks they live on.
            var extents = await Task.Run(() => DriveLetters().Select(l => (l, DriveNative.VolumeDiskNumbers(l))).ToList(), ct).ConfigureAwait(false);
            DriveInventoryJoin.AddVolumeLetters(joined, extents);
        }

        return joined;
    }

    private static IEnumerable<string> DriveLetters()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => d.Name.TrimEnd('\\')).ToList();
        }
        catch
        {
            return [];
        }
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

        // GetDevicePowerState only knows Windows' own idle spin-down. A drive that spun itself down (firmware standby or
        // APM timers, common on external disks) is found with ATA CHECK POWER MODE, which never spins it up.
        // A USB stick or card reader gets no ATA commands at all (unless Windows measured a seek penalty: then it is a disk).
        bool usbFlash = joined.IsUsbFlash && seekPenalty != true;
        if (mode == DriveReadMode.Routine && power != DrivePowerState.Asleep && seekPenalty != false && !joined.LooksNvme && !usbFlash
            && joined.Role == DriveRole.Disk && joined.BusTypeCode is not (DriveMediaRules.BusSd or DriveMediaRules.BusMmc))
        {
            if (CheckAtaPowerMode(joined) == DrivePowerState.Asleep)
            {
                power = DrivePowerState.Asleep;
            }
        }

        string? pcie = joined.LooksNvme ? DriveNative.PcieLink(joined.PnpDeviceId) : null;
        string key = KeyFor(joined);

        if (!DriveSleepRules.ShouldReadSmart(power, mode))
        {
            var last = _cache.Get(key);
            var probe = new DriveProbe(seekPenalty, power, null, pcie);
            string note = DriveSleepRules.Describe(power, last?.ReadAt, DateTimeOffset.Now)
                          + ". It is not woken for routine checks; use Read now to wake it.";
            return DriveFactsBuilder.Build(joined, probe, SmartAccess.SkippedAsleep, last, systemRoot, warn, crit, note);
        }

        var (access, reading, identity, smartNote) = ReadSmart(joined, usbFlash, ct);
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

    private DrivePowerState CheckAtaPowerMode(JoinedDrive joined)
    {
        try
        {
            using var handle = DriveNative.OpenReadWrite(joined.Number, out _);
            if (handle is null)
            {
                return DrivePowerState.Unknown;
            }

            var tf = AtaCommands.CheckPowerMode();
            byte? count = joined.IsUsb
                ? AtaCommands.CountFromSatSense(DriveNative.ScsiPassThroughSense(handle, AtaCommands.SatCdb16NonData(tf)) ?? [])
                : DriveNative.AtaPassThroughNoDataCount(handle, tf);
            return AtaCommands.PowerFromCheckPowerMode(count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CHECK POWER MODE failed for disk {Number}.", joined.Number);
            return DrivePowerState.Unknown;
        }
    }

    private (SmartAccess Access, SmartReading? Reading, AtaIdentity? Identity, string? Note) ReadSmart(JoinedDrive joined, bool usbFlash, CancellationToken ct)
    {
        if (usbFlash)
        {
            // No ATA commands: flash sticks and card readers have no SMART, and some hang on pass-through commands.
            return (SmartAccess.NotSupported, null, null, "USB flash drives and card readers do not report SMART health.");
        }

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

            if (joined.BusTypeCode == DriveMediaRules.BusRaid)
            {
                // Intel RST / VMD reports BusType RAID also for a single NVMe drive and newer drivers pass the health log
                // through, so it is tried first; the ATA paths below follow for SATA drives behind the RAID driver.
                var reading = DriveFactsBuilder.FromNvmeLog(DriveNative.NvmeHealthLog(handle), now, "NVMe health log (RAID driver)");
                if (reading is not null)
                {
                    return (SmartAccess.Read, reading, null, null);
                }
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

        if (joined.Role == DriveRole.RaidArray)
        {
            return (SmartAccess.HiddenByRaid, null, null, null);
        }

        if (joined.IsRaidPassThrough || joined.BusTypeCode == DriveMediaRules.BusRaid)
        {
            return (SmartAccess.HiddenByRaid, null, null,
                "The Intel RST or AMD RAID driver controls this drive and did not pass its SMART data through. The drive is in no array; the RAID software (Intel RST or AMD RAIDXpert) or the standard Windows driver shows its health.");
        }

        return (SmartAccess.NotSupported, null, null, null);
    }

    private (SmartAccess, SmartReading?, AtaIdentity?, string?) ReadViaSat(Microsoft.Win32.SafeHandles.SafeFileHandle handle, JoinedDrive joined, DateTimeOffset now, CancellationToken ct)
    {
        // UASP bridges often accept IOCTL_ATA_PASS_THROUGH; the classic USB mass storage bridges need SAT (16) or (12).
        foreach (var (name, read) in new (string, Func<AtaCommands.TaskFile, byte[]?>)[]
                 {
                     ("ATA pass-through", tf => DriveNative.AtaPassThrough(handle, tf)),
                     ("SAT (16)", tf => DriveNative.ScsiPassThrough(handle, AtaCommands.SatCdb16(tf))),
                     ("SAT (12)", tf => DriveNative.ScsiPassThrough(handle, AtaCommands.SatCdb12(tf))),
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
        using var results = searcher.Get();
        foreach (ManagementBaseObject row in results)
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

    private static string KeyFor(JoinedDrive joined) => DriveFactsBuilder.KeyFor(joined);

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
