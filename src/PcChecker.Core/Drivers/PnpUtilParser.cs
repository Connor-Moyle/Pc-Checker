using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Drivers;

/// <summary>Pure parsers for <c>pnputil.exe</c> output (driver store listing, export summary, exit codes).</summary>
public static partial class PnpUtilParser
{
    /// <summary>Exit code pnputil returns when a reboot is needed to finish (ERROR_SUCCESS_REBOOT_REQUIRED).</summary>
    public const int RebootRequiredExitCode = 3010;

    /// <summary>Exit code pnputil returns when a reboot was started (ERROR_SUCCESS_REBOOT_INITIATED).</summary>
    public const int RebootInitiatedExitCode = 1641;

    /// <summary>Exit code pnputil returns when nothing matched (ERROR_NO_MORE_ITEMS); not a failure.</summary>
    public const int NoMoreItemsExitCode = 259;

    [GeneratedRegex(@"^\s*([A-Za-z][A-Za-z ]*?)\s*:\s*(.*?)\s*$")]
    private static partial Regex KeyValueLine();

    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex TrailingNumber();

    /// <summary>Unambiguous ISO-style formats, tried before anything culture-dependent.</summary>
    private static readonly string[] IsoDateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd"];

    /// <summary>Last-resort formats when the machine's culture did not recognise the text.</summary>
    private static readonly string[] DateFormats =
    [
        "MM/dd/yyyy", "M/d/yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd-MM-yyyy", "d-M-yyyy",
    ];

    /// <summary>
    /// Parses the output of <c>pnputil /enum-drivers</c>: blank-line separated blocks of "Key: Value" lines.
    /// Unknown keys are ignored; blocks without a published name are skipped.
    /// </summary>
    public static IReadOnlyList<DriverPackage> ParseEnumDrivers(string? output)
    {
        var packages = new List<DriverPackage>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return packages;
        }

        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            var m = KeyValueLine().Match(line);
            if (m.Success)
            {
                block[Normalize(m.Groups[1].Value)] = m.Groups[2].Value.Trim();
            }
        }

        Flush();
        return packages;

        void Flush()
        {
            if (block.TryGetValue("publishedname", out var published) && !string.IsNullOrWhiteSpace(published))
            {
                var (date, version) = SplitVersionField(block.GetValueOrDefault("driverversion"));
                packages.Add(new DriverPackage(
                    published,
                    block.GetValueOrDefault("originalname"),
                    block.GetValueOrDefault("providername"),
                    block.GetValueOrDefault("classname"),
                    block.GetValueOrDefault("classguid"),
                    version,
                    date,
                    block.GetValueOrDefault("signername")));
            }

            block.Clear();
        }
    }

    /// <summary>Splits pnputil's "Driver Version" field ("03/22/2023 1.4.1.0") into a date and a version.</summary>
    public static (DateTime? Date, string? Version) SplitVersionField(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return (null, null);
        }

        var parts = field.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            return TryParseDate(parts[0], out var only) ? (only, null) : (null, parts[0]);
        }

        if (TryParseDate(parts[0], out var date))
        {
            return (date, parts[1]);
        }

        if (TryParseDate(parts[1], out var date2))
        {
            return (date2, parts[0]);
        }

        return (null, field.Trim());
    }

    /// <summary>
    /// Tolerant date parse for the formats pnputil emits. pnputil prints the driver date in the OS locale, so the
    /// machine's own short date pattern decides first (03/04/2023 is 3 April on an en-GB PC); ISO dates are always
    /// unambiguous and the invariant US formats are only a last resort.
    /// </summary>
    public static bool TryParseDate(string? text, out DateTime date) => TryParseDate(text, CultureInfo.CurrentCulture, out date);

    /// <summary>Same as <see cref="TryParseDate(string?, out DateTime)"/> with an explicit culture (the OS locale pnputil printed in).</summary>
    public static bool TryParseDate(string? text, CultureInfo culture, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(culture);
        var t = text.Trim();
        if (DateTime.TryParseExact(t, IsoDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        var patterns = new[] { culture.DateTimeFormat.ShortDatePattern, "d" };
        if (DateTime.TryParseExact(t, patterns, culture, DateTimeStyles.AllowWhiteSpaces, out date)
            || DateTime.TryParse(t, culture, DateTimeStyles.None, out date))
        {
            return true;
        }

        return DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>Counts how many "Exporting driver package" lines <c>pnputil /export-driver</c> has printed so far.</summary>
    public static int CountExportLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count(IsExportLine);
    }

    /// <summary>True for a progress line of <c>pnputil /export-driver</c> ("Exporting driver package: oem3.inf").</summary>
    public static bool IsExportLine(string? line) =>
        !string.IsNullOrWhiteSpace(line)
        && line.Contains("driver package", StringComparison.OrdinalIgnoreCase)
        && line.Contains("export", StringComparison.OrdinalIgnoreCase)
        && line.Contains(".inf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the "Total driver packages: N" / "Exported driver packages: N" summary lines when present.</summary>
    public static (int? Total, int? Exported) ParseExportSummary(string? output)
    {
        int? total = null, exported = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            return (total, exported);
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains("driver packages", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var m = TrailingNumber().Match(line);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                continue;
            }

            if (line.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
            {
                total = n;
            }
            else if (line.StartsWith("Exported", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Added", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Deleted", StringComparison.OrdinalIgnoreCase))
            {
                exported = n;
            }
        }

        return (total, exported);
    }

    /// <summary>Maps a pnputil exit code to success/reboot flags and a plain-English explanation.</summary>
    public static (bool Success, bool RebootRequired, string Text) InterpretExitCode(int exitCode) => exitCode switch
    {
        0 => (true, false, "Completed successfully."),
        NoMoreItemsExitCode => (true, false, "Nothing matched, so nothing was changed."),
        RebootRequiredExitCode => (true, true, "Completed; restart Windows to finish."),
        RebootInitiatedExitCode => (true, true, "Completed; Windows is restarting to finish."),
        2 => (false, false, "The file was not found."),
        3 => (false, false, "The folder was not found."),
        5 => (false, false, "Access was denied. Run PC Checker as an administrator."),
        87 => (false, false, "pnputil rejected the arguments."),
        1168 => (false, false, "The driver package was not found in the driver store."),
        -536870329 => (false, false, "The driver package is in use by a device and could not be removed. Try again with the device uninstalled."),
        _ => (false, false, $"pnputil exited with code {exitCode}."),
    };

    private static string Normalize(string key) => key.Replace(" ", string.Empty).ToLowerInvariant();
}
