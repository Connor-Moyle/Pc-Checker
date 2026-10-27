using PcChecker.Core.Checkup;
using PcChecker.Core.Sensors.PawnIo;
using static PcChecker.Core.Shell.SearchText;

namespace PcChecker.Core.Shell;

/// <summary>How urgent a "What needs attention" item is. Lower values come first.</summary>
public enum AttentionLevel
{
    /// <summary>Something is wrong or risky now (updates turned off, the system drive almost full).</summary>
    Critical,

    /// <summary>Worth doing soon (updates waiting, a nearly full drive, driver problems).</summary>
    Warning,

    /// <summary>Would make the PC nicer (fewer startup apps, a fresh check-up).</summary>
    Suggestion,
}

/// <summary>What the item's one button does.</summary>
public enum AttentionAction
{
    /// <summary>Open the page named by <see cref="AttentionItem.PageKey"/>.</summary>
    OpenPage,

    /// <summary>Open Check-up and start it (the same as the Dashboard's Run check-up).</summary>
    RunCheckup,

    /// <summary>Ask, then install the PawnIO sensor driver (the shared install flow of the Fans page).</summary>
    InstallPawnIo,
}

/// <summary>One drive as the rules see it.</summary>
public sealed record AttentionDrive(string Letter, string? Label, double UsedPercent, long FreeBytes, bool IsSystemDrive);

/// <summary>
/// What the Dashboard already knows, gathered from the services' last results. Unknown values stay null (or empty), and
/// an unknown value never produces an item.
/// </summary>
public sealed record AttentionFacts
{
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    /// <summary>When the last check-up finished; null when there has been none (this session or saved).</summary>
    public DateTimeOffset? LastCheckup { get; init; }

    /// <summary>How many items the last check-up left on its to-do list, and how many of those are critical.</summary>
    public int CheckupItems { get; init; }

    public int CheckupCriticalItems { get; init; }

    public IReadOnlyList<AttentionDrive> Drives { get; init; } = [];

    /// <summary>Enabled startup apps estimated to slow sign-in a lot; null when the startup list was not read.</summary>
    public int? HeavyStartupApps { get; init; }

    /// <summary>The Windows Update tile's view (service off, restart pending, updates waiting…); null when unknown.</summary>
    public UpdateGlance? Updates { get; init; }

    /// <summary>Where the PawnIO sensor driver stands; null when it was not checked.</summary>
    public PawnIoState? PawnIo { get; init; }

    /// <summary>Devices whose driver reports a problem (from the last driver scan); null when unknown.</summary>
    public int? DeviceProblems { get; init; }
}

/// <summary>
/// One recommendation. <see cref="Title"/> and <see cref="Detail"/> are English format strings (translated by the app)
/// filled with <see cref="TitleArgs"/> and <see cref="DetailArgs"/>; <see cref="ActionText"/> is the button.
/// </summary>
public sealed record AttentionItem(
    string Id,
    AttentionLevel Level,
    int Priority,
    string Title,
    IReadOnlyList<object> TitleArgs,
    string Detail,
    IReadOnlyList<object> DetailArgs,
    string ActionText,
    AttentionAction Action,
    string PageKey);

/// <summary>
/// The Dashboard's "What needs attention" card: plain-language recommendations built from results the app already has,
/// most urgent first, at most <see cref="MaxItems"/>. Live problems the Dashboard's alerts strip already shows
/// (temperatures, critical health items, a pending restart from another cause) are left to that strip.
/// </summary>
public static class AttentionRules
{
    public const int MaxItems = 5;

    /// <summary>A check-up older than this is "a while ago".</summary>
    public static readonly TimeSpan CheckupStaleAfter = TimeSpan.FromDays(14);

    /// <summary>A drive at or above this share used gets an item; the system drive is critical at <see cref="CriticalUsedPercent"/>.</summary>
    public const double WarnUsedPercent = 90;

    public const double CriticalUsedPercent = 95;

