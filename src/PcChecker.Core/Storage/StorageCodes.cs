using System.Text.RegularExpressions;
using PcChecker.Core.Common;

namespace PcChecker.Core.Storage;

/// <summary>Maps the numeric codes that MSFT_PhysicalDisk reports to readable text. Pure, so it is unit-testable.</summary>
public static class StorageCodes
{
    /// <summary>MSFT_PhysicalDisk.MediaType → "HDD" / "SSD" / "SCM" / "Unknown".</summary>
    public static string MediaType(int? code) => code switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unknown",
    };

    /// <summary>Win32_DiskDrive.MediaType text ("Fixed hard disk media", "External hard disk media", "Removable Media") → short label.</summary>
    public static string MediaTypeFromText(string? text, string? busType)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Equals(busType, "NVMe", StringComparison.OrdinalIgnoreCase) ? "SSD" : "Unknown";
        }

        if (text.Contains("Removable", StringComparison.OrdinalIgnoreCase))
        {
            return "Removable";
        }

        return string.Equals(busType, "NVMe", StringComparison.OrdinalIgnoreCase) ? "SSD" : "Unknown";
    }

    /// <summary>MSFT_PhysicalDisk.BusType → text.</summary>
    public static string BusType(int? code) => code switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };

    /// <summary>Win32_DiskDrive.InterfaceType ("IDE", "SCSI", "USB", "HDC", "1394") → normalised text.</summary>
    public static string BusTypeFromInterface(string? interfaceType, string? pnpDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(pnpDeviceId))
        {
            if (pnpDeviceId.StartsWith("SCSI\\DISK&VEN_NVME", StringComparison.OrdinalIgnoreCase) || pnpDeviceId.Contains("NVME", StringComparison.OrdinalIgnoreCase))
            {
                return "NVMe";
            }

            if (pnpDeviceId.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase))
            {
                return "USB";
            }
        }

        return interfaceType?.ToUpperInvariant() switch
        {
            "IDE" => "SATA",
            "SCSI" => "SATA",
            "USB" => "USB",
            "1394" => "1394",
            "HDC" => "SATA",
            null or "" => "Unknown",
            _ => interfaceType,
        };
    }

    /// <summary>MSFT_PhysicalDisk.HealthStatus → <see cref="HealthStatus"/>.</summary>
    public static HealthStatus Health(int? code) => code switch
    {
        0 => HealthStatus.Good,
        1 => HealthStatus.Warning,
        2 => HealthStatus.Critical,
        _ => HealthStatus.Unknown,
    };

    /// <summary>Win32_DiskDrive.Status ("OK", "Pred Fail", "Error", "Degraded") → <see cref="HealthStatus"/>.</summary>
    public static HealthStatus HealthFromStatusText(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "OK" => HealthStatus.Good,
        "PRED FAIL" or "DEGRADED" or "STRESSED" => HealthStatus.Warning,
        "ERROR" or "NONRECOVER" or "LOST COMM" => HealthStatus.Critical,
        _ => HealthStatus.Unknown,
    };

    /// <summary>Normalises "C", "c:", "C:\" to "C:"; null when the input is not a drive letter.</summary>
    public static string? NormalizeDriveLetter(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var t = input.Trim();
        if (t.Length == 0 || t.Length > 3 || !char.IsLetter(t[0]))
        {
            return null;
        }

        if (t.Length > 1 && t[1] != ':')
        {
            return null;
        }

        if (t.Length > 2 && t[2] != '\\' && t[2] != '/')
        {
            return null;
        }

        return char.ToUpperInvariant(t[0]) + ":";
    }

    /// <summary>True when the volume list contains the drive Windows is installed on.</summary>
    public static bool ContainsSystemDrive(IEnumerable<string> volumes, string? systemRoot)
    {
        var sys = NormalizeDriveLetter(systemRoot);
        if (sys is null)
        {
            return false;
        }

        return volumes.Any(v => NormalizeDriveLetter(v) == sys);
    }

    /// <summary>True when <paramref name="driveLetter"/> is the volume Windows is installed on (chkdsk cannot dismount it, so repairs are scheduled for the next restart).</summary>
    public static bool IsSystemVolume(string? driveLetter, string? systemRoot)
    {
        var letter = NormalizeDriveLetter(driveLetter);
        var sys = NormalizeDriveLetter(systemRoot);
        return letter is not null && sys is not null && letter == sys;
    }
}

