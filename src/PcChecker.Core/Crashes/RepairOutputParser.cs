using System.Globalization;
using System.Text.RegularExpressions;
using PcChecker.Core.Optimization;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>Which repair step a progress line belongs to.</summary>
public enum RepairStep
{
    Dism,
    Sfc,
}

/// <summary>
/// Reads the live output of "DISM /Online /Cleanup-Image /RestoreHealth" and "sfc /scannow" without depending on the
/// Windows language: only the number next to a percent sign is used ("[==== 62.3% ====]", "Verification 45%
/// complete.", "Vérification à 45 % effectuée", Turkish "%45"). The verdict of sfc comes from
/// <see cref="SfcResultParser"/> (English console text or the always-English CBS.log), the verdict of DISM from its
/// exit code. Pure.
/// </summary>
public static partial class RepairOutputParser
{
    // A number (with . or , decimals) directly before or after a percent sign, with an optional space.
    [GeneratedRegex(@"(?<!\d)(\d{1,3}(?:[.,]\d+)?)\s?%|%\s?(\d{1,3}(?:[.,]\d+)?)(?![\d.,])", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPattern();

    /// <summary>The percentage in a line (0–100), or null when there is none.</summary>
    public static double? Percent(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        double? last = null;
        foreach (Match m in PercentPattern().Matches(line))
        {
            var text = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Replace(',', '.');
            if (double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 100)
            {
                last = v;
            }
        }

        return last;
    }

    /// <summary>
    /// The overall progress (0–100) of the two-step repair: DISM is the first half, sfc the second.
    /// </summary>
    public static double Overall(RepairStep step, double stepPercent) =>
        Math.Clamp((step == RepairStep.Dism ? 0 : 50) + (Math.Clamp(stepPercent, 0, 100) / 2), 0, 100);

    /// <summary>The plain-words summary of both steps (English keys).</summary>
    public static CrashLine Summary(bool dismSucceeded, SfcOutcome sfc, bool sfcRan)
    {
        if (!sfcRan)
        {
            return CrashLine.Of(dismSucceeded
                ? T("The Windows image was checked and is healthy or repaired, but the file check did not run.")
                : T("The Windows image repair (DISM) did not finish, so the file check was not run. Check the internet connection and try again."));
        }

        string dism = dismSucceeded ? T("The Windows image is healthy or was repaired.") : T("The Windows image repair (DISM) reported a problem.");
        string files = sfc switch
        {
            SfcOutcome.NoViolations => T("No damaged Windows files were found."),
            SfcOutcome.Repaired => T("Damaged Windows files were found and repaired. Restart the PC to finish."),
            SfcOutcome.ViolationsFound => T("Damaged Windows files were found and repaired, but check the log in case some could not be fixed. Restart the PC to finish."),
            SfcOutcome.CouldNotRepair => T("Some damaged Windows files could not be repaired. An in-place upgrade (reinstalling Windows over itself, keeping files) fixes that."),
            _ => T("The file check finished; its result could not be read (it is written in the Windows display language)."),
        };
        return new CrashLine("{0} {1}", new LocalizedText(dism), new LocalizedText(files));
    }
}
