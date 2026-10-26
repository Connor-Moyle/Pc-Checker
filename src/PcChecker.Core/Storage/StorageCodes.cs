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

    /// <summary>Summary of a chkdsk run derived from its output.</summary>
    public sealed record ChkdskOutcome(bool RebootScheduled, bool ProblemsFound, bool NoProblems, long? BadSectorKb, string Summary);

    /// <summary>Reads the chkdsk output and the exit code (0 ok, 1 fixed, 2 cleanup, 3 could not check) into a verdict.</summary>
    public static ChkdskOutcome ParseChkdsk(string? output, int exitCode, string driveLetter)
    {
        output ??= string.Empty;
        bool reboot = RebootScheduledPattern().IsMatch(output);
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

        string summary;
        if (reboot)
        {
            summary = $"A full check and repair of {driveLetter} will run the next time Windows starts. Restart when convenient and allow it to finish (it can take a while).";
        }
        else if (exitCode == 3)
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
        else if (noProblems || exitCode == 0)
        {
            summary = $"Windows found no file system problems on {driveLetter}.";
        }
        else
        {
            summary = $"Check of {driveLetter} finished with exit code {exitCode}. Review the output for details.";
        }

        return new ChkdskOutcome(reboot, problems, noProblems || (exitCode == 0 && !problems), badKb, summary);
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

    /// <summary>Optimize-Volume switch for a media type: TRIM for solid state, defragment for spinning disks.</summary>
    public static string OptimizeSwitch(string? mediaType) =>
        string.Equals(mediaType, "HDD", StringComparison.OrdinalIgnoreCase) ? "-Defrag" : "-ReTrim";

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
