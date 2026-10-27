using PcChecker.Core.Common;
using PcChecker.Core.Health;
using PcChecker.Core.Storage;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// RAID pass-through drives, USB flash sticks, ATA CHECK POWER MODE over SAT, PCIe link property ids, dynamic-disk
/// letters and one verdict for the Health and Storage pages.
/// </summary>
public class DriveInventoryReviewTests
{
    [Fact]
    public void PcieLinkPropertyIds_MatchPciprop()
    {
        // pciprop.h: 7/8 are MaxPayloadSize / MaxReadRequestSize, which must never be read as link facts.
        Assert.Equal(9u, DriveText.PciCurrentLinkSpeed);
        Assert.Equal(10u, DriveText.PciCurrentLinkWidth);
        Assert.Equal(11u, DriveText.PciMaxLinkSpeed);
        Assert.Equal(12u, DriveText.PciMaxLinkWidth);
    }

    [Theory]
    [InlineData("Intel Raid 0 Volume", null, true)]
    [InlineData("AMD-RAID Array 1 SCSI Disk Device", null, true)]
    [InlineData("HP LOGICAL VOLUME", null, true)]
    [InlineData(null, @"SCSI\DISK&VEN_INTEL&PROD_RAID_1_VOLUME\4&1", true)]
    [InlineData("NVMe Samsung SSD 980", @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG_SSD_980\5&1", false)]
    [InlineData("WDC PC SN730 SDBPNTY-512G-1101", null, false)]
    public void RaidArray_IsToldApartFromADriveBehindTheRaidDriver(string? model, string? pnp, bool array)
    {
        Assert.Equal(array, DriveInventoryJoin.LooksLikeRaidArray(model, pnp));
    }

    [Fact]
    public void Join_SingleNvmeBehindIntelRst_IsADiskReadAsNvme()
    {
        var joined = DriveInventoryJoin.Join(
            [new MsftDiskRow(0, "NVMe Samsung SSD 980", "0025_38B1", 1_000_204_886_016, DriveMediaRules.BusRaid, 2)],
            [new MsftPhysicalDiskRow("0", "NVMe Samsung SSD 980", "S64DNX0R1", "1B4QFXO7", 1_000_204_886_016, 0, DriveMediaRules.BusRaid)],
            [new Win32DiskRow(0, "NVMe Samsung SSD 980", "0025_38B1", "1B4QFXO7", 1_000_204_886_016, "SCSI", null, @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG_SSD_980\5&1")]);

        var drive = Assert.Single(joined);
        Assert.Equal(DriveRole.Disk, drive.Role);
        Assert.True(drive.IsRaidPassThrough);
        Assert.True(drive.LooksNvme);

        var facts = DriveFactsBuilder.Build(drive, new DriveProbe(), SmartAccess.NotRead, null, "C:\\");
        Assert.Equal("NVMe", facts.BusType);
        Assert.Equal(DriveMedia.Ssd, facts.Media);
    }

    [Fact]
    public void Join_IntelRaidVolume_StaysAnArray()
    {
        var joined = DriveInventoryJoin.Join(
            [new MsftDiskRow(0, "Intel Raid 0 Volume", null, 2_000_000_000_000, DriveMediaRules.BusRaid, 2)],
            [],
            [new Win32DiskRow(0, "Intel Raid 0 Volume", null, null, 2_000_000_000_000, "SCSI", null, @"SCSI\DISK&VEN_INTEL&PROD_RAID_0_VOLUME\4&1")]);

        var drive = Assert.Single(joined);
        Assert.Equal(DriveRole.RaidArray, drive.Role);
        Assert.False(drive.IsRaidPassThrough);
    }

    [Theory]
    [InlineData("SanDisk Ultra USB 3.0 USB Device", @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA_USB_3.0\1", null, true)]
    [InlineData("Generic- SD/MMC USB Device", @"USBSTOR\DISK&VEN_GENERIC-&PROD_SD/MMC\1", null, true)]
    [InlineData("Kingston DataTraveler 3.0 USB Device", null, null, true)]
    [InlineData("WD Elements 25A3 USB Device", @"USBSTOR\DISK&VEN_WD&PROD_ELEMENTS_25A3\1", null, false)]
    [InlineData("Samsung Portable SSD T7", null, 4, false)]
    [InlineData("Generic Flash Disk USB Device", null, 3, false)]
    public void UsbFlash_IsToldApartFromAUsbEnclosure(string model, string? pnp, int? mediaType, bool flash)
    {
        Assert.Equal(flash, DriveInventoryJoin.LooksLikeUsbFlash(model, pnp, mediaType));
    }

    [Fact]
    public void UsbFlashStick_IsLabelledAFlashDrive_NotAnSsd()
    {
        var joined = DriveInventoryJoin.Join(
            [new MsftDiskRow(5, "SanDisk Ultra USB 3.0", "4C530001", 64_000_000_000, DriveMediaRules.BusUsb, 1)],
            [],
            [new Win32DiskRow(5, "SanDisk Ultra USB 3.0 USB Device", "4C530001", "1.00", 64_000_000_000, "USB", "Removable Media", @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA_USB_3.0\1")]);
        var facts = DriveFactsBuilder.Build(Assert.Single(joined), new DriveProbe(SeekPenalty: false), SmartAccess.NotSupported, null, "C:\\");
        Assert.Equal(DriveMedia.FlashDrive, facts.Media);
        Assert.Equal("Flash drive", DriveMediaRules.Label(facts.Media));
    }

    [Fact]
    public void CheckPowerMode_SatSense_DescriptorAndFixedFormats()
    {
        // Descriptor format (72h), RECOVERED ERROR, ASC/ASCQ 00h/1Dh, ATA Status Return descriptor with count 00h (standby).
        var descriptor = new byte[22];
        descriptor[0] = 0x72;
        descriptor[1] = 0x01;
        descriptor[2] = 0x00;
        descriptor[3] = 0x1D;
        descriptor[7] = 14;
        descriptor[8] = 0x09;
        descriptor[9] = 0x0C;
        descriptor[13] = 0x00;
        descriptor[21] = 0x50;
        Assert.Equal((byte)0x00, AtaCommands.CountFromSatSense(descriptor));
        descriptor[13] = 0xFF;
        Assert.Equal(DrivePowerState.Active, AtaCommands.PowerFromCheckPowerMode(AtaCommands.CountFromSatSense(descriptor)));

        // Fixed format (70h): INFORMATION = ERROR, STATUS, DEVICE, COUNT.
        var fixedSense = new byte[18];
        fixedSense[0] = 0x70;
        fixedSense[2] = 0x01;
        fixedSense[6] = 0x00;
        fixedSense[12] = 0x00;
        fixedSense[13] = 0x1D;
        Assert.Equal(DrivePowerState.Asleep, AtaCommands.PowerFromCheckPowerMode(AtaCommands.CountFromSatSense(fixedSense)));

        // A bridge that did not run the command (no 00h/1Dh) must not make an awake drive look asleep.
        fixedSense[13] = 0x00;
        Assert.Null(AtaCommands.CountFromSatSense(fixedSense));
        Assert.Equal(DrivePowerState.Unknown, AtaCommands.PowerFromCheckPowerMode(null));
    }

    [Fact]
    public void CheckPowerMode_SatCdbIsNonDataWithCheckCondition()
    {
        var cdb = AtaCommands.SatCdb16NonData(AtaCommands.CheckPowerMode());
        Assert.Equal(16, cdb.Length);
        Assert.Equal(0x85, cdb[0]);
        Assert.Equal(3 << 1, cdb[1]);
        Assert.Equal(0x20, cdb[2]);
        Assert.Equal(AtaCommands.CheckPowerModeCommand, cdb[14]);
    }

    [Fact]
    public void DynamicDisk_GetsItsLettersFromVolumeExtents()
    {
        var joined = DriveInventoryJoin.Join(
            [new MsftDiskRow(0, "Samsung SSD", "A1", 500_000_000_000, 11, 2), new MsftDiskRow(1, "WDC WD40EZAZ", "B2", 4_000_000_000_000, 11, 1), new MsftDiskRow(2, "WDC WD40EZAZ", "C3", 4_000_000_000_000, 11, 1)],
            [],
            [],
            [new PartitionRow(0, "C"), new PartitionRow(1, null, DriveInventoryJoin.MbrLdm), new PartitionRow(2, null, DriveInventoryJoin.MbrLdm)]);
        DriveInventoryJoin.AddVolumeLetters(joined, [("C:", [0]), ("D:", [1, 2]), ("E:", [2])]);

        Assert.Equal(["C:"], joined.Single(d => d.Number == 0).Volumes);
        Assert.Equal(["D:"], joined.Single(d => d.Number == 1).Volumes);
        Assert.Equal(["D:", "E:"], joined.Single(d => d.Number == 2).Volumes);
        Assert.True(joined.Single(d => d.Number == 1).IsDynamic);
    }

    [Fact]
    public void HealthPage_UsesTheStoragePageVerdict()
    {
        // One offline-uncorrectable sector is Caution on the Storage page, so the Health page shows a warning, not critical.
        var verdict = DriveHealthRules.Judge(new DriveHealthRules.Input(DriveMedia.Hdd, DriveRole.Disk, SmartAccess.Read,
            new SmartReading(DateTimeOffset.Now, [], new SmartValues { OfflineUncorrectable = 1 }, false, "SMART"), "Healthy"));
        Assert.Equal(DriveVerdict.Caution, verdict.Verdict);

        var health = HealthItemRules.FromDriveVerdict(verdict);
        Assert.Equal(HealthStatus.Warning, health.Status);
        Assert.StartsWith("Caution: ", health.Summary, StringComparison.Ordinal);

        var disk = new DiskHealth("WDC WD40EZAZ", null, null, 4_000_000_000_000, "HDD", HealthStatus.Warning, "OK", null, null, null, null, 1, null, null, []);
        var item = HealthItemRules.Disk(disk, health, verdict);
        Assert.Equal("WDC WD40EZAZ: Caution", item.Title);
        Assert.Equal(HealthStatus.Warning, item.Status);
    }

    [Fact]
    public void SleepingDrive_ReadingDayDecidesTheWording()
    {
        var now = new DateTimeOffset(2026, 10, 27, 15, 0, 0, TimeSpan.Zero).ToLocalTime();
        Assert.Equal(DriveSleepRules.ReadingDay.None, DriveSleepRules.DayOf(null, now));
        Assert.Equal(DriveSleepRules.ReadingDay.Today, DriveSleepRules.DayOf(now.AddMinutes(-5), now));
        Assert.Equal(DriveSleepRules.ReadingDay.Yesterday, DriveSleepRules.DayOf(now.AddDays(-1), now));
        Assert.Equal(DriveSleepRules.ReadingDay.Earlier, DriveSleepRules.DayOf(now.AddDays(-9), now));
    }
}
