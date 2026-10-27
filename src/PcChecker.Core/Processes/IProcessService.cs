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

    /// <summary>Logical processors of the PC (for the affinity choice).</summary>
    int LogicalProcessorCount { get; }

    /// <summary>
    /// Asks the program's windows to close, as clicking their X does, and waits a few seconds. The value is true when
    /// the program has exited, false when it is still running (it may be asking to save). Journaled.
    /// </summary>
    Task<OperationResult<bool>> CloseWindowsAsync(ProcessInfo process, CancellationToken cancellationToken);

    /// <summary>Pauses every thread of the process. Refused for critical processes and Windows' own. Journaled.</summary>
    Task<OperationResult> SuspendAsync(ProcessInfo process, CancellationToken cancellationToken);

    /// <summary>Lets a suspended process run again. Journaled.</summary>
    Task<OperationResult> ResumeAsync(ProcessInfo process, CancellationToken cancellationToken);

    /// <summary>Turns efficiency mode (EcoQoS and Low priority) on or off. Refused for Windows' own processes. Journaled.</summary>
    Task<OperationResult> SetEfficiencyModeAsync(ProcessInfo process, bool on, CancellationToken cancellationToken);

    /// <summary>The processors the program may run on, or null when it cannot be read.</summary>
    ulong? GetAffinity(ProcessInfo process);

    /// <summary>Sets the processors the program may run on (see <see cref="AffinityMath"/>). Journaled.</summary>
    Task<OperationResult> SetAffinityAsync(ProcessInfo process, ulong mask, CancellationToken cancellationToken);

    /// <summary>Brings the program's window to the front, or minimizes or maximizes it.</summary>
    OperationResult ShowWindow(ProcessInfo process, WindowCommand command);

    /// <summary>Opens the Windows file properties dialog of the program file.</summary>
    OperationResult ShowProperties(ProcessInfo process);

    /// <summary>
    /// Writes a full memory dump of the process to the Temp folder (Task Manager's "Create memory dump file") and
    /// returns its path. The program keeps running. Journaled.
    /// </summary>
    Task<OperationResult<string>> CreateDumpAsync(ProcessInfo process, CancellationToken cancellationToken);

    /// <summary>Opens File Explorer with <paramref name="path"/> selected (used for a new dump file).</summary>
    OperationResult ShowInFolder(string path);

    /// <summary>
    /// Closes the program (windows first, then ending it) and starts the same file with the same options again, without
    /// administrator rights unless it had them. File Explorer is restarted by Windows. Journaled.
    /// </summary>
    Task<OperationResult> RestartAsync(ProcessInfo process, CancellationToken cancellationToken);

    /// <summary>
    /// Task Manager's "Run new task": opens a program, file, folder or web address. Without
    /// <paramref name="asAdministrator"/> it starts without administrator rights, even though PC Checker has them. Journaled.
    /// </summary>
    Task<OperationResult> RunNewTaskAsync(string command, bool asAdministrator, CancellationToken cancellationToken);
}
