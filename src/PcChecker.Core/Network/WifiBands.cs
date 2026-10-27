namespace PcChecker.Core.Network;

/// <summary>Wi-Fi band names, channel → band mapping and signal conversions. Pure.</summary>
public static class WifiBands
{
    public const string Band24 = "2.4 GHz";
    public const string Band5 = "5 GHz";
    public const string Band6 = "6 GHz";
    public const string Unknown = "Unknown";

    /// <summary>The only 2.4 GHz channels that do not overlap each other.</summary>
    public static IReadOnlyList<int> NonOverlapping24 { get; } = [1, 6, 11];

    /// <summary>
    /// The band a channel belongs to. A band reported by netsh always wins. Without one: 1–14 → 2.4 GHz,
    /// 32–177 → 5 GHz, and 181–233 → 6 GHz only for a Wi-Fi 6E/7 radio (802.11ax/be); anything else is unknown.
    /// (6 GHz reuses the numbers 1–177, so a low channel without a band field is assumed to be 2.4 or 5 GHz.)
    /// </summary>
    public static string FromChannel(int? channel, string? reportedBand = null, string? radio = null)
    {
        var band = NetshFields.Band(reportedBand);
        if (band is not null)
        {
            return band;
        }

        return channel switch
        {
            >= 1 and <= 14 => Band24,
            >= 32 and <= 177 => Band5,
            >= 181 and <= 233 when IsWifi6OrNewer(radio) => Band6,
            _ => Unknown,
        };
    }

    /// <summary>Windows reports signal quality as 0–100 %; the usual estimate is dBm = percent / 2 − 100.</summary>
    public static int SignalToDbm(int percent) => (int)Math.Round(Math.Clamp(percent, 0, 100) / 2.0 - 100);

    /// <summary>Inverse of <see cref="SignalToDbm"/>: −100 dBm → 0 %, −50 dBm or better → 100 %.</summary>
    public static int DbmToSignal(int dbm) => Math.Clamp((dbm + 100) * 2, 0, 100);

    /// <summary>Plain-English label for a signal strength.</summary>
    public static string DescribeSignal(int? percent) => percent switch
    {
        null => "Unknown",
        >= 80 => "Excellent",
        >= 60 => "Good",
        >= 40 => "Fair",
        _ => "Weak",
    };

    /// <summary>"Wi-Fi 6 (802.11ax)" style name for a radio standard.</summary>
    public static string DescribeRadio(string? radio)
    {
        var r = radio?.Trim().ToLowerInvariant();
        return r switch
        {
            null or "" => "Unknown",
            "802.11be" => "Wi-Fi 7 (802.11be)",
            "802.11ax" => "Wi-Fi 6/6E (802.11ax)",
            "802.11ac" => "Wi-Fi 5 (802.11ac)",
            "802.11n" => "Wi-Fi 4 (802.11n)",
            "802.11g" => "Wi-Fi 3 (802.11g)",
            "802.11a" => "Wi-Fi 2 (802.11a)",
            "802.11b" => "Wi-Fi 1 (802.11b)",
            _ => radio!,
        };
    }

    /// <summary>Rank of a radio standard (b=1 … be=7), 0 when unknown.</summary>
    public static int RadioGeneration(string? radio) => radio?.Trim().ToLowerInvariant() switch
    {
        "802.11be" => 7,
        "802.11ax" => 6,
        "802.11ac" => 5,
        "802.11n" => 4,
        "802.11g" => 3,
        "802.11a" => 2,
        "802.11b" => 1,
        _ => 0,
    };

    public static bool IsWifi6OrNewer(string? radio) => RadioGeneration(radio) >= 6;

    /// <summary>True for 2.4 GHz channels that overlap (closer than five channels apart).</summary>
    public static bool Overlaps24(int a, int b) => Math.Abs(a - b) <= 4;
}
