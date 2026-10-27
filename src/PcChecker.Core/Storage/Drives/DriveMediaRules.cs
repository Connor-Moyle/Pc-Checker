using System.Globalization;

namespace PcChecker.Core.Storage;

/// <summary>
/// Decides whether a drive is a hard disk, an SSD, storage class memory or a memory card, and its spindle speed.
/// Pure, so the decision table is unit-tested.
/// </summary>
public static class DriveMediaRules
{
    /// <summary>MSFT_PhysicalDisk.SpindleSpeed for "not known".</summary>
    public const long SpindleUnknown = 0xFFFFFFFF;

    public const int BusUsb = 7;
    public const int BusRaid = 8;
    public const int BusSd = 12;
    public const int BusMmc = 13;
    public const int BusSpaces = 16;
    public const int BusNvme = 17;
    public const int BusScm = 18;

    /// <summary>The media decision and where it came from.</summary>
    public sealed record Decision(DriveMedia Media, int? Rpm, string Basis);

    /// <summary>
    /// Order of evidence: MSFT_PhysicalDisk.MediaType (3 HDD, 4 SSD, 5 SCM); for 0 (Unspecified) the bus (NVMe is
    /// always solid state, SD/MMC is a memory card), then the seek penalty Windows measured (none = SSD), then the spindle
    /// speed (0 = SSD, 0xFFFFFFFF = unknown, anything else is a hard disk's RPM), then the ATA nominal rotation rate
    /// (1 = SSD), and finally the model name.
    /// </summary>
    public static Decision Decide(int? mediaType, int? busType, bool? seekPenalty, long? spindleSpeed, string? model, int? ataRotationRate = null)
    {
        int? rpm = RpmFrom(spindleSpeed) ?? RpmFromAta(ataRotationRate);
        switch (mediaType)
        {
            case 3:
                return new Decision(DriveMedia.Hdd, rpm, "Windows media type");
            case 4:
                return new Decision(DriveMedia.Ssd, null, "Windows media type");
            case 5:
                return new Decision(DriveMedia.Scm, null, "Windows media type");
        }

        if (busType == BusNvme)
        {
            return new Decision(DriveMedia.Ssd, null, "NVMe bus");
        }

        if (busType == BusScm)
        {
            return new Decision(DriveMedia.Scm, null, "SCM bus");
        }

        if (busType is BusSd or BusMmc)
        {
            return new Decision(DriveMedia.FlashCard, null, "Memory card bus");
        }

        if (seekPenalty is { } penalty)
        {
            return penalty ? new Decision(DriveMedia.Hdd, rpm, "Seek penalty") : new Decision(DriveMedia.Ssd, null, "No seek penalty");
        }

        if (spindleSpeed is 0)
        {
            return new Decision(DriveMedia.Ssd, null, "Spindle speed 0");
        }

        if (RpmFrom(spindleSpeed) is { } spinning)
        {
            return new Decision(DriveMedia.Hdd, spinning, "Spindle speed");
        }

        if (ataRotationRate is 1)
        {
            return new Decision(DriveMedia.Ssd, null, "ATA rotation rate");
        }

        if (RpmFromAta(ataRotationRate) is { } ataRpm)
        {
            return new Decision(DriveMedia.Hdd, ataRpm, "ATA rotation rate");
        }

        var m = model ?? string.Empty;
        if (m.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || m.Contains("SSD", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Solid State", StringComparison.OrdinalIgnoreCase) || m.Contains("Optane", StringComparison.OrdinalIgnoreCase))
        {
            return new Decision(DriveMedia.Ssd, null, "Model name");
        }

        return new Decision(DriveMedia.Unknown, rpm, "Not reported");
    }

    /// <summary>A believable spindle speed (MSFT_PhysicalDisk.SpindleSpeed), else null.</summary>
    public static int? RpmFrom(long? spindleSpeed) =>
        spindleSpeed is { } s && s != SpindleUnknown && s is >= 1000 and <= 20000 ? (int)s : null;

    /// <summary>ATA IDENTIFY word 217: 1 = non-rotating, 0x0401–0xFFFE = RPM.</summary>
    public static int? RpmFromAta(int? word217) => word217 is >= 0x0401 and <= 0xFFFE and <= 20000 ? word217 : null;

    /// <summary>"HDD", "SSD", "SCM", "Memory card" or "Unknown".</summary>
    public static string Label(DriveMedia media) => media switch
    {
        DriveMedia.Hdd => "HDD",
        DriveMedia.Ssd => "SSD",
        DriveMedia.Scm => "SCM",
        DriveMedia.FlashCard => "Memory card",
        _ => "Unknown",
    };

    /// <summary>The long name for the details list: "Hard disk (7,200 RPM)", "Solid state drive"…</summary>
    public static string Describe(DriveMedia media, int? rpm) => media switch
    {
        DriveMedia.Hdd => rpm is { } r ? $"Hard disk ({r.ToString("N0", CultureInfo.InvariantCulture)} RPM)" : "Hard disk",
        DriveMedia.Ssd => "Solid state drive",
        DriveMedia.Scm => "Storage class memory",
        DriveMedia.FlashCard => "Memory card",
        _ => "Not reported",
    };

    /// <summary>MSFT_PhysicalDisk / MSFT_Disk BusType code → text.</summary>
    public static string BusName(int? code) => StorageCodes.BusType(code);
}
