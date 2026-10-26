using System.Text.RegularExpressions;

namespace PcChecker.Core.Optimization;

/// <summary>What <c>sfc /scannow</c> found.</summary>
public enum SfcOutcome
{
    /// <summary>The verdict could not be read.</summary>
    Unknown,

    /// <summary>No integrity violations.</summary>
    NoViolations,

    /// <summary>Damaged files were found and all of them were repaired.</summary>
    Repaired,

    /// <summary>Damaged files were found; whether every one was repaired could not be confirmed.</summary>
    ViolationsFound,

    /// <summary>Damaged files were found and some could not be repaired.</summary>
    CouldNotRepair,
}

/// <summary>
/// Reads the verdict of <c>sfc /scannow</c>. Pure. sfc prints its console summary in the Windows display language, so
/// the English sentences are only one source: the <c>[SR]</c> lines sfc appends to <c>CBS.log</c> are always English and
/// decide when the console text is not understood.
/// </summary>
public static partial class SfcResultParser
{
    [GeneratedRegex(@"^Repairing (\d+) components", RegexOptions.IgnoreCase)]
    private static partial Regex RepairingComponents();

    /// <summary>Console text first, then the CBS.log lines written during the scan.</summary>
    public static SfcOutcome Interpret(string? consoleText, IEnumerable<string>? cbsLogLines)
    {
        var outcome = FromConsole(consoleText);
        return outcome == SfcOutcome.Unknown ? FromCbsLog(cbsLogLines) : outcome;
    }

    /// <summary>The English console verdicts; Unknown for anything else (including localized text).</summary>
    public static SfcOutcome FromConsole(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SfcOutcome.Unknown;
        }

        if (text.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase))
        {
            return SfcOutcome.NoViolations;
        }

        if (text.Contains("unable to fix", StringComparison.OrdinalIgnoreCase))
        {
            return SfcOutcome.CouldNotRepair;
        }

        if (text.Contains("successfully repaired", StringComparison.OrdinalIgnoreCase))
        {
            return SfcOutcome.Repaired;
        }

        return SfcOutcome.Unknown;
    }

    /// <summary>
    /// Verdict from the <c>[SR]</c> entries sfc wrote to CBS.log: "Verify complete" batches with no repair entries mean
    /// a clean scan; "Cannot repair member file" marks a damaged file and "Repaired file" / "Repairing corrupted file"
    /// a repair of one.
    /// </summary>
    public static SfcOutcome FromCbsLog(IEnumerable<string>? lines)
    {
        if (lines is null)
        {
            return SfcOutcome.Unknown;
        }

        bool verified = false;
        int damaged = 0, repaired = 0;
        foreach (var raw in lines)
        {
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            int marker = raw.IndexOf("[SR]", StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var line = raw[(marker + 4)..].Trim();
            if (line.StartsWith("Cannot repair member file", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Could not reproject corrupted file", StringComparison.OrdinalIgnoreCase))
            {
                damaged++;
            }
            else if (line.StartsWith("Repaired file", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("Repairing corrupted file", StringComparison.OrdinalIgnoreCase))
            {
                repaired++;
            }
            else if (RepairingComponents().Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int n) && n > 0)
            {
                repaired += n;
            }
            else if (line.StartsWith("Verify complete", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("Verifying ", StringComparison.OrdinalIgnoreCase))
            {
                verified = true;
            }
        }

        if (damaged == 0 && repaired == 0)
        {
            return verified ? SfcOutcome.NoViolations : SfcOutcome.Unknown;
        }

        if (damaged == 0 || repaired >= damaged)
        {
            return SfcOutcome.Repaired;
        }

        return SfcOutcome.ViolationsFound;
    }
}
