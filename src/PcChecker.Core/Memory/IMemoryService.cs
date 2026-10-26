using PcChecker.Core.Common;

namespace PcChecker.Core.Memory;

/// <summary>RAM overview, in-app memory test, Windows Memory Diagnostic, memory profiles and page file control.</summary>
public interface IMemoryService
{
    Task<MemoryOverview> GetOverviewAsync(CancellationToken cancellationToken);
    Task<MemoryTestResult> RunMemoryTestAsync(int percentOfAvailable, int passes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> ScheduleWindowsMemoryDiagnosticAsync(CancellationToken cancellationToken);
    IReadOnlyList<MemoryProfile> Profiles { get; }
    Task<OperationResult> ApplyProfileAsync(MemoryProfile profile, CancellationToken cancellationToken);
    Task<OperationResult> ClearStandbyListAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetPageFileAsync(string drive, bool systemManaged, int? initialMb, int? maxMb, CancellationToken cancellationToken);
    Task<OperationResult> SetMemoryCompressionAsync(bool enabled, CancellationToken cancellationToken);
    MemoryOverview? Last { get; }
}
