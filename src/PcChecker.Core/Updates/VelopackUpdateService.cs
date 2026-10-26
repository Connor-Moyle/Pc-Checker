// TODO(updates): updates module — Velopack UpdateManager + GitHub Releases fallback.
using System.Reflection;
using PcChecker.Core.Common;

namespace PcChecker.Core.Updates;

/// <summary>Velopack-based implementation of <see cref="IAppUpdateService"/>.</summary>
public sealed class VelopackUpdateService : IAppUpdateService
{
    public string CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public bool IsInstalledViaUpdater => false;

    public bool IsUpdateDownloaded => false;

    public Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<AppUpdateInfo?>(null);

    public Task<OperationResult> DownloadAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail("Updates are not implemented yet."));

    public void ApplyAndRestart()
    {
    }
}
