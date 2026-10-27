using PcChecker.Core.Common;

namespace PcChecker.Core.Restore;

/// <summary>
/// A guarded change that finished and can be taken back straight away. <see cref="Undo"/> must itself go through
/// <see cref="IChangeGuard"/> (call the same service the other way round), so the undo is journaled too.
/// <see cref="ScopeId"/> ties together the guarded changes made under one <see cref="ChangeUndo.Offer"/> scope.
/// </summary>
public sealed record UndoableChange(
    Guid Id,
    Guid ScopeId,
    string Description,
    string Module,
    string Target,
    DateTimeOffset CompletedAt,
    Func<CancellationToken, Task<OperationResult>> Undo);

/// <summary>Where one undoable change stands: offered (with the time the Undo button appeared) and used.</summary>
public sealed record UndoEntry(UndoableChange Change, DateTimeOffset? OfferedAt = null, bool Used = false);

/// <summary>
/// Lets the code that starts a change say how to take it back, without changing the service it calls: inside
/// <c>using (ChangeUndo.Offer(...))</c> every successful, reversible guarded change is registered with that undo.
/// Flows with the async call like <see cref="ChangeBatch"/>.
/// </summary>
public static class ChangeUndo
{
    private static readonly AsyncLocal<OfferScope?> CurrentScope = new();
    private static readonly AsyncLocal<bool> Suppressed = new();

    /// <summary>The innermost open offer, or null.</summary>
    public static OfferScope? Current => CurrentScope.Value;

    /// <summary>True while an undo runs: changes made by it are not offered for undo again.</summary>
    public static bool IsSuppressed => Suppressed.Value;

    /// <summary>Opens a scope: guarded changes inside it can be undone with <paramref name="undo"/>.</summary>
    public static OfferScope Offer(Func<CancellationToken, Task<OperationResult>> undo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        var scope = new OfferScope(Guid.NewGuid(), undo, CurrentScope.Value);
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

        internal OfferScope(Guid id, Func<CancellationToken, Task<OperationResult>> undo, OfferScope? outer)
        {
            Id = id;
            Undo = undo;
            _outer = outer;
        }

        public Guid Id { get; }

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

    /// <summary>Entries older than this are forgotten.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The change a success notification shown at <paramref name="now"/> should offer to undo: the one fresh change
    /// that has not been offered or used. Null when there is none, or when several changes finished together (one Undo
    /// button for several changes would be misleading; the Action Journal lists them).
    /// </summary>
    public static UndoEntry? PickOffer(IEnumerable<UndoEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var fresh = entries
            .Where(e => e.OfferedAt is null && !e.Used && now - e.Change.CompletedAt <= Freshness && now >= e.Change.CompletedAt - Freshness)
            .ToList();
        return fresh.Count == 1 ? fresh[0] : null;
    }

    /// <summary>True when the Undo button for <paramref name="entry"/> still works at <paramref name="now"/>.</summary>
    public static bool CanUndo(UndoEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !entry.Used && entry.OfferedAt is { } offered && now >= offered && now - offered <= UndoWindow;
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

    /// <summary>The change a success notification at <paramref name="now"/> should offer, marked as offered; or null.</summary>
    UndoableChange? TakeOffer(DateTimeOffset now);

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

    public UndoableChange? TakeOffer(DateTimeOffset now)
    {
        lock (_gate)
        {
            _entries = UndoOfferRules.Prune(_entries, now).ToList();
            var pick = UndoOfferRules.PickOffer(_entries, now);
            if (pick is null)
            {
                return null;
            }

            Replace(pick, pick with { OfferedAt = now });
            return pick.Change;
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
