using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Settings;

namespace PcChecker.App.Services;

/// <summary>
/// Background work that starts right after the main window is shown: the overclock crash watchdog (revert and
/// notify when the previous session did not end cleanly) and the automatic update check. Everything is guarded;
/// nothing here can stop the app from starting.
/// </summary>
public static class StartupTasks
{
    /// <summary>Starts the tasks without waiting for them. Safe to call exactly once from <c>App.OnStartup</c>.</summary>
    public static void Begin(IServiceProvider services, ILogger? logger)
    {
        _ = RunAsync(services, logger);
    }

    private static async Task RunAsync(IServiceProvider services, ILogger? logger)
    {
        bool interactive = !AppOptions.Current.ScreenshotsRequested;
        try
        {
            await CheckOverclockWatchdogAsync(services, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Overclock watchdog check failed.");
        }

        try
        {
            var prompter = ActivatorUtilities.CreateInstance<UpdatePrompter>(services);
            await prompter.RunAsync(interactive, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Update prompter failed.");
        }
    }

    private static async Task CheckOverclockWatchdogAsync(IServiceProvider services, ILogger? logger)
    {
        var settings = services.GetService<ISettingsService>();
        var overclock = services.GetService<IOverclockService>();
        if (settings is null || overclock is null)
        {
            return;
        }

        bool uncleanBefore = !settings.Current.Overclock.LastSessionClean;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await overclock.CheckUncleanShutdownAndRevertAsync(cts.Token);
        if (uncleanBefore)
        {
            if (result.Success)
            {
                StartupNotices.Add("Overclock settings were reset",
                    "The last session did not end cleanly, so the GPU and CPU tuning were put back to their defaults as a precaution. " +
                    "Open the Overclock page to re-apply a profile, or keep the defaults if the PC was unstable. " + result.Message,
                    Severity.Medium);
            }
            else
            {
                StartupNotices.Add("Could not reset overclock settings",
                    "The last session did not end cleanly and the tuning could not be reverted automatically: " + result.Message +
                    " Open the Overclock page and press Reset to defaults.",
                    Severity.High);
            }

            logger?.LogWarning("Unclean shutdown handled: {Message}", result.Message);
        }
        else if (!result.Success)
        {
            logger?.LogDebug("Overclock watchdog reported: {Message}", result.Message);
        }
    }
}
