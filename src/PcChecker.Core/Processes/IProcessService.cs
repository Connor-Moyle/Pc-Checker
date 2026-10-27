using PcChecker.Core.Common;

namespace PcChecker.Core.Processes;

/// <summary>Running processes: live readings and the few actions the Processes page offers.</summary>
public interface IProcessService
{
    /// <summary>Process id of PC Checker itself (never offered for ending).</summary>
    int OwnProcessId { get; }

    /// <summary>
    /// Reads every process. CPU and disk rates cover the time since the previous call, so the first call reports zero
    /// for them. Signatures are checked a few files per call and show as pending until then.
    /// </summary>
    Task<ProcessSnapshot> SampleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the previous reading, so the next <see cref="SampleAsync"/> starts a new baseline (zero rates) instead of
    /// averaging over a pause or the time the page was hidden.
    /// </summary>
    void ResetRates();

    /// <summary>
    /// Ends the process (and, with <paramref name="includeChildren"/>, the processes it started). Critical Windows
    /// processes and PC Checker itself are refused. Journaled through the change guard without a restore point.
    /// </summary>
    Task<OperationResult> EndProcessAsync(ProcessInfo process, bool includeChildren, CancellationToken cancellationToken);

    /// <summary>Sets the priority class. Realtime is refused unless <paramref name="allowRealtime"/> is true.</summary>
    Task<OperationResult> SetPriorityAsync(ProcessInfo process, ProcessPriority priority, bool allowRealtime, CancellationToken cancellationToken);

    /// <summary>Opens File Explorer with the program file selected.</summary>
    OperationResult OpenFileLocation(ProcessInfo process);

    /// <summary>Opens a web search for the program's file name in the default browser.</summary>
    OperationResult SearchOnline(ProcessInfo process);
}
