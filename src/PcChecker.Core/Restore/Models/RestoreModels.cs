namespace PcChecker.Core.Restore;

public sealed record RestorePoint(long SequenceNumber, string Description, DateTimeOffset CreationTime, string Type);

/// <summary>What a guarded change does when the "before" restore point cannot be created.</summary>
public enum RestorePointFailedBehavior
{
    /// <summary>Run the change anyway and mention the missing restore point in the result and journal (default).</summary>
    Continue,

    /// <summary>Do not run the change; return a failure that tells the user how to enable System Protection.</summary>
    Abort,
}

/// <summary>Options for one guarded change. Null booleans fall back to the user's restore point settings.</summary>
public sealed record ChangeOptions(
    bool? CreateBefore,
    bool? CreateAfter,
    string Module,
    string Target,
    bool Reversible,
    string? UndoHint)
{
    /// <summary>Behaviour when the "before" restore point fails. Modules that make deep changes (Features, Drivers, Overclock) use Abort.</summary>
    public RestorePointFailedBehavior OnRestorePointFailed { get; init; } = RestorePointFailedBehavior.Continue;

    /// <summary>
    /// How to take the change back straight away (calling the same service the other way round), or null. When set on a
    /// <see cref="Reversible"/> change that succeeds outside a batch, the success notification offers "Undo" for a few
    /// seconds. <see cref="ChangeUndo.Offer"/> does the same from the calling code. Never written to the journal.
    /// </summary>
    public Func<CancellationToken, Task<PcChecker.Core.Common.OperationResult>>? Undo { get; init; }

    public static ChangeOptions For(string module, string target, bool reversible = false, string? undoHint = null) =>
        new(null, null, module, target, reversible, undoHint);
}
