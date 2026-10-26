namespace PcChecker.Core.Logging;

/// <summary>A record of a change the app made to the system, with enough information to undo it.</summary>
public sealed record ActionRecord(
    Guid Id,
    DateTimeOffset Timestamp,
    string Module,
    string Action,
    string Target,
    string? Before,
    string? After,
    bool Reversible,
    string? UndoHint,
    string Outcome,
    long? RestorePointSequence)
{
    public static ActionRecord Create(string module, string action, string target, string outcome,
        string? before = null, string? after = null, bool reversible = false, string? undoHint = null, long? restorePointSequence = null) =>
        new(Guid.NewGuid(), DateTimeOffset.Now, module, action, target, before, after, reversible, undoHint, outcome, restorePointSequence);
}