/// <summary>Parses the text that chkdsk and Repair-Volume print, so the result can be explained in plain words.</summary>
public static partial class StorageCommandParsers
{
    [GeneratedRegex(@"next time the system restarts|scheduled to be checked|will be checked the next time", RegexOptions.IgnoreCase)]
    private static partial Regex RebootScheduledPattern();

    [GeneratedRegex(@"found no problems|no further action is required|has verified the file system|no problems were found", RegexOptions.IgnoreCase)]
    private static partial Regex NoProblemsPattern();

    [GeneratedRegex(@"errors found|corrupt|unreadable|bad sectors|found problems|made corrections|repaired|cannot continue in read-only mode|is in use by another process", RegexOptions.IgnoreCase)]
    private static partial Regex ProblemPattern();

    [GeneratedRegex(@"(\d[\d,.]*)\s+KB in bad sectors", RegexOptions.IgnoreCase)]
    private static partial Regex BadSectorsPattern();

    [GeneratedRegex(@"cannot run|cannot open|cannot lock|is not available for|unable to determine|access denied", RegexOptions.IgnoreCase)]
    private static partial Regex CouldNotCheckPattern();

    // The answer letters chkdsk shows in its prompts: "(Y/N)", "(J/N)", "(O/N)", "(S/N)", "[Y(да)/N(нет)]".
    [GeneratedRegex(@"[\(\[]\s*(\p{L})(?:\s*\([^()/]*\))?\s*/\s*(\p{L})(?:\s*\([^()/]*\))?\s*[\)\]]")]
    private static partial Regex YesNoPromptPattern();

    /// <summary>Summary of a chkdsk run derived from its output.</summary>
    public sealed record ChkdskOutcome(bool RebootScheduled, bool ProblemsFound, bool NoProblems, long? BadSectorKb, string Summary);

    /// <summary>Reads the chkdsk output and the exit code (0 ok, 1 fixed, 2 cleanup, 3 could not check or not fixed) into a verdict.</summary>
    public static ChkdskOutcome ParseChkdsk(string? output, int exitCode, string driveLetter) => ParseChkdsk(output, exitCode, driveLetter, scheduledHint: null);

    /// <summary>
    /// True when a chkdsk run counts as having done its job: 0 (no errors), 1 (errors fixed), 2 (cleanup done, or
    /// skipped without /f), a read-only scan that found problems (3 means "not fixed because /f was not specified"),
    /// or a repair that was scheduled for the next restart.
    /// </summary>
    public static bool IsChkdskSuccess(int exitCode, bool scanOnly, ChkdskOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return exitCode is 0 or 1 or 2 || outcome.RebootScheduled || (scanOnly && exitCode == 3 && outcome.ProblemsFound);
    }

    /// <summary>
    /// As above, with a locale-independent hint from <see cref="VolumeCheckState"/>: chkdsk prints in the user's
    /// language, so when the system says a check is scheduled (BootExecute entry or dirty bit) that wins over the
    /// English text patterns and the "no problems" fallback on exit code 0.
    /// </summary>
    public static ChkdskOutcome ParseChkdsk(string? output, int exitCode, string driveLetter, bool? scheduledHint) =>
        ParseChkdsk(output, exitCode, driveLetter, scheduledHint, scanOnly: false);

