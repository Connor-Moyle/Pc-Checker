using System.Globalization;

namespace PcChecker.Core.Storage;

/// <summary>
/// The Good / Caution / Bad verdict per drive with the reason in one plain sentence. The rules follow what drive
/// makers and long-standing SMART tools agree on: any reallocated, pending or offline-uncorrectable sector is a caution
/// sign; a pre-fail attribute at or below its threshold, or the drive predicting its own failure, is bad. Pure.
/// </summary>
public static class DriveHealthRules
{
    public const int BadReallocated = 50;
    public const int BadPending = 10;
    public const int BadOfflineUncorrectable = 10;
    public const double CautionWear = 70;
    public const double BadWear = 90;
    public const long OldDriveHours = 50_000;

    /// <summary>Everything the verdict looks at.</summary>
    public sealed record Input(
        DriveMedia Media,
        DriveRole Role,
        SmartAccess Access,
        SmartReading? Reading,
        string? WindowsHealth,
        double WarnC = 55,
        double CritC = 65);

    public static DriveHealthVerdict Judge(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var findings = new List<string>();
        var verdict = DriveVerdict.Good;
        string? headline = null;

        void Raise(DriveVerdict level, string finding)
        {
            findings.Add(finding);
            if (level > verdict)
            {
                verdict = level;
                headline = finding;
            }
            else if (level == verdict && headline is null)
            {
                headline = finding;
            }
        }

        switch (input.WindowsHealth?.Trim().ToUpperInvariant())
        {
            case "UNHEALTHY":
                Raise(DriveVerdict.Bad, "Windows reports this drive as unhealthy.");
                break;
            case "WARNING":
                Raise(DriveVerdict.Caution, "Windows reports a warning for this drive.");
                break;
        }

        var reading = input.Reading;
        if (reading is null)
        {
            if (verdict != DriveVerdict.Good)
            {
                return new DriveHealthVerdict(verdict, headline!, findings);
            }

            if (input.Role == DriveRole.StorageSpace && input.WindowsHealth is { } spaceHealth && spaceHealth.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
            {
                return new DriveHealthVerdict(DriveVerdict.Good, "Windows reports this storage space as healthy. Each member drive is checked on its own.", []);
            }

            return DriveHealthVerdict.Unknown(NoReadingReason(input.Access));
        }

        var v = reading.Values;
        if (reading.PredictsFailure)
        {
            Raise(DriveVerdict.Bad, "The drive predicts its own failure.");
        }

        foreach (var attribute in v.FailedPreFailAttributes)
        {
            Raise(DriveVerdict.Bad, $"SMART attribute {attribute} is at or below the drive's failure threshold.");
        }

        foreach (var attribute in v.FailedAttributes.Except(v.FailedPreFailAttributes))
        {
            Raise(DriveVerdict.Caution, $"SMART attribute {attribute} is at or below its threshold.");
        }

        if (v.ReallocatedSectors is > 0 and var reallocated)
        {
            Raise(reallocated >= BadReallocated ? DriveVerdict.Bad : DriveVerdict.Caution,
                $"{N(reallocated)} reallocated sector{S(reallocated)}: the drive found bad spots and swapped in spares. Back up and watch whether this number grows.");
        }

        if (v.PendingSectors is > 0 and var pending)
        {
            Raise(pending >= BadPending ? DriveVerdict.Bad : DriveVerdict.Caution,
                $"{N(pending)} sector{S(pending)} could not be read and {(pending == 1 ? "is" : "are")} waiting to be reallocated.");
        }

        if (v.OfflineUncorrectable is > 0 and var offline)
        {
            Raise(offline >= BadOfflineUncorrectable ? DriveVerdict.Bad : DriveVerdict.Caution,
                $"{N(offline)} sector{S(offline)} could not be fixed during the drive's own surface check.");
        }

        if (v.SpinRetries is > 0 and var spin && input.Media != DriveMedia.Ssd)
        {
            Raise(DriveVerdict.Caution, $"The motor needed {N(spin)} retr{(spin == 1 ? "y" : "ies")} to spin up; the drive's motor or power supply may be weak.");
        }

        if (v.ReportedUncorrectable is > 0 and var reported)
        {
            Raise(DriveVerdict.Caution, $"The drive reported {N(reported)} error{S(reported)} it could not correct.");
        }

        if (v.CrcErrors is > 0 and var crc)
        {
            Raise(DriveVerdict.Caution, $"{N(crc)} data transfer error{S(crc)} (CRC) {(crc == 1 ? "was" : "were")} logged. This is usually the SATA cable or port, not the disk itself: reseat or replace the cable.");
        }

        if (v.NvmeCriticalWarning is > 0 and var warning)
        {
            Raise(DriveVerdict.Bad, "The SSD raised a critical warning: " + DescribeNvmeWarning(warning) + ".");
        }

        if (v.AvailableSparePercent is { } spare && v.AvailableSpareThreshold is { } spareThreshold && spareThreshold > 0 && spare <= spareThreshold)
        {
            Raise(DriveVerdict.Bad, $"The SSD has used up its spare blocks ({spare}% left).");
        }

        if (v.MediaErrors is > 0 and var media)
        {
            Raise(DriveVerdict.Caution, $"The SSD logged {N(media)} media error{S(media)}.");
        }

        if (v.WearPercent is { } wear)
        {
            if (wear >= BadWear)
            {
                Raise(DriveVerdict.Bad, $"The SSD has used about {wear:0}% of its rated write life.");
            }
            else if (wear >= CautionWear)
            {
                Raise(DriveVerdict.Caution, $"The SSD has used about {wear:0}% of its rated write life.");
            }
        }

        if (v.TemperatureC is { } temp)
        {
            if (temp >= input.CritC)
            {
                Raise(DriveVerdict.Bad, $"The drive is very hot ({temp:0} °C).");
            }
            else if (temp >= input.WarnC)
            {
                Raise(DriveVerdict.Caution, $"The drive is running warm ({temp:0} °C).");
            }
        }

        if (input.Media == DriveMedia.Hdd && v.PowerOnHours is >= OldDriveHours and var hours)
        {
            Raise(DriveVerdict.Caution, $"The drive has run for {N(hours)} hours (over {OldDriveHours / 8766} years). Keep backups current.");
        }

        if (verdict == DriveVerdict.Good)
        {
            return new DriveHealthVerdict(DriveVerdict.Good, "SMART reports no problems.", findings);
        }

        return new DriveHealthVerdict(verdict, headline ?? findings[0], findings);
    }

    /// <summary>Why there is no verdict, in plain words.</summary>
    public static string NoReadingReason(SmartAccess access) => access switch
    {
        SmartAccess.SkippedAsleep => "The drive is asleep and has not been read yet. It is not woken for routine checks.",
        SmartAccess.BlockedByEnclosure => "The USB enclosure does not pass SMART data through, so the drive's health cannot be read. Connect the drive inside the PC for a full check.",
        SmartAccess.HiddenByRaid => "This is a RAID volume. SMART belongs to each member drive and the RAID driver may hide it; use the RAID software (Intel RST or AMD RAIDXpert) to check the members.",
        SmartAccess.NotApplicable => "A storage space has no SMART of its own. Its member drives are checked one by one.",
        SmartAccess.NeedsAdmin => "Reading SMART needs administrator rights. Restart PC Checker as administrator.",
        SmartAccess.NotSupported => "The drive did not report SMART data.",
        _ => "Not checked yet.",
    };

    /// <summary>Decodes the NVMe critical warning bits.</summary>
    public static string DescribeNvmeWarning(int bits)
    {
        var parts = new List<string>();
        if ((bits & 0x01) != 0)
        {
            parts.Add("spare space is low");
        }

        if ((bits & 0x02) != 0)
        {
            parts.Add("temperature is outside its limits");
        }

        if ((bits & 0x04) != 0)
        {
            parts.Add("reliability is degraded by media errors");
        }

        if ((bits & 0x08) != 0)
        {
            parts.Add("the drive has switched to read-only");
        }

        if ((bits & 0x10) != 0)
        {
            parts.Add("the backup power has failed");
        }

        return parts.Count == 0 ? "unknown warning" : string.Join(", ", parts);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string S(long value) => value == 1 ? string.Empty : "s";
}

/// <summary>When a drive may be read, and how its sleeping state is described. Pure.</summary>
public static class DriveSleepRules
{
    /// <summary>
    /// A routine refresh never sends a command to a drive that is asleep: the SMART read would spin a hard disk back up
    /// (and wear it with an extra start/stop cycle). Only an explicit "Read now" request wakes it.
    /// </summary>
    public static bool ShouldReadSmart(DrivePowerState power, DriveReadMode mode) =>
        mode == DriveReadMode.WakeIfAsleep || power != DrivePowerState.Asleep;

    /// <summary>When the last reading of a sleeping drive was taken, relative to today.</summary>
    public enum ReadingDay
    {
        None,
        Today,
        Yesterday,
        Earlier,
    }

    /// <summary>Which wording <see cref="Describe"/> uses, so the app can build the same sentence from translated text.</summary>
    public static ReadingDay DayOf(DateTimeOffset? lastReading, DateTimeOffset now)
    {
        if (lastReading is not { } at)
        {
            return ReadingDay.None;
        }

        var local = at.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        return local == today ? ReadingDay.Today : local == today.AddDays(-1) ? ReadingDay.Yesterday : ReadingDay.Earlier;
    }

    /// <summary>"Asleep, last reading at 14:05", "Asleep, last reading on 3 Oct at 09:12" or "Asleep, not read yet".</summary>
    public static string Describe(DrivePowerState power, DateTimeOffset? lastReading, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        switch (power)
        {
            case DrivePowerState.Active:
                return "Active";
            case DrivePowerState.Asleep:
                if (lastReading is not { } at)
                {
                    return "Asleep, not read yet";
                }

                var local = at.ToLocalTime();
                var today = now.ToLocalTime().Date;
                string time = local.ToString("t", culture);
                if (local.Date == today)
                {
                    return $"Asleep, last reading at {time}";
                }

                if (local.Date == today.AddDays(-1))
                {
                    return $"Asleep, last reading yesterday at {time}";
                }

                return $"Asleep, last reading on {local.ToString("d", culture)} at {time}";
            default:
                return "Not reported";
        }
    }
}
