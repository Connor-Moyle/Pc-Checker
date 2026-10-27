using System.Globalization;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Rules for active hours, the part of the day Windows does not restart the PC for updates. Settings stores them as
/// whole hours (0–23) in <c>ActiveHoursStart</c>/<c>ActiveHoursEnd</c> under the Windows Update UX settings key and
/// allows a span of up to 18 hours, which may run past midnight. Pure.
/// </summary>
public static class ActiveHoursRules
{
    public const int MaxSpanHours = 18;
    public const int DefaultStart = 8;
    public const int DefaultEnd = 17;

    public const string StartValue = "ActiveHoursStart";
    public const string EndValue = "ActiveHoursEnd";

    /// <summary>1 while Windows adjusts active hours automatically from how the PC is used.</summary>
    public const string SmartStateValue = "SmartActiveHoursState";

    /// <summary>The length in hours from start to end, wrapping past midnight (8 → 17 is 9; 22 → 6 is 8).</summary>
    public static int SpanHours(int start, int end) => ((end - start) % 24 + 24) % 24;

    /// <summary>Null when the pair is allowed, otherwise a plain-English reason.</summary>
    public static string? Validate(int start, int end)
    {
        if (start is < 0 or > 23 || end is < 0 or > 23)
        {
            return "Pick hours between 00:00 and 23:00.";
        }

        if (start == end)
        {
            return "The start and end of active hours must be different.";
        }

        return SpanHours(start, end) > MaxSpanHours
            ? $"Active hours can cover at most {MaxSpanHours} hours. Choose a later start or an earlier end."
            : null;
    }

    /// <summary>The stored values, falling back to the Windows defaults (08:00–17:00) when missing or out of range.</summary>
    public static ActiveHours FromRegistry(int? start, int? end, int? smartState)
    {
        int s = start is >= 0 and <= 23 ? start.Value : DefaultStart;
        int e = end is >= 0 and <= 23 ? end.Value : DefaultEnd;
        return new ActiveHours(s, e, smartState == 1);
    }

    /// <summary>"08:00 – 17:00", with the automatic note when Windows picks them.</summary>
    public static string Describe(ActiveHours hours)
    {
        ArgumentNullException.ThrowIfNull(hours);
        var text = Hour(hours.StartHour) + " – " + Hour(hours.EndHour);
        return hours.IsAutomatic ? text + " (set automatically)" : text;
    }

    /// <summary>"08:00".</summary>
    public static string Hour(int hour) => ((hour % 24 + 24) % 24).ToString("00", CultureInfo.InvariantCulture) + ":00";
}