    /// <summary>
    /// As above for a run of the given kind. For a read-only <c>/scan</c>, exit code 3 means chkdsk found problems it
    /// did not fix because /f was not given (the message is localized, so the exit code is the reliable signal); only
    /// output that says the check could not run is reported as "could not check".
    /// </summary>
    public static ChkdskOutcome ParseChkdsk(string? output, int exitCode, string driveLetter, bool? scheduledHint, bool scanOnly)
    {
        output ??= string.Empty;
        bool reboot = RebootScheduledPattern().IsMatch(output) || scheduledHint == true;
        bool noProblems = NoProblemsPattern().IsMatch(output);
        bool problems = ProblemPattern().IsMatch(output) && !noProblems;
        long? badKb = null;
        var m = BadSectorsPattern().Match(output);
        if (m.Success && long.TryParse(m.Groups[1].Value.Replace(",", string.Empty).Replace(".", string.Empty), out var kb))
        {
            badKb = kb;
            if (kb > 0)
            {
                problems = true;
            }
        }

        bool scanFoundProblems = scanOnly && exitCode == 3 && !reboot && !CouldNotCheckPattern().IsMatch(output);
        if (scanFoundProblems)
        {
            problems = true;
            noProblems = false;
        }

        string summary;
        if (reboot)
        {
            summary = $"A full check and repair of {driveLetter} will run the next time Windows starts. Restart when convenient and allow it to finish (it can take a while).";
        }
        else if (scanFoundProblems && badKb is not > 0)
        {
            summary = $"The scan of {driveLetter} found file system problems that it did not fix. Run Spot fix, or schedule the full repair at the next restart.";
        }
        else if (exitCode == 3 && !scanFoundProblems)
        {
            summary = $"Windows could not check {driveLetter} right now (it may be in use). Try again or schedule the check at the next restart.";
        }
        else if (badKb is > 0)
        {
            summary = $"Check of {driveLetter} finished: {badKb:N0} KB in bad sectors. The drive is losing sectors; back it up and plan a replacement.";
        }
        else if (problems || exitCode == 1)
        {
            summary = exitCode == 1
                ? $"Check of {driveLetter} found and corrected file system problems."
                : $"Check of {driveLetter} found problems. Schedule the full repair at the next restart.";
        }
        else if (noProblems || exitCode is 0 or 2)
        {
            summary = $"Windows found no file system problems on {driveLetter}.";
        }
        else
        {
            summary = $"Check of {driveLetter} finished with exit code {exitCode}. Review the output for details.";
        }

        return new ChkdskOutcome(reboot, problems, noProblems || ((exitCode is 0 or 2) && !problems && !reboot), badKb, summary);
    }

    /// <summary>Turns the Repair-Volume status word into a sentence.</summary>
    public static string DescribeRepairVolumeStatus(string? status, RepairMode mode, string driveLetter)
    {
        var s = (status ?? string.Empty).Trim();
        return s.ToUpperInvariant() switch
        {
            "NOERRORSFOUND" => $"Repair-Volume ({mode}) on {driveLetter}: no corruption found.",
            "ERRORSFOUND" => $"Repair-Volume ({mode}) on {driveLetter}: corruption found. Run Spot fix, or schedule an offline scan and fix.",
            "ERRORSFIXED" => $"Repair-Volume ({mode}) on {driveLetter}: corruption was found and fixed.",
            "ERRORSNOTFIXED" => $"Repair-Volume ({mode}) on {driveLetter}: corruption found but could not be fixed online. Schedule an offline scan and fix.",
            "SCANNEEDED" => $"Repair-Volume ({mode}) on {driveLetter}: Windows recommends a scan.",
            "SPOTFIXNEEDED" => $"Repair-Volume ({mode}) on {driveLetter}: a Spot fix is needed.",
            "FULLREPAIRNEEDED" => $"Repair-Volume ({mode}) on {driveLetter}: a full offline repair is needed. Schedule it at the next restart.",
            "" => $"Repair-Volume ({mode}) on {driveLetter} finished.",
            _ => $"Repair-Volume ({mode}) on {driveLetter}: {s}.",
        };
    }

    /// <summary>Repair-Volume switch for a mode.</summary>
    public static string RepairVolumeSwitch(RepairMode mode) => mode switch
    {
        RepairMode.SpotFix => "-SpotFix",
        RepairMode.OfflineScanAndFix => "-OfflineScanAndFix",
        _ => "-Scan",
    };

