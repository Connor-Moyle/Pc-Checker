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

    /// <summary>SetupAPI ERROR_DRIVER_INSTALL_BLOCKED (0xE0000247): the driver is unsigned or blocked by policy.</summary>
    public const int DriverInstallBlockedExitCode = unchecked((int)0xE0000247);

    /// <summary>SetupAPI ERROR_DRIVER_STORE_DELETE_FAILED (0xE000024A): the package is still in use by a device.</summary>
    public const int DriverStoreDeleteFailedExitCode = unchecked((int)0xE000024A);

    /// <summary>SetupAPI ERROR_NO_COMPAT_DRIVERS (0xE0000228): no device on this PC matches the package.</summary>
    public const int NoCompatibleDriversExitCode = unchecked((int)0xE0000228);

    /// <summary>SetupAPI ERROR_NO_CATALOG_FOR_OEM_INF (0xE000023F): the package has no signed catalog.</summary>
    public const int NoCatalogExitCode = unchecked((int)0xE000023F);

    /// <summary>SetupAPI ERROR_SIGNATURE_OSATTRIBUTE_MISMATCH (0xE0000244): signed for a different Windows version.</summary>
    public const int SignatureOsMismatchExitCode = unchecked((int)0xE0000244);

    private const int SetupApiFacilityMask = unchecked((int)0xFFFF0000);
    private const int SetupApiFacility = unchecked((int)0xE0000000);

    /// <summary>A "Label: value" line in any language (the label is localized, so anything up to the first colon counts).</summary>
    [GeneratedRegex(@"^\s*([^:\uFF1A]+?)\s*[:\uFF1A]\s*(.*?)\s*$")]
    private static partial Regex KeyValueLine();

    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex PublishedName();

    [GeneratedRegex(@"^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$")]
    private static partial Regex ClassGuid();

    /// <summary>pnputil's "Driver Version" value: a date followed by a dotted version ("02/10/2025 32.0.15.6094").</summary>
    [GeneratedRegex(@"^\S+\s+\d+(?:\.\d+){1,3}$")]
    private static partial Regex DriverVersionField();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex WholeNumber();

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
    /// Parses the output of <c>pnputil /enum-drivers</c>: blank-line separated blocks of "Label: Value" lines. The labels
    /// are printed in the Windows display language, so the English names are used when present and otherwise the
    /// fields are recognised by the shape of their values and by pnputil's fixed print order (see <see cref="MapFields"/>).
    /// Unknown fields are ignored; blocks without a published name are skipped.
    /// </summary>
    public static IReadOnlyList<DriverPackage> ParseEnumDrivers(string? output)
    {
        var packages = new List<DriverPackage>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return packages;
        }

        var entries = new List<(string Key, string Value)>();
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
                entries.Add((m.Groups[1].Value, m.Groups[2].Value.Trim()));
            }
        }

        Flush();
        return packages;

        void Flush()
        {
            if (entries.Count > 0)
            {
                var block = MapFields(entries);
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
            }

            entries.Clear();
        }
    }

    /// <summary>
    /// True when the text contains "Label: value" lines, i.e. pnputil printed a listing. Used to tell an empty driver
    /// store (nothing to parse) from a listing whose fields were not recognised.
    /// </summary>
    public static bool ContainsFieldLines(string? output) =>
        !string.IsNullOrWhiteSpace(output) && output.Split('\n').Any(l => KeyValueLine().IsMatch(l.TrimEnd('\r')));

    /// <summary>
    /// Maps one block's lines to pnputil's English field names. When the labels are not English, the fields are found
    /// by value shape (published name "oemN.inf", original name "*.inf", class GUID "{…}", driver version "date x.y.z.w")
    /// and by pnputil's fixed print order: Published Name, Original Name, Provider Name, Class Name, Class GUID,
    /// [Class Version], Driver Version, Signer Name.
    /// </summary>
    private static Dictionary<string, string> MapFields(List<(string Key, string Value)> entries)
    {
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            byKey.TryAdd(Normalize(key), value);
        }

        if (byKey.ContainsKey("publishedname"))
        {
            return byKey;
        }

        int published = entries.FindIndex(e => PublishedName().IsMatch(e.Value));
        if (published < 0)
        {
            return byKey;
        }

        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["publishedname"] = entries[published].Value };
        int original = entries.FindIndex(published + 1, e => IsInfName(e.Value));
        int guid = entries.FindIndex(e => ClassGuid().IsMatch(e.Value));
        int version = entries.FindLastIndex(e => DriverVersionField().IsMatch(e.Value));
        if (original >= 0)
        {
            mapped["originalname"] = entries[original].Value;
        }

        if (guid >= 0)
        {
            mapped["classguid"] = entries[guid].Value;
        }

        if (version >= 0)
        {
            mapped["driverversion"] = entries[version].Value;
            if (version + 1 < entries.Count)
            {
                mapped["signername"] = entries[version + 1].Value;
            }
        }

        // Provider and class name sit between the original name and the class GUID (or the driver version).
        int first = (original >= 0 ? original : published) + 1;
        int end = guid >= 0 ? guid : version >= 0 ? version : entries.Count;
        if (end - first >= 1)
        {
            mapped["providername"] = entries[first].Value;
        }

        if (end - first >= 2)
        {
            mapped["classname"] = entries[first + 1].Value;
        }

        return mapped;
    }

    private static bool IsInfName(string value) =>
        value.Length > 4 && value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) && !value.Any(char.IsWhiteSpace);

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

    /// <summary>
    /// True for a progress line of <c>pnputil /export-driver</c> ("Exporting driver package: oem3.inf"). The wording is
    /// localized, so the line is recognised by shape: a label, a colon and an .inf name. The "exported successfully"
    /// and summary lines carry no .inf name.
    /// </summary>
    public static bool IsExportLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        int colon = line.LastIndexOfAny([':', '\uFF1A']);
        return colon > 0 && IsInfName(line[(colon + 1)..].Trim());
    }

    /// <summary>
    /// Reads the "Total driver packages: N" / "Exported driver packages: N" summary lines when present. With localized
    /// labels the summary is the last two "label: number" lines, printed total first.
    /// </summary>
    public static (int? Total, int? Exported) ParseExportSummary(string? output)
    {
        int? total = null, exported = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            return (total, exported);
        }

        var counts = new List<int>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            int colon = line.LastIndexOfAny([':', '\uFF1A']);
            if (colon <= 0)
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            if (!WholeNumber().IsMatch(value) || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                continue;
            }

            counts.Add(n);
            if (!line.Contains("driver packages", StringComparison.OrdinalIgnoreCase))
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

        if (total is null && exported is null && counts.Count >= 2)
        {
            total = counts[^2];
            exported = counts[^1];
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
        DriverInstallBlockedExitCode => (false, false, "Windows blocked this driver: it is unsigned, or driver installation is blocked by policy (for example Memory Integrity or a Group Policy setting)."),
        DriverStoreDeleteFailedExitCode => (false, false, "The driver package is in use by a device and could not be removed. Try again with the device uninstalled."),
        NoCompatibleDriversExitCode => (false, false, "No device on this PC matches the driver package, so it was not installed."),
        NoCatalogExitCode => (false, false, "The driver package has no signed catalog (.cat) file, so Windows refused it."),
        SignatureOsMismatchExitCode => (false, false, "The driver is signed for a different version of Windows."),
        _ when (exitCode & SetupApiFacilityMask) == SetupApiFacility => (false, false, $"Windows setup reported error 0x{exitCode:X8} while handling the driver package."),
        _ => (false, false, $"pnputil exited with code {exitCode}."),
    };

    private static string Normalize(string key) => key.Replace(" ", string.Empty).ToLowerInvariant();
}
