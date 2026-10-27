using PcChecker.Core.Common;

namespace PcChecker.Core.Restore;

/// <summary>
/// A guarded change that finished and can be taken back straight away. <see cref="Undo"/> must itself go through
/// <see cref="IChangeGuard"/> (call the same service the other way round), so the undo is journaled too.
/// <see cref="ScopeId"/> ties together the guarded changes made under one <see cref="ChangeUndo.Offer"/> scope.
/// <see cref="FlowId"/> is the undo flow (<see cref="ChangeUndo.BeginFlow"/>) of the code that made the change: only a
/// success notification shown from that same flow offers the Undo, so an unrelated notification never carries it.
/// </summary>
public sealed record UndoableChange(
    Guid Id,
    Guid ScopeId,
    string Description,
    string Module,
    string Target,
    DateTimeOffset CompletedAt,
    Func<CancellationToken, Task<OperationResult>> Undo,
    Guid? FlowId = null);

/// <summary>
/// Where one undoable change stands: picked for a notification (<see cref="OfferedAt"/>), on screen with its Undo
/// button (<see cref="ShownAt"/>, which starts the undo window) and used.
/// </summary>
public sealed record UndoEntry(UndoableChange Change, DateTimeOffset? OfferedAt = null, bool Used = false, DateTimeOffset? ShownAt = null);

/// <summary>
/// Lets the code that starts a change say how to take it back, without changing the service it calls: inside
/// <c>using (ChangeUndo.Offer(...))</c> every successful, reversible guarded change is registered with that undo.
/// Flows with the async call like <see cref="ChangeBatch"/>.
/// </summary>
public static class ChangeUndo
{
    private static readonly AsyncLocal<OfferScope?> CurrentScope = new();
    private static readonly AsyncLocal<bool> Suppressed = new();
    private static readonly AsyncLocal<Guid?> Flow = new();

    /// <summary>The undo flow of the running code, or null (see <see cref="BeginFlow"/>).</summary>
    public static Guid? CurrentFlow => Flow.Value;

    /// <summary>
    /// Starts a new undo flow for the rest of the calling method. Call it directly (it is not async on purpose) from the
    /// method that makes the change and then shows the success notification: the flow reaches the change below it and
    /// the notification after it, and ends with that method. <c>UndoOffers.RunAsync</c> in the app calls it for you.
    /// </summary>
    public static Guid BeginFlow()
    {
        var id = Guid.NewGuid();
        Flow.Value = id;
        return id;
    }

    /// <summary>The innermost open offer, or null.</summary>
    public static OfferScope? Current => CurrentScope.Value;

    /// <summary>True while an undo runs: changes made by it are not offered for undo again.</summary>
    public static bool IsSuppressed => Suppressed.Value;

    /// <summary>
    /// Opens a scope: guarded changes inside it can be undone with <paramref name="undo"/>. They belong to
    /// <paramref name="flowId"/>, or to the current flow.
    /// </summary>
    public static OfferScope Offer(Func<CancellationToken, Task<OperationResult>> undo, Guid? flowId = null)
    {
        ArgumentNullException.ThrowIfNull(undo);
        var scope = new OfferScope(Guid.NewGuid(), undo, CurrentScope.Value, flowId ?? Flow.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>Runs <paramref name="work"/> with undo offers switched off (used while an undo runs).</summary>
    public static async Task<T> SuppressAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        bool before = Suppressed.Value;
        Suppressed.Value = true;
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            Suppressed.Value = before;
        }
    }

    /// <summary>An open offer. Dispose it when the change has finished.</summary>
    public sealed class OfferScope : IDisposable
    {
        private readonly OfferScope? _outer;
        private bool _disposed;

        internal OfferScope(Guid id, Func<CancellationToken, Task<OperationResult>> undo, OfferScope? outer, Guid? flowId)
        {
            Id = id;
            Undo = undo;
            _outer = outer;
            FlowId = flowId;
        }

        public Guid Id { get; }

        /// <summary>The undo flow the changes in this scope belong to (null: no notification will offer them).</summary>
        public Guid? FlowId { get; }

        public Func<CancellationToken, Task<OperationResult>> Undo { get; }

        /// <summary>True once a guarded change inside the scope registered this undo.</summary>
        public bool Registered { get; internal set; }

        /// <summary>Marks the scope as used by a change that registered it some other way (demo services).</summary>
        public void MarkRegistered() => Registered = true;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentScope.Value = _outer;
        }
    }
}

/// <summary>
/// When a success notification offers "Undo", and for how long the button works. Pure, so the decisions are tested.
/// </summary>
public static class UndoOfferRules
{
    /// <summary>A change counts for the next success notification only if it finished this recently.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(5);

    /// <summary>How long the Undo button works after it appears.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);

    /// <summary>A notification queued behind others longer than this shows without its Undo button.</summary>
    public static readonly TimeSpan MaxQueueDelay = TimeSpan.FromSeconds(30);

    /// <summary>Entries older than this are forgotten.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The change a success notification shown at <paramref name="now"/> from undo flow <paramref name="flowId"/> should
    /// offer to undo: the one fresh change of that same flow that has not been offered or used. Null when the
    /// notification is not part of a flow (a background job, the tray, another page), when there is no such change, or
    /// when several finished together (one Undo button for several changes would be misleading; the Action Journal lists
    /// them).
    /// </summary>
    public static UndoEntry? PickOffer(IEnumerable<UndoEntry> entries, DateTimeOffset now, Guid? flowId)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (flowId is null)
        {
            return null;
        }

