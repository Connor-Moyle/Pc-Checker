using System.Globalization;
using PcChecker.App.Localization;
using PcChecker.Core.Battery;

namespace PcChecker.App.ViewModels;

/// <summary>One stretch of recent use on the Battery page, with its text already built and translated.</summary>
public sealed record BatteryUsageRow(string TimeText, string StateText, bool OnAc, bool IsStandby, string DurationText, string ChangeText, string ChangeTooltip)
{
    public static BatteryUsageRow From(BatteryUsageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string state = entry.State switch
        {
            BatteryUsageState.Active => Loc.T("In use"),
            BatteryUsageState.Standby => Loc.T("Asleep"),
            BatteryUsageState.Off => Loc.T("Off or hibernating"),
            _ => Loc.T("Unknown"),
        };
        string source = entry.OnAc ? Loc.T("plugged in") : Loc.T("on battery");
        string change = string.Empty, tooltip = string.Empty;
        if (entry.DrainPercent is { } drain && entry.StartPercent is { } startPercent)
        {
            double endPercent = Math.Clamp(startPercent - drain, 0, 100);
            change = Loc.F("{0} % → {1} %", Round(startPercent), Round(endPercent));
            tooltip = entry.DrainPercentPerHour is { } perHour && Math.Abs(perHour) >= 0.05
                ? perHour > 0 ? Loc.F("Used {0} % an hour", perHour.ToString("0.#", CultureInfo.CurrentCulture))
                    : Loc.F("Charged {0} % an hour", (-perHour).ToString("0.#", CultureInfo.CurrentCulture))
                : Loc.T("The charge hardly changed.");
        }

        return new BatteryUsageRow(
            entry.Start.ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture),
            state + ", " + source,
            entry.OnAc,
            entry.State is BatteryUsageState.Standby or BatteryUsageState.Off,
            BatteryMath.FormatDuration(entry.Duration),
            change,
            tooltip);
    }

    private static string Round(double percent) => Math.Round(percent).ToString("0", CultureInfo.CurrentCulture);
}

/// <summary>One period of the usage history (a day or a week): time on battery and plugged in.</summary>
public sealed record BatteryHistoryRow(string PeriodText, string OnBatteryText, string OnAcText, string LifeText, string LifeTooltip)
{
    public static BatteryHistoryRow From(BatteryUsagePeriod usage, BatteryLifeEstimate? estimate)
    {
        ArgumentNullException.ThrowIfNull(usage);
        string life = estimate?.ActiveAtFullCharge is { } now ? BatteryMath.FormatDuration(now) : "—";
        string tooltip = estimate?.ActiveAtDesign is { } design
            ? Loc.F("When new it would have lasted {0}.", BatteryMath.FormatDuration(design))
            : string.Empty;
        return new BatteryHistoryRow(
            Period(usage.Start, usage.End),
            Loc.F("{0} in use, {1} asleep", BatteryMath.FormatDuration(usage.ActiveOnBattery), BatteryMath.FormatDuration(usage.StandbyOnBattery)),
            Loc.F("{0} in use, {1} asleep", BatteryMath.FormatDuration(usage.ActiveOnAc), BatteryMath.FormatDuration(usage.StandbyOnAc)),
            life,
            tooltip);
    }

    /// <summary>"12 Oct" for a day, "5 Oct – 12 Oct" for a longer period.</summary>
    public static string Period(DateTime start, DateTime end) =>
        (end - start).TotalDays <= 1.01
            ? start.ToString("ddd d MMM", CultureInfo.CurrentCulture)
            : start.ToString("d MMM yyyy", CultureInfo.CurrentCulture) + " – " + end.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
}

/// <summary>One tip on the Battery page.</summary>
public sealed record BatteryTipRow(string Title, string Text);
