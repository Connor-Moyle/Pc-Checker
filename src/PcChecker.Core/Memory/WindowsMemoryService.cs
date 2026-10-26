// TODO(memory): memory module — GlobalMemoryStatusEx, performance counters, Get-MMAgent, page file WMI, memory test, standby purge.
using PcChecker.Core.Common;

namespace PcChecker.Core.Memory;

/// <summary>Windows implementation of <see cref="IMemoryService"/>.</summary>
public sealed class WindowsMemoryService : IMemoryService
{
    private const string NotImplemented = "Memory tools are not implemented yet.";

    public IReadOnlyList<MemoryProfile> Profiles => MemoryProfileCatalog.All;
    public MemoryOverview? Last { get; private set; }

    public Task<MemoryOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        Last = MemoryOverview.Empty(NotImplemented);
        return Task.FromResult(Last);
    }

    public Task<MemoryTestResult> RunMemoryTestAsync(int percentOfAvailable, int passes, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new MemoryTestResult(0, 0, 0, [], TimeSpan.Zero, false, NotImplemented));

    public Task<OperationResult> ScheduleWindowsMemoryDiagnosticAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> ApplyProfileAsync(MemoryProfile profile, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> ClearStandbyListAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetPageFileAsync(string drive, bool systemManaged, int? initialMb, int? maxMb, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetMemoryCompressionAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
}
