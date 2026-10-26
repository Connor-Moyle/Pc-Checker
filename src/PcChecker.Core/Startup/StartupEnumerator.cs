// TODO(startup): startup module — registry Run keys, StartupApproved flags, startup folders, logon tasks.
using PcChecker.Core.Performance;

namespace PcChecker.Core.Startup;

/// <summary>Enumerates startup items from the registry, startup folders and Task Scheduler. Shared with the Performance module.</summary>
public static class StartupEnumerator
{
    public static Task<IReadOnlyList<StartupItem>> EnumerateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StartupItem>>([]);
}
