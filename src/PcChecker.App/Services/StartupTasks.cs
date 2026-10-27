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
        await ReconcileTaskManagerReplacementAsync(services, interactive, logger);
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

    /// <summary>
    /// Checks the "Replace Task Manager" setting at start: a stale value pointing at a PC Checker that no longer
    /// exists is cleaned up (the setting owns it), and the user is told when that happens.
    /// </summary>
    private static async Task ReconcileTaskManagerReplacementAsync(IServiceProvider services, bool interactive, ILogger? logger)
    {
        if (AppOptions.Current.IsDemo || AppOptions.Current.IsHeadless)
        {
            return;
        }

        try
        {
            var replacement = services.GetService(typeof(PcChecker.Core.TaskManager.ITaskManagerReplacementService))
                as PcChecker.Core.TaskManager.ITaskManagerReplacementService;
            if (replacement is null)
            {
                return;
            }

            var note = await replacement.ReconcileOnStartAsync(CancellationToken.None);
            if (interactive && !string.IsNullOrWhiteSpace(note)
                && services.GetService(typeof(INotificationService)) is INotificationService notifications)
            {
                notifications.Info("Task Manager", note!);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "The Task Manager replacement setting could not be checked at start.");
        }
    }
}
