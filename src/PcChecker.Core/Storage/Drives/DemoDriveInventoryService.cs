using PcChecker.Core.Common;
using PcChecker.Core.Health;

namespace PcChecker.Core.Storage;

/// <summary>
/// Demo drives for the sample gaming desktop, built through the same join and verdict code as the real inventory:
/// a Samsung 990 PRO NVMe (C:, disk 0), a WD Black 4 TB 7200 RPM hard disk with 8 reallocated sectors (D:, disk 1,
/// Caution), a Crucial MX500 SATA SSD (E:), a WD Elements USB hard disk that is asleep (F:), and a two-way mirror
/// storage space (G:) made from two Seagate 2 TB drives. Pure .NET.
/// </summary>
public sealed class DemoDriveInventoryService : IDriveInventoryService
{
    public const string SataSsdModel = "CT1000MX500SSD1";
    public const long SataSsdBytes = 1_000_204_886_016;
    public const string UsbModel = "WD Elements 25A3 USB Device";
    public const long UsbBytes = 4_000_752_599_040;
    public const string PoolMemberModel = "ST2000DM008-2FR102";
    public const long PoolMemberBytes = 2_000_398_934_016;
    public const string SpaceModel = "Microsoft Storage Space Device";
    public const long SpaceBytes = 1_999_844_147_200;

    private bool _usbAwake;
    private IReadOnlyList<PhysicalDrive> _last = [];

    public IReadOnlyList<PhysicalDrive> Last => _last;

    public async Task<IReadOnlyList<PhysicalDrive>> GetDrivesAsync(DriveReadMode mode, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        if (mode == DriveReadMode.WakeIfAsleep)
        {
            _usbAwake = true;
        }

        _last = Build(DateTimeOffset.Now, _usbAwake);
        return _last;
    }

    public async Task<PhysicalDrive?> RefreshDriveAsync(string key, DriveReadMode mode, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 400, 900).ConfigureAwait(false);
        var current = Build(DateTimeOffset.Now, _usbAwake);
        var target = current.FirstOrDefault(d => d.Key == key);
        if (target is not null && mode == DriveReadMode.WakeIfAsleep && target.Power == DrivePowerState.Asleep)
        {
            _usbAwake = true;
        }