    /// <summary>
    /// Optimize-Volume switch for a media type: TRIM for solid state, defragment for spinning disks. When the media type
    /// is not known (the Win32_DiskDrive fallback cannot tell an SSD from a hard disk) the switch is left out so
    /// Optimize-Volume picks the right operation itself instead of failing with -ReTrim on a hard disk.
    /// </summary>
    public static string OptimizeSwitch(string? mediaType)
    {
        if (string.Equals(mediaType, "HDD", StringComparison.OrdinalIgnoreCase))
        {
            return "-Defrag";
        }

        if (string.Equals(mediaType, "SSD", StringComparison.OrdinalIgnoreCase) || string.Equals(mediaType, "SCM", StringComparison.OrdinalIgnoreCase))
        {
            return "-ReTrim";
        }

        return string.Empty;
    }

    /// <summary>
    /// Lines fed to chkdsk's standard input so it never waits on a prompt. chkdsk asks different questions depending on
    /// the volume: on the Windows volume it can only offer "schedule at next restart?" (answer Y). On any other volume
    /// that is in use it first asks "force a dismount?" — answering Y there would invalidate every open file and run the
    /// repair immediately, so the scheduled repair answers N and then Y to the "schedule at next restart?" question that
    /// follows. A spot fix is meant to take the volume offline for a few seconds, so it answers Y everywhere.
    /// </summary>
    public static string ChkdskAnswers(bool scheduleOfflineFix, bool isSystemVolume) =>
        ChkdskAnswers(scheduleOfflineFix, isSystemVolume, 'Y', 'N');

    /// <summary>As above with the answer letters chkdsk uses in the Windows display language (J/N in German, O/N in French, S/N in Spanish…).</summary>
    public static string ChkdskAnswers(bool scheduleOfflineFix, bool isSystemVolume, char yes, char no) =>
        scheduleOfflineFix && !isSystemVolume ? $"{no}\r\n{yes}\r\n" : $"{yes}\r\n";

    /// <summary>Finds the yes and no letters in a chkdsk prompt such as "(Y/N)", "(J/N)" or "[Y(да)/N(нет)]".</summary>
    public static bool TryParsePromptLetters(string? output, out char yes, out char no)
    {
        yes = 'Y';
        no = 'N';
        if (string.IsNullOrEmpty(output))
        {
            return false;
        }

        var m = YesNoPromptPattern().Match(output);
        if (!m.Success)
        {
            return false;
        }

        yes = char.ToUpperInvariant(m.Groups[1].Value[0]);
        no = char.ToUpperInvariant(m.Groups[2].Value[0]);
        return yes != no;
    }

    /// <summary>
    /// chkdsk compares an answer with the "yes" letter of the Windows display language, so "Y" counts as "no" on German,
    /// French, Spanish, Italian or Portuguese Windows and nothing is scheduled or fixed. When the output of a run shows a
    /// prompt with other letters than Y/N, this returns the same answers in those letters for a second run; otherwise null.
    /// Any answer that is not the localized "yes" is read as "no", so the first run never dismounted or changed anything.
    /// </summary>
    public static string? LocalizedChkdskAnswers(string? output, bool scheduleOfflineFix, bool isSystemVolume)
    {
        if (!TryParsePromptLetters(output, out var yes, out var no) || (yes == 'Y' && no == 'N'))
        {
            return null;
        }

        return ChkdskAnswers(scheduleOfflineFix, isSystemVolume, yes, no);
    }

    /// <summary>Builds the chkdsk arguments for the requested operation.</summary>
    public static string ChkdskArguments(string driveLetter, bool scanOnly, bool scheduleOfflineFix)
    {
        if (scanOnly)
        {
            return $"{driveLetter} /scan";
        }

        return scheduleOfflineFix ? $"{driveLetter} /f /r" : $"{driveLetter} /spotfix";
    }
}