    /// <summary>The system drive is critical below this much free space, whatever its size.</summary>
    public const long CriticalFreeBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>Heavy startup apps from this many get a suggestion, and a warning from <see cref="ManyHeavyStartupApps"/>.</summary>
    public const int SomeHeavyStartupApps = 2;

    public const int ManyHeavyStartupApps = 5;

    /// <summary>All items that apply, most urgent first, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<AttentionItem> Build(AttentionFacts facts, int max = MaxItems)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var items = new List<AttentionItem>();
        AddUpdates(facts, items);
        AddDrive(facts, items);
        AddDeviceProblems(facts, items);
        AddPawnIo(facts, items);
        AddStartup(facts, items);
        AddCheckup(facts, items);
        return Rank(items).Take(Math.Max(0, max)).ToList();
    }

    /// <summary>Orders items by level, then by each rule's priority, then by id so the order never flickers.</summary>
    public static IEnumerable<AttentionItem> Rank(IEnumerable<AttentionItem> items) =>
        items.OrderBy(i => i.Level).ThenBy(i => i.Priority).ThenBy(i => i.Id, StringComparer.Ordinal);

    /// <summary>True when there is no check-up or the last one is older than <see cref="CheckupStaleAfter"/>.</summary>
    public static bool IsCheckupStale(DateTimeOffset? lastCheckup, DateTimeOffset now) =>
        lastCheckup is not { } last || now - last >= CheckupStaleAfter;

    /// <summary>Whole days between <paramref name="then"/> and <paramref name="now"/> (never negative).</summary>
    public static int DaysAgo(DateTimeOffset then, DateTimeOffset now) => Math.Max(0, (int)Math.Floor((now - then).TotalDays));

    private static void AddUpdates(AttentionFacts facts, List<AttentionItem> items)
    {
        if (facts.Updates is not { } updates)
        {
            return;
        }

        switch (updates.State)
        {
            case UpdateGlanceState.ServiceOff:
                items.Add(Item("updates.off", AttentionLevel.Critical, 0, T("Windows Update is turned off"), [],
                    T("No security fixes arrive while it is off."), [], T("Review"), AttentionAction.OpenPage, "windows-update"));
                break;
            case UpdateGlanceState.RestartPending:
                items.Add(Item("updates.restart", AttentionLevel.Warning, 0, T("Restart to finish installing updates"), [],
                    T("Windows has installed updates that complete after a restart."), [], T("Review"), AttentionAction.OpenPage, "windows-update"));
                break;
            case UpdateGlanceState.UpdatesWaiting when updates.Waiting > 0:
                items.Add(Item("updates.waiting", AttentionLevel.Warning, 1,
                    updates.Waiting == 1 ? T("Windows Update has 1 update waiting") : T("Windows Update has {0} updates waiting"), [updates.Waiting],
                    T("Installing them keeps the PC safe and stable."), [], T("Install updates"), AttentionAction.OpenPage, "windows-update"));
                break;
        }
    }

    private static void AddDrive(AttentionFacts facts, List<AttentionItem> items)
    {
        var full = facts.Drives
            .Where(d => d.UsedPercent >= WarnUsedPercent || (d.IsSystemDrive && d.FreeBytes < CriticalFreeBytes && d.UsedPercent > 0))
            .OrderByDescending(d => IsCritical(d))
            .ThenByDescending(d => d.UsedPercent)
            .ThenByDescending(d => d.IsSystemDrive)
            .ToList();
        if (full.Count == 0)
        {
            return;
        }

        var drive = full[0];
        int others = full.Count - 1;
        string detail = others == 0
            ? T("Windows and programs slow down when a drive is nearly full.")
            : others == 1 ? T("1 other drive is nearly full too.") : T("{0} other drives are nearly full too.");
        items.Add(Item("drive.full", IsCritical(drive) ? AttentionLevel.Critical : AttentionLevel.Warning, 1,
            T("Drive {0} is {1} % full"), [drive.Letter, Math.Round(drive.UsedPercent)],
            detail, others > 1 ? [others] : [],
            T("Free up space"), AttentionAction.OpenPage, drive.IsSystemDrive ? "cleanup" : "disk-space"));
    }

    private static bool IsCritical(AttentionDrive drive) =>
        drive.IsSystemDrive && (drive.UsedPercent >= CriticalUsedPercent || (drive.FreeBytes < CriticalFreeBytes && drive.UsedPercent > 0));

    private static void AddDeviceProblems(AttentionFacts facts, List<AttentionItem> items)
    {
        if (facts.DeviceProblems is not ({ } count and > 0))
        {
            return;
        }

        items.Add(Item("drivers.problems", AttentionLevel.Warning, 2,
            count == 1 ? T("1 device has a driver problem") : T("{0} devices have a driver problem"), [count],
            T("A device with a driver problem may not work properly."), [], T("Check drivers"), AttentionAction.OpenPage, "drivers"));
    }

    private static void AddPawnIo(AttentionFacts facts, List<AttentionItem> items)
    {
        switch (facts.PawnIo)
        {
            case PawnIoState.NotInstalled:
                items.Add(Item("pawnio.missing", AttentionLevel.Suggestion, 1, T("PawnIO is missing, so CPU and motherboard fans can't be read"), [],
                    T("PawnIO is a free, signed driver. You can remove it later from Settings → Apps."), [], T("Install"), AttentionAction.InstallPawnIo, "fans"));
                break;
            case PawnIoState.InstalledNotRunning:
                items.Add(Item("pawnio.stopped", AttentionLevel.Suggestion, 1, T("PawnIO is installed but not running"), [],
                    T("CPU and motherboard sensors stay hidden until it runs. A restart often fixes this."), [], T("Review"), AttentionAction.OpenPage, "fans"));
                break;
        }
    }

    private static void AddStartup(AttentionFacts facts, List<AttentionItem> items)
    {
        if (facts.HeavyStartupApps is not ({ } count and >= SomeHeavyStartupApps))
        {
            return;
        }

        items.Add(Item("startup.heavy", count >= ManyHeavyStartupApps ? AttentionLevel.Warning : AttentionLevel.Suggestion, 3,
            T("{0} apps start with Windows and slow it down"), [count],
            T("Turning off the ones you don't need straight away makes sign-in faster."), [], T("Review"), AttentionAction.OpenPage, "startup"));
    }

    private static void AddCheckup(AttentionFacts facts, List<AttentionItem> items)
    {
        if (facts.LastCheckup is not { } last)
        {
            items.Add(Item("checkup.never", AttentionLevel.Suggestion, 0, T("No check-up yet"), [],
                T("A check-up looks at everything in one go and lists what to fix."), [], T("Run check-up"), AttentionAction.RunCheckup, "checkup"));
            return;
        }

        if (IsCheckupStale(last, facts.Now))
        {
            items.Add(Item("checkup.stale", AttentionLevel.Suggestion, 0, T("Last check-up was {0} days ago"), [DaysAgo(last, facts.Now)],
                T("Run it again to catch anything new."), [], T("Run check-up"), AttentionAction.RunCheckup, "checkup"));
            return;
        }

        if (facts.CheckupItems > 0)
        {
            items.Add(Item("checkup.todo", facts.CheckupCriticalItems > 0 ? AttentionLevel.Warning : AttentionLevel.Suggestion, 4,
                facts.CheckupItems == 1 ? T("The last check-up found 1 thing to do") : T("The last check-up found {0} things to do"), [facts.CheckupItems],
                T("Each one explains why it matters and how to fix it."), [], T("Review"), AttentionAction.OpenPage, "checkup"));
        }
    }

    private static AttentionItem Item(string id, AttentionLevel level, int priority, string title, object[] titleArgs, string detail,
        object[] detailArgs, string actionText, AttentionAction action, string pageKey) =>
        new(id, level, priority, title, titleArgs, detail, detailArgs, actionText, action, pageKey);
}
