namespace PcChecker.Core.Logging;

/// <summary>Append-only journal of system changes made by the app.</summary>
public interface IActionJournal
{
    Task RecordAsync(ActionRecord record);

    /// <summary>Most recent records first.</summary>
    Task<IReadOnlyList<ActionRecord>> ReadAsync(int take = 500);

    string JournalPath { get; }
}
