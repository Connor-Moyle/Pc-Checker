namespace PcChecker.Core.Restore;

public sealed record RestorePoint(long SequenceNumber, string Description, DateTimeOffset CreationTime, string Type);

/// <summary>Options for one guarded change. Null booleans fall back to the user's restore point settings.</summary>
public sealed record ChangeOptions(
    bool? CreateBefore,
    bool? CreateAfter,
    string Module,
    string Target,
    bool Reversible,
    string? UndoHint)
{
    public static ChangeOptions For(string module, string target, bool reversible = false, string? undoHint = null) =>
        new(null, null, module, target, reversible, undoHint);
}
