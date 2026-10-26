using System.Globalization;
using System.Text;

namespace PcChecker.Core.Drivers;

/// <summary>Pure CSV export of an installed-driver list (for the "Export list" action and reports).</summary>
public static class DriverListExport
{
    public const string Header = "Device,Class,Provider,Version,Date,AgeDays,Signed,Source,Status,ProblemCode,Inf,DeviceId";

    /// <summary>Builds a UTF-8 friendly CSV with a header row; values containing commas, quotes or newlines are quoted.</summary>
    public static string BuildCsv(IEnumerable<DriverInfo> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var d in drivers)
        {
            sb.AppendJoin(',',
                Escape(d.DeviceName), Escape(d.DeviceClass), Escape(d.Provider), Escape(d.Version),
                Escape(d.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Escape(d.AgeDays?.ToString(CultureInfo.InvariantCulture)),
                d.IsSigned ? "Yes" : "No", d.IsInbox ? "Windows" : "Third-party", Escape(d.Status),
                d.ProblemCode.ToString(CultureInfo.InvariantCulture), Escape(d.InfName), Escape(d.DeviceId));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Quotes a CSV field when needed (RFC 4180 style).</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
