using System.Globalization;
using PcChecker.App.Localization;
using PcChecker.Core.Crashes;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One row of the Crashes timeline.</summary>
public sealed class CrashRow
{
    public CrashRow(CrashEvent crash, CrashAdviceText advice)
    {
        Crash = crash;
        Advice = advice;
        var local = crash.Time.ToLocalTime();
        DayText = DayLabel(DateOnly.FromDateTime(local.DateTime), DateOnly.FromDateTime(DateTime.Now));
        TimeText = local.ToString("t", CultureInfo.CurrentCulture);
    }

    public CrashEvent Crash { get; }

    public CrashAdviceText Advice { get; }

    public CrashKind Kind => Crash.Kind;

    public string Title => Advice.Title;

    public string Summary => Advice.Summary;

    public string DayText { get; }

    public string TimeText { get; }

    public SymbolRegular Icon => Kind switch
    {
        CrashKind.BlueScreen => SymbolRegular.ErrorCircle24,
        CrashKind.SuddenShutdown => SymbolRegular.Power24,
        CrashKind.DriverReset => SymbolRegular.DesktopPulse24,
        CrashKind.AppHang => SymbolRegular.AppGeneric24,
        _ => SymbolRegular.WindowApps24,
    };

    public string KindText => KindName(Kind);

    public string AccessibleName => $"{TimeText}, {Title}, {Summary}";

    public static string KindName(CrashKind kind) => kind switch
    {
        CrashKind.BlueScreen => Loc.T("Blue screen"),
        CrashKind.SuddenShutdown => Loc.T("Sudden shutdown"),
        CrashKind.DriverReset => Loc.T("Driver reset"),
        CrashKind.AppHang => Loc.T("App stopped responding"),
        _ => Loc.T("App crash"),
    };

    /// <summary>"Today", "Yesterday" or the long date in the user's format.</summary>
    public static string DayLabel(DateOnly day, DateOnly today) =>
        day == today ? Loc.T("Today")
        : day == today.AddDays(-1) ? Loc.T("Yesterday")
        : day.ToDateTime(TimeOnly.MinValue).ToString("D", CultureInfo.CurrentCulture);
}

/// <summary>One line of the "Most crashing apps" card.</summary>
public sealed class AppCrashRow
{
    public AppCrashRow(AppCrashGroup group)
    {
        Group = group;
        var parts = new List<string>();
        if (group.Crashes > 0)
        {
            parts.Add(group.Crashes == 1 ? Loc.T("1 crash") : Loc.F("{0} crashes", group.Crashes));
        }

        if (group.Hangs > 0)
        {
            parts.Add(group.Hangs == 1 ? Loc.T("1 hang") : Loc.F("{0} hangs", group.Hangs));
        }

        CountText = string.Join(", ", parts);
        var detail = new List<string>();
        if (group.TopModule is { Length: > 0 } module && !string.Equals(module, group.AppName, StringComparison.OrdinalIgnoreCase))
        {
            detail.Add(Loc.F("Mostly in {0}", module));
        }

        if (group.TopExceptionCode is { } code)
        {
            detail.Add(ExceptionCodeCatalog.Describe(code));
        }

        detail.Add(Loc.F("last {0}", group.Latest.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        DetailText = string.Join(" · ", detail);
    }

    public AppCrashGroup Group { get; }

    public string AppName => Group.AppName;

    public string CountText { get; }

    public string DetailText { get; }

    public string AccessibleName => $"{AppName}, {CountText}";

    public string FilterButtonName => Loc.F("Show only {0}", AppName);
}

/// <summary>A choice in a filter drop-down.</summary>
public sealed record CrashFilterOption(string Text, int Days, CrashKind[] Kinds)
{
    public override string ToString() => Text;
}