        _last = Build(DateTimeOffset.Now, _usbAwake);
        return _last.FirstOrDefault(d => d.Number == target?.Number && d.Model == target?.Model) ?? _last.FirstOrDefault(d => d.Key == key);
    }

    /// <summary>The demo drives at <paramref name="now"/> (also used by tests and the demo storage tools).</summary>
    public static IReadOnlyList<PhysicalDrive> Build(DateTimeOffset now, bool usbAwake = false)
    {
        var disks = new[]
        {
            new MsftDiskRow(0, DemoData.NvmeModel, "0025_38B1_4150_0001.", DemoData.NvmeBytes, DriveMediaRules.BusNvme, 2, IsBoot: true, HealthStatus: 0),
            new MsftDiskRow(2, SataSsdModel, "2235E6A1B2C3", SataSsdBytes, 11, 2, HealthStatus: 0),
            new MsftDiskRow(1, DemoData.HddModel, "WD-WX12D80K7E42", DemoData.HddBytes, 11, 2, HealthStatus: 0),
            new MsftDiskRow(3, UsbModel, "5857313641444C3934353237", UsbBytes, DriveMediaRules.BusUsb, 2, HealthStatus: 0),
            new MsftDiskRow(4, SpaceModel, "{6b2c4f1e-0d6a-4a52-9c41-2f6c1d3e5a77}", SpaceBytes, DriveMediaRules.BusSpaces, 2, HealthStatus: 0),
        };
        var physical = new[]
        {
            new MsftPhysicalDiskRow("0", DemoData.NvmeModel, "S6Z2NJ0R512345X", "4B2QJXD7", DemoData.NvmeBytes, 4, DriveMediaRules.BusNvme, 0, 0),
            new MsftPhysicalDiskRow("2", SataSsdModel, "2235E6A1B2C3", "M3CR046", SataSsdBytes, 4, 11, 0, 0),
            new MsftPhysicalDiskRow("1", DemoData.HddModel, "WD-WX12D80K7E42", "01.01A01", DemoData.HddBytes, 3, 11, 7200, 0),
            new MsftPhysicalDiskRow("3", UsbModel, "WX61DA9L5472", "1026", UsbBytes, 0, DriveMediaRules.BusUsb, 0xFFFFFFFF, 0),
            new MsftPhysicalDiskRow("5", PoolMemberModel, "ZFL5A1B2", "0001", PoolMemberBytes, 3, 11, 7200, 0, [DriveInventoryJoin.CannotPoolInPool], "POOL-M1"),
            new MsftPhysicalDiskRow("6", PoolMemberModel, "ZFL5C3D4", "0001", PoolMemberBytes, 3, 11, 7200, 0, [DriveInventoryJoin.CannotPoolInPool], "POOL-M2"),
        };
        var win32 = new[]
        {
            new Win32DiskRow(0, DemoData.NvmeModel, "0025_38B1_4150_0001.", "4B2QJXD7", DemoData.NvmeBytes, "SCSI", "Fixed hard disk media", @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG_SSD_990\5&DEMO0001&0&000000"),
            new Win32DiskRow(2, SataSsdModel, "2235E6A1B2C3", "M3CR046", SataSsdBytes, "IDE", "Fixed hard disk media", @"SCSI\DISK&VEN_&PROD_CT1000MX500SSD1\4&DEMO0003&0&020000"),
            // Some Windows builds hand back the ATA serial byte-swapped; the join fixes it.
            new Win32DiskRow(1, DemoData.HddModel, "W -DXW218DK0E724", "01.01A01", DemoData.HddBytes, "IDE", "Fixed hard disk media", @"SCSI\DISK&VEN_WDC&PROD_WD4005FZBX-00K5WB0\4&DEMO0002&0&010000"),
            new Win32DiskRow(3, UsbModel, "5857313641444C3934353237", "1026", UsbBytes, "USB", "External hard disk media", @"USBSTOR\DISK&VEN_WD&PROD_ELEMENTS_25A3&REV_1026\DEMO0004&0"),
            new Win32DiskRow(4, SpaceModel, null, null, SpaceBytes, "SCSI", "Fixed hard disk media", @"STORAGE\DISK\{DEMO-SPACE}"),
        };
        var partitions = new[]
        {
            new PartitionRow(0, null, GptType: "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"),
            new PartitionRow(0, null),
            new PartitionRow(0, "C"),
            new PartitionRow(0, null),
            new PartitionRow(1, "D"),
            new PartitionRow(2, "E"),
            new PartitionRow(3, "F"),
            new PartitionRow(4, "G"),
        };
        var pools = new[]
        {
            new StoragePoolRow("Storage pool",
                [new PoolMemberKey("5", "ZFL5A1B2", "POOL-M1"), new PoolMemberKey("6", "ZFL5C3D4", "POOL-M2")],
                [new VirtualDiskRow("Mirror space", "Mirror", 2, 4)]),
        };

        var joined = DriveInventoryJoin.Join(disks, physical, win32, partitions, pools);
        var monitoring = new Settings.MonitoringSettings();
        var result = new List<PhysicalDrive>();
        foreach (var j in joined)
        {
            result.Add(j.Number switch
            {
                0 => DriveFactsBuilder.Build(j, new DriveProbe(false, DrivePowerState.Active, null, "PCIe 4.0 x4"), SmartAccess.Read,
                    DriveFactsBuilder.FromNvmeLog(NvmeLog(), now, "NVMe health log"), "C:\\", monitoring.DiskWarnC, monitoring.DiskCritC),
                2 => DriveFactsBuilder.Build(j, new DriveProbe(false, DrivePowerState.Active, Identity(SataSsdModel, 1, 3)), SmartAccess.Read,
                    DriveFactsBuilder.FromAtaSectors(SataSsdValues(), SataSsdThresholds(), SataSsdModel, now, "SMART"), "C:\\", monitoring.DiskWarnC, monitoring.DiskCritC),
                1 => DriveFactsBuilder.Build(j, new DriveProbe(true, DrivePowerState.Active, Identity(DemoData.HddModel, 7200, 3)), SmartAccess.Read,
                    DriveFactsBuilder.FromAtaSectors(HddValues(), HddThresholds(), DemoData.HddModel, now, "SMART"), "C:\\", monitoring.DiskWarnC, monitoring.DiskCritC),
                3 => UsbDrive(j, now, usbAwake, monitoring),
                _ => j.Role == DriveRole.StorageSpace
                    ? DriveFactsBuilder.Build(j, new DriveProbe(), SmartAccess.NotApplicable, null, "C:\\", monitoring.DiskWarnC, monitoring.DiskCritC)
                    : DriveFactsBuilder.Build(j, new DriveProbe(), SmartAccess.NotSupported, null, "C:\\", monitoring.DiskWarnC, monitoring.DiskCritC,
                        "Windows hides drives inside a storage pool, so SMART cannot be read directly. The health Windows reports is shown."),
            });
        }

        return result;
    }

    private static PhysicalDrive UsbDrive(JoinedDrive j, DateTimeOffset now, bool awake, Settings.MonitoringSettings monitoring)
    {
        if (awake)
        {
            return DriveFactsBuilder.Build(j, new DriveProbe(true, DrivePowerState.Active, Identity("WDC WD40NDZW-11BCSS1", 5400, 3)), SmartAccess.Read,
                DriveFactsBuilder.FromAtaSectors(UsbValues(), HddThresholds(), "WDC WD40NDZW-11BCSS1", now, "USB SAT (16)"), "C:\\", monitoring.DiskWarnC, monitoring.DiskCritC);
        }

        var last = DriveFactsBuilder.FromAtaSectors(UsbValues(), HddThresholds(), "WDC WD40NDZW-11BCSS1", now.AddDays(-1).Date.AddHours(18).AddMinutes(42), "USB SAT (16)");
        string note = DriveSleepRules.Describe(DrivePowerState.Asleep, last?.ReadAt, now) + ". It is not woken for routine checks; use Read now to wake it.";
        return DriveFactsBuilder.Build(j, new DriveProbe(true, DrivePowerState.Asleep), SmartAccess.SkippedAsleep, last, "C:\\", monitoring.DiskWarnC, monitoring.DiskCritC, note);
    }

    private static AtaIdentity Identity(string model, int rotation, int sataGen) =>
        new(model, string.Empty, string.Empty, rotation, sataGen, 3, true, true);

    private static byte[] NvmeLog()
    {
        var log = new byte[512];
        log[1] = 309 & 0xFF; // 36 °C
        log[2] = 309 >> 8;
        log[3] = 100;
        log[4] = 10;
        log[5] = 3;
        WriteCounter(log, 48, 36_000_000);   // data units written (× 512,000 bytes) ≈ 18.4 TB
        WriteCounter(log, 112, 611);
        WriteCounter(log, 128, 4_120);
        WriteCounter(log, 144, 23);
        return log;
    }

    private static void WriteCounter(byte[] log, int offset, long value) =>
        BitConverter.TryWriteBytes(log.AsSpan(offset, 8), value);

    private static byte[] HddValues() => SmartBlobParser.Build(
    [
        (1, 0x2F, 200, 200, 0),
        (3, 0x27, 180, 177, 3966),
        (4, 0x32, 100, 100, 612),
        (5, 0x33, 199, 199, 8),
        (7, 0x2E, 200, 200, 0),
        (9, 0x32, 80, 80, 15_230),
        (10, 0x32, 100, 100, 0),
        (12, 0x32, 100, 100, 598),
        (192, 0x32, 200, 200, 41),
        (193, 0x32, 196, 196, 12_804),
        (194, 0x22, 114, 98, 0x0030_0014_0024), // 36 °C now, 20 °C min, 48 °C max packed above
        (196, 0x32, 200, 200, 8),
        (197, 0x32, 200, 200, 0),
        (198, 0x30, 100, 253, 0),
        (199, 0x32, 200, 200, 0),
    ]);

    private static byte[] HddThresholds() => SmartBlobParser.BuildThresholds(
    [
        (1, 51), (3, 21), (4, 0), (5, 140), (7, 0), (9, 0), (10, 0), (12, 0), (192, 0), (193, 0), (194, 0), (196, 0), (197, 0), (198, 0), (199, 0),
    ]);

    private static byte[] UsbValues() => SmartBlobParser.Build(
    [
        (1, 0x2F, 200, 200, 0),
        (5, 0x33, 200, 200, 0),
        (9, 0x32, 94, 94, 4_870),
        (10, 0x32, 100, 100, 0),
        (12, 0x32, 100, 100, 211),
        (194, 0x22, 118, 104, 32),
        (197, 0x32, 200, 200, 0),
        (198, 0x30, 100, 253, 0),
        (199, 0x32, 200, 200, 0),
    ]);

    private static byte[] SataSsdValues() => SmartBlobParser.Build(
    [
        (1, 0x2F, 100, 100, 0),
        (5, 0x32, 100, 100, 0),
        (9, 0x32, 100, 100, 9_310),
        (12, 0x32, 100, 100, 1_204),
        (173, 0x32, 94, 94, 92),
        (194, 0x22, 67, 52, 0x0034_0011_0021), // 33 °C
        (197, 0x32, 100, 100, 0),
        (199, 0x32, 100, 100, 0),
        (202, 0x30, 94, 94, 6),
        (246, 0x30, 100, 100, 21_484_839_712),
    ]);

    private static byte[] SataSsdThresholds() => SmartBlobParser.BuildThresholds([(1, 0), (5, 10), (9, 0), (12, 0), (173, 0), (194, 0), (197, 0), (199, 0), (202, 1)]);
}
