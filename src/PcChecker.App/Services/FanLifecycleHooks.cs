using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Fans;

namespace PcChecker.App.Services;

/// <summary>
/// Ties the fan service to the app lifetime: crash recovery and "apply on startup" after the window is shown,
/// "revert to automatic on exit" before the container is disposed. Every step is guarded so a fan problem can
/// never stop the app from starting or closing.
/// </summary>
public static class FanLifecycleHooks
{
    /// <summary>Runs the fan service's startup work in the background and posts its notices to the dashboard.</summary>
    public static void OnStartup(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _ = Task.Run(async () =>
        {
            ILogger? logger = null;
            try
            {
                logger = services.GetService<ILogger<App>>();
                if (services.GetService<IFanControlService>() is not IFanControlAdvanced fans)
                {
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var notices = await fans.RunStartupAsync(cts.Token).ConfigureAwait(false);
                foreach (var notice in notices)
                {
                    StartupNotices.Add("Fans", notice, notice.Contains("could not", StringComparison.OrdinalIgnoreCase) ? Severity.Medium : Severity.Info);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Fan startup hook failed.");
            }
        });
    }

    /// <summary>Reverts fans (per settings) and stops the control loop. Waits a bounded time so exit stays snappy.</summary>
    public static void OnExit(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            if (services.GetService<IFanControlService>() is IFanControlAdvanced fans)
            {
                fans.ShutdownAsync().Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            services.GetService<ILogger<App>>()?.LogWarning(ex, "Fan shutdown hook failed.");
        }
    }
}
