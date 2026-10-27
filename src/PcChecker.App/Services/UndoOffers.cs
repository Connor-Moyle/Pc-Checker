using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.App.Services;

/// <summary>
/// Runs a change with an undo attached, so the success notification that follows offers "Undo" for a few seconds:
/// <code>
/// var result = await UndoOffers.RunAsync(() => service.Disable(item, ct), undoCt => service.Enable(item, undoCt), item.Name);
/// if (result.Success) _notifications.Success(...);
/// </code>
/// Use it only for changes the same service can take straight back. The guarded change registers the undo itself (see
/// <see cref="ChangeUndo"/>); when it did not (demo services skip the change guard, and some services mark a change as
/// not reversible in general even though this particular call is), the undo is registered here. The undo must call the
/// service, so it is journaled like any other change.
/// </summary>
public static class UndoOffers
{
    public static async Task<OperationResult> RunAsync(Func<Task<OperationResult>> change, Func<CancellationToken, Task<OperationResult>> undo, string description)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(undo);
        using var scope = ChangeUndo.Offer(undo);
        var result = await change();
        if (result.Success && !scope.Registered && !ChangeUndo.IsSuppressed && Tracker() is { } tracker)
        {
            tracker.Register(new UndoableChange(Guid.NewGuid(), scope.Id, description, "PC Checker", description, DateTimeOffset.Now, undo));
            scope.MarkRegistered();
        }

        return result;
    }

    private static IChangeUndoTracker? Tracker()
    {
        try
        {
            return App.Services.GetService(typeof(IChangeUndoTracker)) as IChangeUndoTracker;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