        var fresh = entries
            .Where(e => e.Change.FlowId == flowId && e.OfferedAt is null && !e.Used
                        && now - e.Change.CompletedAt <= Freshness && now >= e.Change.CompletedAt - Freshness)
            .ToList();
        return fresh.Count == 1 ? fresh[0] : null;
    }

    /// <summary>
    /// True when the notification that picked <paramref name="entry"/> may still show its Undo button when it appears at
    /// <paramref name="now"/> (it may have waited behind other notifications).
    /// </summary>
    public static bool CanShow(UndoEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !entry.Used && entry.OfferedAt is { } offered && now >= offered && now - offered <= MaxQueueDelay;
    }

    /// <summary>
    /// True when the Undo button for <paramref name="entry"/> still works at <paramref name="now"/>: the window runs from
    /// when the button appeared (or, if that was never reported, from when it was picked).
    /// </summary>
    public static bool CanUndo(UndoEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !entry.Used && (entry.ShownAt ?? entry.OfferedAt) is { } start && now >= start && now - start <= UndoWindow;
    }

    /// <summary>Drops entries older than <see cref="KeepFor"/>.</summary>
    public static IEnumerable<UndoEntry> Prune(IEnumerable<UndoEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(e => now - e.Change.CompletedAt <= KeepFor);
    }
}

/// <summary>Holds the changes that can be undone right now (in memory only; the journal keeps the full record).</summary>
public interface IChangeUndoTracker
{
    /// <summary>Remembers a finished change. A second change from the same offer scope replaces the first.</summary>
    void Register(UndoableChange change);

    /// <summary>
    /// The change a success notification at <paramref name="now"/> from undo flow <paramref name="flowId"/> should offer,
    /// marked as offered; or null.
    /// </summary>
    UndoableChange? TakeOffer(DateTimeOffset now, Guid? flowId);

    /// <summary>
    /// The notification with the Undo button for <paramref name="id"/> appeared at <paramref name="now"/>: the undo
    /// window starts. False when it came too late to offer Undo (the button should then be hidden).
    /// </summary>
    bool MarkShown(Guid id, DateTimeOffset now);

    /// <summary>True when the Undo button for <paramref name="id"/> still works.</summary>
    bool CanUndo(Guid id, DateTimeOffset now);

    /// <summary>Runs the undo once, within the window; a failure (or a late click) is returned, never thrown.</summary>
    Task<OperationResult> UndoAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Thread-safe in-memory <see cref="IChangeUndoTracker"/>.</summary>
public sealed class ChangeUndoTracker : IChangeUndoTracker
{
    private readonly object _gate = new();
    private List<UndoEntry> _entries = [];

    public void Register(UndoableChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            _entries = UndoOfferRules.Prune(_entries, change.CompletedAt)
                .Where(e => e.Change.ScopeId != change.ScopeId)
                .Append(new UndoEntry(change))
                .ToList();
        }
    }

    public UndoableChange? TakeOffer(DateTimeOffset now, Guid? flowId)
    {
        lock (_gate)
        {
            _entries = UndoOfferRules.Prune(_entries, now).ToList();
            var pick = UndoOfferRules.PickOffer(_entries, now, flowId);
            if (pick is null)
            {
                return null;
            }

            Replace(pick, pick with { OfferedAt = now });
            return pick.Change;
        }
    }

    public bool MarkShown(Guid id, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (Find(id) is not { } entry)
            {
                return false;
            }

            if (entry.ShownAt is not null)
            {
                return UndoOfferRules.CanUndo(entry, now);
            }

            if (!UndoOfferRules.CanShow(entry, now))
            {
                return false;
            }

            Replace(entry, entry with { ShownAt = now });
            return true;
        }
    }

    public bool CanUndo(Guid id, DateTimeOffset now)
    {
        lock (_gate)
        {
            return Find(id) is { } entry && UndoOfferRules.CanUndo(entry, now);
        }
    }

    public async Task<OperationResult> UndoAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        UndoableChange change;
        lock (_gate)
        {
            if (Find(id) is not { } entry)
            {
                return OperationResult.Fail("This change can no longer be undone here. The Action Journal (Logs page) says how to undo it.");
            }

            if (!UndoOfferRules.CanUndo(entry, now))
            {
                return OperationResult.Fail(entry.Used
                    ? "This change was already undone."
                    : "The time to undo has passed. The Action Journal (Logs page) says how to undo it.");
            }

            Replace(entry, entry with { Used = true });
            change = entry.Change;
        }

        try
        {
            return await ChangeUndo.SuppressAsync(() => change.Undo(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("The change could not be undone: " + ex.Message, ex);
        }
    }

    private UndoEntry? Find(Guid id) => _entries.FirstOrDefault(e => e.Change.Id == id);

    private void Replace(UndoEntry old, UndoEntry updated)
    {
        int index = _entries.IndexOf(old);
        if (index >= 0)
        {
            _entries[index] = updated;
        }
    }
}
