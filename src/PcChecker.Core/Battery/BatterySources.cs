using System.Globalization;
using System.Text;
using PcChecker.Core.Power;

namespace PcChecker.Core.Battery;

/// <summary>
/// Pure helpers that turn what Windows reports into the Battery page's models: WMI chemistry codes, the live reading
/// from the Power module, merging WMI packs with the battery report's, and the names of the report files.
/// </summary>
public static class BatterySources
{
    /// <summary>
    /// Decodes <c>BatteryStaticData.Chemistry</c>: four ASCII letters packed into a 32-bit number, first letter in
    /// the lowest byte ("LION", "LiP"). Null for zero or unprintable values.
    /// </summary>
    public static string? DecodeChemistry(long? packed)
    {
        if (packed is not > 0 || packed > uint.MaxValue)
        {
            return null;
        }

        var bytes = BitConverter.GetBytes((uint)packed.Value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        var text = new StringBuilder(4);
        foreach (var b in bytes)
        {
            if (b == 0)
            {
                break;
            }

            if (b is < 0x20 or > 0x7E)
            {
                return null;
            }

            text.Append((char)b);
        }

        var code = text.ToString().Trim();
        return code.Length == 0 ? null : BatteryReportParser.ChemistryName(code);
    }

    /// <summary>Maps the Power module's battery reading to the page's live status; null when there is no battery.</summary>
    public static BatteryLiveStatus? Live(BatteryReading? reading)
    {
        if (reading is not { Present: true } r)
        {
            return null;
        }

        var state = r.Charging ? BatteryPowerState.Charging
            : r.OnBattery ? BatteryPowerState.Discharging
            : BatteryPowerState.PluggedInNotCharging;
        double? rate = state switch
        {
            BatteryPowerState.Charging => r.ChargeW,
            BatteryPowerState.Discharging => r.DischargeW,
            _ => null,
        };
        return new BatteryLiveStatus(r.Percent, state, rate is > 0 ? rate : null, r.RemainingCapacityMwh,
            state == BatteryPowerState.Discharging ? r.EstimatedRemaining : null);
    }

    /// <summary>
    /// Merges the packs WMI reported with the battery report's, pack by pack in order: WMI values win, the report fills
    /// what WMI left out (manufacturer, chemistry, cycles). With no WMI packs, the report's are used.
    /// </summary>
    public static IReadOnlyList<BatteryPackInfo> MergePacks(IReadOnlyList<BatteryPackInfo> wmi, IReadOnlyList<BatteryPackInfo> report)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(report);
        if (wmi.Count == 0)
        {
            return report;
        }

        var merged = new List<BatteryPackInfo>(wmi.Count);
        for (int i = 0; i < wmi.Count; i++)
        {
            var w = wmi[i];
            var r = i < report.Count ? report[i] : null;
            merged.Add(r is null ? w : new BatteryPackInfo(
                IsGenericName(w.Name) && !IsGenericName(r.Name) ? r.Name : w.Name,
                w.Manufacturer ?? r.Manufacturer,
                IsUnknown(w.Chemistry) ? r.Chemistry ?? w.Chemistry : w.Chemistry,
                w.DesignCapacityMwh ?? r.DesignCapacityMwh,
                w.FullChargeCapacityMwh ?? r.FullChargeCapacityMwh,
                w.CycleCount ?? r.CycleCount));
        }

        return merged;
    }

    /// <summary>"battery-report-2026-10-27-1430.html": sortable, and unique to the minute.</summary>
    public static string ReportFileName(DateTime localNow) =>
        "battery-report-" + localNow.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + ".html";

    /// <summary>Documents\PC Checker Reports (the profile folder when Documents is unavailable).</summary>
    public static string DefaultReportsFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(documents, "PC Checker Reports");
    }

    private static bool IsGenericName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.StartsWith("Battery ", StringComparison.OrdinalIgnoreCase) && name.Length <= 10
        || string.Equals(name.Trim(), "Internal Battery", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknown(string? chemistry) =>
        string.IsNullOrWhiteSpace(chemistry) || chemistry is "Unknown" or "Other";
}
