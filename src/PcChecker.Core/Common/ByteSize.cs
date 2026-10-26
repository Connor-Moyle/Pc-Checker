using System.Globalization;

namespace PcChecker.Core.Common;

/// <summary>Human-friendly byte formatting ("1.23 GB").</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    public const long Kilobyte = 1024L;
    public const long Megabyte = Kilobyte * 1024L;
    public const long Gigabyte = Megabyte * 1024L;
    public const long Terabyte = Gigabyte * 1024L;

    /// <summary>Formats a byte count using binary multiples (1024) with up to two decimals.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Format(bytes == long.MinValue ? long.MaxValue : -bytes);
        }

        if (bytes < Kilobyte)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024d && unit < Units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        string number = value >= 100 ? value.ToString("0", CultureInfo.InvariantCulture)
            : value >= 10 ? value.ToString("0.0", CultureInfo.InvariantCulture)
            : value.ToString("0.00", CultureInfo.InvariantCulture);

        return number + " " + Units[unit];
    }

    /// <summary>Formats a nullable byte count, returning <paramref name="fallback"/> when null.</summary>
    public static string Format(long? bytes, string fallback = "—") => bytes.HasValue ? Format(bytes.Value) : fallback;
}
