using PcChecker.Core.Health;

namespace PcChecker.Core.Storage;

/// <summary>What the per-drive probe found out (without SMART): seek penalty, power state, IDENTIFY data, link speed.</summary>
public sealed record DriveProbe(
    bool? SeekPenalty = null,
    DrivePowerState Power = DrivePowerState.Unknown,
    AtaIdentity? Identity = null,
    string? PcieLink = null);

/// <summary>Turns a joined drive plus probe and SMART results into the <see cref="PhysicalDrive"/> people see. Pure.</summary>
public static class DriveFactsBuilder
{
    public static PhysicalDrive Build(
        JoinedDrive joined,
        DriveProbe probe,
        SmartAccess access,
        SmartReading? reading,
        string? systemRoot,
        double warnC = 55,
        double critC = 65,
        string? smartNote = null)
    {
        ArgumentNullException.ThrowIfNull(joined);
        ArgumentNullException.ThrowIfNull(probe);
        int? bus = joined.BusTypeCode is { } code && code != 0 ? code : BusCodeFromText(joined.BusText);
        if (joined.IsRaidPassThrough && joined.LooksNvme)
        {
            bus = DriveMediaRules.BusNvme; // an NVMe drive behind Intel RST / VMD reports BusType RAID
        }

        var media = DriveMediaRules.Decide(joined.MediaTypeCode, bus, probe.SeekPenalty, joined.SpindleSpeed, joined.Model, probe.Identity?.RotationRate);
        bool usb = joined.IsUsb;
        if (usb && joined.IsUsbFlash && media.Media != DriveMedia.Hdd)
        {
            media = DriveInventoryJoin.LooksLikeCardReader(joined.Model, joined.PnpDeviceId)
                ? new DriveMediaRules.Decision(DriveMedia.FlashCard, null, "USB card reader")
                : new DriveMediaRules.Decision(DriveMedia.FlashDrive, null, "USB flash drive");
        }

        string? link = probe.PcieLink;
        if (link is null && probe.Identity is { } id && !usb)
        {
            link = DriveText.SataLink(id.SataCurrentGeneration, id.SataMaxGeneration);
        }

        if (link is null && usb)
        {
            link = "USB";
        }

        string? serial = joined.Serial;
        if (DriveSerial.IsPlaceholder(serial) && probe.Identity is { Serial.Length: > 0 } identity && !DriveSerial.IsPlaceholder(identity.Serial))
        {
            serial = identity.Serial; // a USB bridge hid the serial; SAT passthrough read the real one
        }

        string? firmware = joined.Firmware ?? NullIfEmpty(probe.Identity?.Firmware);
        string? windowsHealth = HealthCodes.PhysicalDiskHealth(joined.HealthCode);
        bool isSystem = joined.IsBoot || StorageCodes.ContainsSystemDrive(joined.Volumes, systemRoot);

        var verdict = DriveHealthRules.Judge(new DriveHealthRules.Input(media.Media, joined.Role, access, reading, windowsHealth, warnC, critC));
        return new PhysicalDrive
        {
            Key = KeyFor(joined),
            Number = joined.Number,
            PhysicalDeviceId = joined.PhysicalDeviceId,
            Model = joined.Model ?? "Unknown drive",
            Serial = serial,
            Firmware = firmware,
            SizeBytes = joined.SizeBytes,
            Media = media.Media,
            Rpm = media.Media == DriveMedia.Hdd ? media.Rpm : null,
            BusType = joined.IsRaidPassThrough && joined.LooksNvme ? "NVMe" : joined.Bus,
            LinkSpeed = link,
            PartitionStyle = joined.Role == DriveRole.PoolMember ? "Used by the storage pool" : DriveText.PartitionStyle(joined.PartitionStyleCode, joined.IsDynamic),
            IsDynamic = joined.IsDynamic,
            IsOffline = joined.IsOffline,
            IsReadOnly = joined.IsReadOnly,
            IsSystemDisk = isSystem,
            IsBootDisk = joined.IsBoot,
            IsUsbBridge = usb,
            Volumes = [.. joined.Volumes],
            HiddenPartitions = joined.HiddenPartitions,
            Role = joined.Role,
            PoolName = joined.PoolName,
            Resiliency = joined.Resiliency,
            Members = [.. joined.Members],
            WindowsHealth = windowsHealth,
            PnpDeviceId = joined.PnpDeviceId,
            Power = probe.Power,
            SmartAccess = access,
            SmartNote = smartNote ?? (access is SmartAccess.Read ? null : DriveHealthRules.NoReadingReason(access)),
            Smart = reading,
            Verdict = verdict,
        };
    }

    /// <summary>
    /// The drive's <see cref="PhysicalDrive.Key"/>, from what Windows reports (not the serial read over SAT), so the
    /// Storage page, single-drive refreshes and the reading cache all use the same key.
    /// </summary>
    public static string KeyFor(JoinedDrive joined) => PhysicalDrive.KeyOf(joined.Serial, joined.Model, joined.SizeBytes, joined.Number);

    /// <summary>Builds a SMART reading from the raw ATA value and threshold sectors; null when they hold no attributes.</summary>
    public static SmartReading? FromAtaSectors(byte[]? values, byte[]? thresholds, string? model, DateTimeOffset at, string source)
    {
        if (values is null || !AtaCommands.LooksLikeSmartData(values))
        {
            return null;
        }

        var attributes = SmartBlobParser.Parse(values, thresholds ?? []);
        if (attributes.Count == 0)
        {
            return null;
        }

        var summary = SmartDecoder.Summarize(attributes, model);
        return new SmartReading(at, attributes, summary, summary.FailedPreFailAttributes.Count > 0, source);
    }

    /// <summary>Builds a SMART reading from an NVMe health log page; null when it cannot be parsed.</summary>
    public static SmartReading? FromNvmeLog(byte[]? log, DateTimeOffset at, string source)
    {
        if (log is null || SmartDecoder.ParseNvmeHealthLog(log) is not { } values)
        {
            return null;
        }

        return new SmartReading(at, SmartDecoder.NvmeRows(values), values, (values.NvmeCriticalWarning ?? 0) != 0, source);
    }

    private static int? BusCodeFromText(string? text) => text?.ToUpperInvariant() switch
    {
        "USB" => DriveMediaRules.BusUsb,
        "NVME" => DriveMediaRules.BusNvme,
        "RAID" => DriveMediaRules.BusRaid,
        "SATA" => 11,
        _ => null,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
