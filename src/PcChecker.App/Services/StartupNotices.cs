using PcChecker.Core.Common;

namespace PcChecker.App.Services;

/// <summary>
/// A message collected while the app started (crash recovery, reverted fan control, missing driver…).
/// <paramref name="Page"/> and <paramref name="PageTitle"/> name the page its Dashboard alert opens.
/// </summary>
public sealed record StartupNotice(string Title, string Message, Severity Severity, Type? Page = null, string? PageTitle = null);

/// <summary>
/// Notices gathered during startup for the Dashboard to show. Any service may add to it before the window is
/// visible; the Dashboard reads <see cref="All"/> and listens to <see cref="Changed"/>.
/// </summary>
public static class StartupNotices
{
    private static readonly object Gate = new();
    private static readonly List<StartupNotice> Items = [];

    public static event EventHandler? Changed;

    public static IReadOnlyList<StartupNotice> All
    {
        get
        {
            lock (Gate)
            {
                return Items.ToList();
            }
        }
    }

    public static void Add(string title, string message, Severity severity = Severity.Info, Type? page = null, string? pageTitle = null)
    {
        lock (Gate)
        {
            Items.Add(new StartupNotice(title, message, severity, page, pageTitle));
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Remove(StartupNotice notice)
    {
        bool removed;
        lock (Gate)
        {
            removed = Items.Remove(notice);
        }

        if (removed)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Items.Clear();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
