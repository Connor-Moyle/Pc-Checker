using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PcChecker.App.Services;

/// <summary>
/// Background work that starts right after the main window is shown: the automatic update check. The overclock
/// crash watchdog has exactly one caller, <see cref="OverclockStartupGuard"/>, so the reset never runs twice.
/// Everything is guarded; nothing here can stop the app from starting.
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
            var prompter = ActivatorUtilities.CreateInstance<UpdatePrompter>(services);
            await prompter.RunAsync(interactive, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Update prompter failed.");
        }
    }
}
