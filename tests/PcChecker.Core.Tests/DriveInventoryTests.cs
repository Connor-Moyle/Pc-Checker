using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Health;
using PcChecker.Core.Storage;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// Every physical drive once, with correct facts: SMART raw decoding per vendor, the media-type decision table, the
/// serial byte-swap fix, the de-duplicating join, the sleeping-drive rule and the Good / Caution / Bad verdict.
/// </summary>
public class DriveInventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 30, 0, TimeSpan.Zero);

    // ---- SMART raw decoding ---------------------------------------------------------------------------------------

    [Fact]
    public void PowerOnHours_UseLow32Bits_SeagateMillisecondsAboveAreIgnored()
    {
        // Seagate: 0x0123_0000_3B7E → milliseconds 0x0123 in the top word, 15,230 hours in the low 32 bits.
        long raw = (0x0123L << 32) | 15_230;
        Assert.Equal(15_230, SmartDecoder.Decode(SmartDecoder.PowerOnHoursId, raw, SmartVendor.Seagate));
        Assert.Equal(15_230, SmartDecoder.Decode(SmartDecoder.PowerOnHoursId, 15_230, SmartVendor.WesternDigital));
    }

    [Fact]
    public void PowerOnHours_ImplausibleValueIsDropped()
    {
        Assert.Null(SmartDecoder.Decode(SmartDecoder.PowerOnHoursId, 0xFFFF_FFFF, SmartVendor.Generic));
    }

    [Fact]
    public void CommandTimeout_SeagatePacksThreeCounters_LowWordIsTheCount()
    {
        // 188 raw 0x0002_0002_0003: three 16-bit counters; the count is the low word (3).
        long raw = (0x0002L << 32) | (0x0002L << 16) | 0x0003;
        Assert.Equal(3, SmartDecoder.Decode(SmartDecoder.CommandTimeoutId, raw, SmartVendor.Seagate));
        Assert.Equal("3", SmartDecoder.DisplayRaw(SmartDecoder.CommandTimeoutId, raw, SmartVendor.Seagate));
    }

    [Fact]
    public void SeagateReadErrorRate_ShowsErrorCountNotOperationCount()
    {
        // Seagate attribute 1: errors in the top 16 bits, operations (huge) in the low 32 bits.
        long raw = (0x0000L << 32) | 123_456_789;
        Assert.Equal(0, SmartDecoder.Decode(SmartDecoder.RawReadErrorRateId, raw, SmartVendor.Seagate));
        Assert.Equal(123_456_789, SmartDecoder.Decode(SmartDecoder.RawReadErrorRateId, raw, SmartVendor.WesternDigital));
    }

    [Theory]
    [InlineData(SmartDecoder.TemperatureId, 0x0030_0014_0024L, 36)]
    [InlineData(SmartDecoder.AirflowTemperatureId, 0x2A00_1A2BL, 43)]
    public void Temperatures_UseTheLowByteOnly(int id, long raw, long expected)
    {
        Assert.Equal(expected, SmartDecoder.Decode(id, raw, SmartVendor.Seagate));
    }

    [Fact]
    public void Counters_UseLow32Bits()
    {
        long raw = (0x00FFL << 32) | 8;
        foreach (int id in new[] { 5, 10, 12, 187, 197, 198, 199 })
        {
            Assert.Equal(8, SmartDecoder.Decode(id, raw, SmartVendor.Generic));
        }
    }

    [Theory]
    [InlineData("ST4000DM004-2CV104", SmartVendor.Seagate)]
    [InlineData("WDC WD4005FZBX-00K5WB0", SmartVendor.WesternDigital)]
    [InlineData("TOSHIBA HDWD130", SmartVendor.Toshiba)]
    [InlineData("Samsung SSD 870 EVO 1TB", SmartVendor.Samsung)]
    [InlineData("CT1000MX500SSD1", SmartVendor.Generic)]
    [InlineData("Stornado 9000", SmartVendor.Generic)]
    public void VendorFromModel(string model, SmartVendor expected) => Assert.Equal(expected, SmartDecoder.VendorFromModel(model));

    [Fact]
    public void Summarize_ReadsHddValuesAndThresholdFailures()
    {
        var values = SmartBlobParser.Build(
        [
            (5, 0x33, 199, 199, 8), (9, 0x32, 80, 80, 15_230), (10, 0x33, 100, 100, 0), (12, 0x32, 100, 100, 598),
            (188, 0x32, 100, 100, (2L << 32) | 1), (194, 0x22, 114, 98, 0x0030_0014_0024), (197, 0x32, 200, 200, 2),
            (198, 0x30, 100, 253, 1), (199, 0x32, 200, 200, 4), (3, 0x27, 20, 20, 9000),
        ]);
        var thresholds = SmartBlobParser.BuildThresholds([(5, 140), (3, 21)]);
        var attributes = SmartBlobParser.Parse(values, thresholds);
        var s = SmartDecoder.Summarize(attributes, "ST4000DM004");

        Assert.Equal(8, s.ReallocatedSectors);
        Assert.Equal(15_230, s.PowerOnHours);
        Assert.Equal(0, s.SpinRetries);
        Assert.Equal(598, s.PowerCycles);
        Assert.Equal(1, s.CommandTimeouts);
        Assert.Equal(36, s.TemperatureC);
        Assert.Equal(2, s.PendingSectors);
        Assert.Equal(1, s.OfflineUncorrectable);
        Assert.Equal(4, s.CrcErrors);
        Assert.Single(s.FailedPreFailAttributes); // 3 Spin-Up Time 20 <= 21 and pre-fail
        Assert.StartsWith("3 ", s.FailedPreFailAttributes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NvmeHealthLog_IsDecoded()
    {
        var log = new byte[512];
        log[1] = 309 & 0xFF;
        log[2] = 309 >> 8;
        log[3] = 100;
        log[4] = 10;
        log[5] = 3;
        BitConverter.TryWriteBytes(log.AsSpan(48, 8), 36_000_000L);
        BitConverter.TryWriteBytes(log.AsSpan(128, 8), 4_120L);
        BitConverter.TryWriteBytes(log.AsSpan(160, 8), 0L);
        var v = SmartDecoder.ParseNvmeHealthLog(log)!;
        Assert.Equal(36, v.TemperatureC);
        Assert.Equal(3, v.WearPercent);
        Assert.Equal(4_120, v.PowerOnHours);
        Assert.Equal(18_432, v.HostWritesGb);
        Assert.Equal(0, v.NvmeCriticalWarning);
    }

    // ---- media type decision table -------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 11, null, 7200L, "Hdd", 7200)]
    [InlineData(4, 11, null, 0L, "Ssd", null)]
    [InlineData(5, 17, null, null, "Scm", null)]
    [InlineData(0, 17, null, null, "Ssd", null)]            // NVMe is always solid state
    [InlineData(0, 11, false, null, "Ssd", null)]           // no seek penalty
    [InlineData(0, 11, true, 5400L, "Hdd", 5400)]           // seek penalty, RPM known
    [InlineData(0, 7, null, 0L, "Ssd", null)]               // USB stick: spindle speed 0
    [InlineData(0, 7, null, 0xFFFFFFFFL, "Unknown", null)]  // spindle speed unknown and nothing else
    [InlineData(0, 12, null, null, "FlashCard", null)]      // SD card
    [InlineData(null, null, null, 7200L, "Hdd", 7200)]
    public void MediaDecisionTable(int? mediaType, int? bus, bool? seekPenalty, long? spindle, string expected, int? rpm)
    {
        var d = DriveMediaRules.Decide(mediaType, bus, seekPenalty, spindle, "Generic drive");
        Assert.Equal(Enum.Parse<DriveMedia>(expected), d.Media);
        Assert.Equal(rpm, d.Rpm);
    }

    [Fact]
    public void MediaDecision_UsesAtaRotationRateAndModelAsLastResort()
    {
        Assert.Equal(DriveMedia.Ssd, DriveMediaRules.Decide(0, 7, null, 0xFFFFFFFF, "WD Elements", ataRotationRate: 1).Media);
        var hdd = DriveMediaRules.Decide(0, 7, null, 0xFFFFFFFF, "WD Elements", ataRotationRate: 5400);
        Assert.Equal(DriveMedia.Hdd, hdd.Media);
        Assert.Equal(5400, hdd.Rpm);
        Assert.Equal(DriveMedia.Ssd, DriveMediaRules.Decide(0, 11, null, null, "KINGSTON SA400S37240G SSD").Media);
    }

    // ---- serial clean-up -----------------------------------------------------------------------------------------

    [Fact]
    public void Serial_ByteSwappedWithEmbeddedSpace_IsSwappedBack()
    {
        // "WD-WCC4E1234567" right-justified in the 20-character ATA field and byte-swapped, then trimmed by WMI.
        Assert.Equal("WD-WCC4E1234567", DriveSerial.Clean("W -DCW4C1E325476"));
    }

    [Fact]
    public void Serial_ByteSwappedWithoutHint_UsesTheOtherSource()
    {
        Assert.Equal("Z1Z2ABCD", DriveSerial.Clean("1Z2ZBADC", reference: "Z1Z2ABCD"));
        Assert.Equal("Z1Z2ABCD", DriveSerial.Clean("  Z1Z2ABCD  ", reference: "Z1Z2ABCD"));
    }

    [Fact]
    public void Serial_HexEncodedRawField_IsDecoded()
    {
        // Older Windows: the raw (byte-swapped) ATA field as hex. "XW16ADL94527" swapped is "WX61DA9L5472".
        Assert.Equal("WX61DA9L5472", DriveSerial.Clean("5857313641444C3934353237"));
    }

    [Fact]
    public void Serial_NormalSerialsAreOnlyTrimmed()
    {
        Assert.Equal("S6Z2NJ0R512345X", DriveSerial.Clean("  S6Z2NJ0R512345X "));
        Assert.Equal("0025_38B1_4150_0001.", DriveSerial.Clean("0025_38B1_4150_0001."));
        Assert.Null(DriveSerial.Clean("   "));
    }

    [Fact]
    public void Serial_PlaceholdersAndAtaStrings()
    {
        Assert.True(DriveSerial.IsPlaceholder("000000000000"));
        Assert.True(DriveSerial.IsPlaceholder(null));
        Assert.False(DriveSerial.IsPlaceholder("WD-WX12D80K7E42"));

        // IDENTIFY words 10-19 hold "  WD-WX12D80K7E42" with each word's bytes swapped.
        var identify = new byte[512];
        var text = "     WD-WX12D80K7E42".PadRight(20);
        for (int i = 0; i < 20; i += 2)
        {
            identify[20 + i] = (byte)text[i + 1];
            identify[20 + i + 1] = (byte)text[i];
        }

        Assert.Equal("WD-WX12D80K7E42", DriveSerial.AtaString(identify, 10, 10));
    }

    // ---- de-duplication join -------------------------------------------------------------------------------------

    [Fact]
    public void Join_EveryDriveOnce_AcrossThreeSources()
    {
        var disks = new[]
        {
            new MsftDiskRow(0, "Samsung SSD 990 PRO 2TB", "0025_38B1_4150_0001.", 2_000_398_934_016, 17, 2),
            new MsftDiskRow(1, "ST4000DM004-2CV104", "ZFN1ABCD", 4_000_787_030_016, 11, 1),
            new MsftDiskRow(2, "Generic disk", null, 500_107_862_016, 11, 0, IsOffline: true),
        };
        var physical = new[]
        {
            // NVMe: the disk serial is an EUI-64 id, the physical serial is the real one. Joined by number + size.
            new MsftPhysicalDiskRow("0", "Samsung SSD 990 PRO 2TB", "S6Z2NJ0R512345X", "4B2QJXD7", 2_000_398_934_016, 4, 17),
            // Listed under another device id but the same serial: joined by serial, not added twice.
            new MsftPhysicalDiskRow("7", "ST4000DM004-2CV104", "ZFN1ABCD", "0001", 4_000_787_030_016, 3, 11, 5400),
            new MsftPhysicalDiskRow("2", "Generic disk", null, null, 500_107_862_016, 3, 11),
        };
        var win32 = new[]
        {
            new Win32DiskRow(0, "Samsung SSD 990 PRO 2TB", "0025_38B1_4150_0001.", "4B2QJXD7", 2_000_398_934_016, "SCSI"),
            new Win32DiskRow(1, "ST4000DM004-2CV104", "FZ1NBADC", "0001", 4_000_787_030_016, "IDE"),
            new Win32DiskRow(2, "Generic disk", null, null, 500_107_862_016, "IDE"),
            new Win32DiskRow(9, "Generic- SD/MMC", null, null, 0, "USB"), // empty card reader slot: not a drive
        };

        var joined = DriveInventoryJoin.Join(disks, physical, win32);

        Assert.Equal([0, 1, 2], joined.Select(j => j.Number));
        Assert.Equal("S6Z2NJ0R512345X", joined[0].Serial);
        Assert.Equal(4, joined[0].MediaTypeCode);
        Assert.Equal("ZFN1ABCD", joined[1].Serial);
        Assert.Equal(5400, joined[1].SpindleSpeed);
        Assert.True(joined[2].IsOffline);
        Assert.Empty(joined[2].Volumes);
    }

    [Fact]
    public void Join_StorageSpacesPoolMembersAreKeptAndMarked()
    {
        var disks = new[] { new MsftDiskRow(3, "Microsoft Storage Space Device", null, 1_999_844_147_200, 16, 2) };
        var physical = new[]
        {
            new MsftPhysicalDiskRow("1", "ST2000DM008", "ZFL5A1B2", "0001", 2_000_398_934_016, 3, 11, 7200, 0, [2], "U1"),
            new MsftPhysicalDiskRow("2", "ST2000DM008", "ZFL5C3D4", "0001", 2_000_398_934_016, 3, 11, 7200, 0, [2], "U2"),
        };
        var pools = new[]
        {
            new StoragePoolRow("Pool", [new PoolMemberKey("1", "ZFL5A1B2", "U1"), new PoolMemberKey("2", "ZFL5C3D4", "U2")], [new VirtualDiskRow("Space", "Mirror", 2, 3)]),
        };

        var joined = DriveInventoryJoin.Join(disks, physical, [new Win32DiskRow(3, "Microsoft Storage Space Device", null, null, 1_999_844_147_200)],
            [new PartitionRow(3, "G")], pools);

        Assert.Equal(3, joined.Count);
        var space = Assert.Single(joined, j => j.Role == DriveRole.StorageSpace);
        Assert.Equal("Two-way mirror", space.Resiliency);
        Assert.Equal(2, space.Members.Count);
        Assert.Equal(["G:"], space.Volumes);
        Assert.All(joined.Where(j => j.Role == DriveRole.PoolMember), m =>
        {
            Assert.Equal(-1, m.Number);
            Assert.Equal("Pool", m.PoolName);
        });
    }

    [Fact]
    public void Join_RaidUsbDynamicAndFallback()
    {
        var disks = new[]
        {
            new MsftDiskRow(0, "Intel Raid 1 Volume", null, 1_000_000_000_000, 8, 2),
            new MsftDiskRow(1, "WD Elements 25A3 USB Device", "000000000000", 4_000_752_599_040, 7, 1),
        };
        var partitions = new[]
        {
            new PartitionRow(1, null, MbrType: 0x42),
            new PartitionRow(1, "F"),
        };
        // Win32 lists a disk the storage namespace missed: it is added from Win32 alone.
        var win32 = new[] { new Win32DiskRow(5, "USB SD Reader", null, null, 31_914_983_424, "USB", "Removable Media", @"USBSTOR\DISK&VEN_GENERIC\X") };

        var joined = DriveInventoryJoin.Join(disks, [], win32, partitions);

        Assert.Equal(DriveRole.RaidArray, joined[0].Role);
        Assert.True(joined[1].IsUsb);
        Assert.True(joined[1].IsDynamic);
        Assert.Equal(["F:"], joined[1].Volumes);
        Assert.Equal(5, joined[2].Number);
        Assert.Equal("USB", joined[2].Bus);
    }

    [Fact]
    public void Join_SameNumberButDifferentSize_IsNotMerged()
    {
        var joined = DriveInventoryJoin.Join(
            [new MsftDiskRow(1, "Disk A", "AAAA1111", 1_000_204_886_016, 11, 2)],
            [new MsftPhysicalDiskRow("1", "Disk B", "BBBB2222", null, 500_107_862_016, 3, 11)],
            []);
        Assert.Equal(2, joined.Count);
        Assert.Equal(1, joined.Count(j => j.Number == 1));
    }

    // ---- sleeping drives -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DrivePowerState.Asleep, DriveReadMode.Routine, false)]
    [InlineData(DrivePowerState.Asleep, DriveReadMode.WakeIfAsleep, true)]
    [InlineData(DrivePowerState.Active, DriveReadMode.Routine, true)]
    [InlineData(DrivePowerState.Unknown, DriveReadMode.Routine, true)]
    public void SleepingDrivesAreNotWokenByRoutineRefreshes(DrivePowerState power, DriveReadMode mode, bool read) =>
        Assert.Equal(read, DriveSleepRules.ShouldReadSmart(power, mode));

    [Fact]
    public void AsleepText_SaysWhenTheLastReadingWas()
    {
        var culture = CultureInfo.InvariantCulture;
        var local = Now.ToLocalTime();
        Assert.Equal("Asleep, not read yet", DriveSleepRules.Describe(DrivePowerState.Asleep, null, Now, culture));
        Assert.StartsWith("Asleep, last reading at ", DriveSleepRules.Describe(DrivePowerState.Asleep, local.AddMinutes(-20), Now, culture), StringComparison.Ordinal);
        Assert.StartsWith("Asleep, last reading yesterday at ", DriveSleepRules.Describe(DrivePowerState.Asleep, local.AddDays(-1), Now, culture), StringComparison.Ordinal);
        Assert.StartsWith("Asleep, last reading on ", DriveSleepRules.Describe(DrivePowerState.Asleep, local.AddDays(-5), Now, culture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/hdd/2", 2)]
    [InlineData("/HDD/0", 0)]
    [InlineData("/nvme/0", null)]
    [InlineData("/lpc/nct6798d", null)]
    [InlineData("", null)]
    public void MonitorIdentifierToDiskNumber(string id, int? expected) => Assert.Equal(expected, DrivePowerProbe.DiskNumberFromMonitorId(id));

    // ---- health verdict ------------------------------------------------------------------------------------------

    private static SmartReading Reading(SmartValues values, bool predicts = false) => new(Now, [], values, predicts, "test");

    private static DriveHealthVerdict Judge(SmartValues values, DriveMedia media = DriveMedia.Hdd, string? windows = "Healthy", bool predicts = false) =>
        DriveHealthRules.Judge(new DriveHealthRules.Input(media, DriveRole.Disk, SmartAccess.Read, Reading(values, predicts), windows));

    [Fact]
    public void Verdict_GoodWhenNothingIsWrong()
    {
        var v = Judge(new SmartValues { ReallocatedSectors = 0, PendingSectors = 0, TemperatureC = 36, PowerOnHours = 15_000 });
        Assert.Equal(DriveVerdict.Good, v.Verdict);
        Assert.Equal("Good", v.Label);
    }

    [Fact]
    public void Verdict_CautionForEightReallocatedSectors_WithTheReason()
    {
        var v = Judge(new SmartValues { ReallocatedSectors = 8 });
        Assert.Equal(DriveVerdict.Caution, v.Verdict);
        Assert.Contains("8 reallocated sectors", v.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(50, 0, 0)]
    [InlineData(0, 10, 0)]
    [InlineData(0, 0, 10)]
    public void Verdict_BadForManyBadSectors(long reallocated, long pending, long offline) =>
        Assert.Equal(DriveVerdict.Bad, Judge(new SmartValues { ReallocatedSectors = reallocated, PendingSectors = pending, OfflineUncorrectable = offline }).Verdict);

    [Fact]
    public void Verdict_BadWhenTheDrivePredictsFailureOrAPreFailAttributeCrossesItsThreshold()
    {
        Assert.Equal(DriveVerdict.Bad, Judge(new SmartValues(), predicts: true).Verdict);
        var v = Judge(new SmartValues { FailedAttributes = ["3 Spin-Up Time"], FailedPreFailAttributes = ["3 Spin-Up Time"] });
        Assert.Equal(DriveVerdict.Bad, v.Verdict);
        Assert.Contains("3 Spin-Up Time", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_CrcErrorsPointAtTheCable()
    {
        var v = Judge(new SmartValues { CrcErrors = 12 });
        Assert.Equal(DriveVerdict.Caution, v.Verdict);
        Assert.Contains("cable", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_SpinRetriesOnlyCountForSpinningDisks()
    {
        Assert.Equal(DriveVerdict.Caution, Judge(new SmartValues { SpinRetries = 2 }).Verdict);
        Assert.Equal(DriveVerdict.Good, Judge(new SmartValues { SpinRetries = 2 }, DriveMedia.Ssd).Verdict);
    }

    [Fact]
    public void Verdict_NvmeWarningsAndWear()
    {
        Assert.Equal(DriveVerdict.Bad, Judge(new SmartValues { NvmeCriticalWarning = 0x04 }, DriveMedia.Ssd).Verdict);
        Assert.Equal(DriveVerdict.Caution, Judge(new SmartValues { WearPercent = 75 }, DriveMedia.Ssd).Verdict);
        Assert.Equal(DriveVerdict.Bad, Judge(new SmartValues { AvailableSparePercent = 5, AvailableSpareThreshold = 10 }, DriveMedia.Ssd).Verdict);
    }

    [Fact]
    public void Verdict_UnknownExplainsWhy()
    {
        var usb = DriveHealthRules.Judge(new DriveHealthRules.Input(DriveMedia.Hdd, DriveRole.Disk, SmartAccess.BlockedByEnclosure, null, null));
        Assert.Equal(DriveVerdict.Unknown, usb.Verdict);
        Assert.Contains("USB enclosure", usb.Reason, StringComparison.Ordinal);

        var raid = DriveHealthRules.Judge(new DriveHealthRules.Input(DriveMedia.Unknown, DriveRole.RaidArray, SmartAccess.HiddenByRaid, null, null));
        Assert.Contains("RAID", raid.Reason, StringComparison.Ordinal);

        var bad = DriveHealthRules.Judge(new DriveHealthRules.Input(DriveMedia.Hdd, DriveRole.PoolMember, SmartAccess.NotSupported, null, "Unhealthy"));
        Assert.Equal(DriveVerdict.Bad, bad.Verdict);
    }

    // ---- text ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Capacity_ShowsDecimalAndBinaryUnitsCorrectlyLabelled()
    {
        Assert.Equal("4.00 TB (3.64 TiB)", DriveText.Capacity(4_000_787_030_016));
        Assert.Equal("500 GB (466 GiB)", DriveText.Capacity(500_107_862_016));
    }

    [Theory]
    [InlineData(3, 3, "SATA 6 Gb/s")]
    [InlineData(2, 3, "SATA 3 Gb/s (drive supports 6 Gb/s)")]
    public void SataLinkText(int current, int max, string expected) => Assert.Equal(expected, DriveText.SataLink(current, max));

    [Theory]
    [InlineData(4, 4, 4, 4, "PCIe 4.0 x4")]
    [InlineData(3, 4, 4, 4, "PCIe 3.0 x4 (drive supports PCIe 4.0 x4)")]
    [InlineData(4, 2, 4, 4, "PCIe 4.0 x2 (drive supports PCIe 4.0 x4)")]
    public void PcieLinkText(int speed, int width, int maxSpeed, int maxWidth, string expected) =>
        Assert.Equal(expected, DriveText.PcieLink(speed, width, maxSpeed, maxWidth));

    [Fact]
    public void Identify_ReadsRotationRateAndSataSpeed()
    {
        var data = new byte[512];
        void Word(int index, int value)
        {
            data[index * 2] = (byte)(value & 0xFF);
            data[index * 2 + 1] = (byte)(value >> 8);
        }

        Word(76, 0x000E);   // supports 1.5, 3 and 6 Gb/s
        Word(77, 0x0004);   // running at Gen 2 (3 Gb/s)
        Word(82, 0x0001);   // SMART supported
        Word(85, 0x0001);   // SMART enabled
        Word(217, 7200);
        var id = AtaCommands.ParseIdentify(data)!;
        Assert.Equal(7200, id.RotationRate);
        Assert.Equal(2, id.SataCurrentGeneration);
        Assert.Equal(3, id.SataMaxGeneration);
        Assert.True(id.SmartSupported && id.SmartEnabled);
    }

    [Fact]
    public void SatCommandsCarrySmartRegisters()
    {
        var cdb16 = AtaCommands.SatCdb16(AtaCommands.SmartRead(AtaCommands.SmartReadData));
        Assert.Equal(16, cdb16.Length);
        Assert.Equal(0x85, cdb16[0]);
        Assert.Equal(0xD0, cdb16[4]);
        Assert.Equal(0x4F, cdb16[10]);
        Assert.Equal(0xC2, cdb16[12]);
        Assert.Equal(0xB0, cdb16[14]);

        var cdb12 = AtaCommands.SatCdb12(AtaCommands.SmartRead(AtaCommands.SmartReadThresholds));
        Assert.Equal(12, cdb12.Length);
        Assert.Equal(0xA1, cdb12[0]);
        Assert.Equal(0xD1, cdb12[3]);
        Assert.Equal(0xB0, cdb12[9]);
    }

    [Fact]
    public void BlankSmartSectorFromABridgeIsRejected()
    {
        Assert.False(AtaCommands.LooksLikeSmartData(new byte[512]));
        Assert.False(AtaCommands.LooksLikeSmartData(Enumerable.Repeat((byte)0xFF, 512).ToArray()));
        Assert.True(AtaCommands.LooksLikeSmartData(SmartBlobParser.Build([(5, 0x33, 100, 100, 0)])));
    }

    [Fact]
    public void ReadingCache_SurvivesARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-drive-cache-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "drive-readings.json");
        try
        {
            var attributes = SmartBlobParser.Parse(SmartBlobParser.Build([(5, 0x33, 199, 199, 8), (9, 0x32, 80, 80, 15_230)]));
            var reading = new SmartReading(Now, attributes, SmartDecoder.Summarize(attributes, "WDC WD4005FZBX"), false, "SMART");
            var cache = new DriveReadingCache(path);
            cache.Put("SN:ABC123", reading);
            cache.Save();

            var reloaded = new DriveReadingCache(path).Get("SN:ABC123");
            Assert.NotNull(reloaded);
            Assert.Equal(Now, reloaded!.ReadAt);
            Assert.Equal(8, reloaded.Values.ReallocatedSectors);
            Assert.Equal(2, reloaded.Attributes.Count);
            Assert.Null(new DriveReadingCache(Path.Combine(dir, "missing.json")).Get("SN:ABC123"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- demo ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Demo_ShowsTheFiveDrivesAndTwoPoolMembers()
    {
        var drives = DemoDriveInventoryService.Build(Now);
        Assert.Equal(7, drives.Count);
        Assert.Equal(drives.Count, drives.Select(d => d.Key).Distinct().Count());

        var nvme = drives.Single(d => d.Number == 0);
        Assert.Equal(DriveMedia.Ssd, nvme.Media);
        Assert.Equal("NVMe", nvme.BusType);
        Assert.Equal("PCIe 4.0 x4", nvme.LinkSpeed);
        Assert.True(nvme.IsSystemDisk);
        Assert.Equal(DriveVerdict.Good, nvme.Verdict.Verdict);

        var hdd = drives.Single(d => d.Model == DemoData.HddModel);
        Assert.Equal(DriveMedia.Hdd, hdd.Media);
        Assert.Equal(7200, hdd.Rpm);
        Assert.Equal("WD-WX12D80K7E42", hdd.Serial);
        Assert.Equal("SATA 6 Gb/s", hdd.LinkSpeed);
        Assert.Equal(DriveVerdict.Caution, hdd.Verdict.Verdict);
        Assert.Equal(8, hdd.Smart!.Values.ReallocatedSectors);

        var usb = drives.Single(d => d.IsUsbBridge);
        Assert.Equal(DrivePowerState.Asleep, usb.Power);
        Assert.Equal(SmartAccess.SkippedAsleep, usb.SmartAccess);
        Assert.NotNull(usb.Smart);
        Assert.Contains("Asleep, last reading", usb.SmartNote, StringComparison.Ordinal);

        var space = drives.Single(d => d.Role == DriveRole.StorageSpace);
        Assert.Equal("Two-way mirror", space.Resiliency);
        Assert.Equal(2, space.Members.Count);
        Assert.Equal(2, drives.Count(d => d.Role == DriveRole.PoolMember && d.Number == -1));
    }

    [Fact]
    public async Task Demo_ReadNowWakesTheSleepingDrive()
    {
        var service = new DemoDriveInventoryService();
        var drives = await service.GetDrivesAsync(DriveReadMode.Routine, CancellationToken.None);
        var usb = drives.Single(d => d.IsUsbBridge);
        var woken = await service.RefreshDriveAsync(usb.Key, DriveReadMode.WakeIfAsleep, CancellationToken.None);
        Assert.NotNull(woken);
        Assert.Equal(DrivePowerState.Active, woken!.Power);
        Assert.Equal(SmartAccess.Read, woken.SmartAccess);
        Assert.Equal(5400, woken.Rpm);
    }
}
