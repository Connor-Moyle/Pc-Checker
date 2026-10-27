using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.TaskManager;

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
    /// exists is cleaned up (the setting owns it) and the user is told; a value pointing at another PC Checker copy that
    /// still exists is never changed silently: the user is offered to point it at this copy.
    /// </summary>
    private static async Task ReconcileTaskManagerReplacementAsync(IServiceProvider services, bool interactive, ILogger? logger)
    {
        if (AppOptions.Current.IsDemo || AppOptions.Current.IsHeadless)
        {
            return;
        }

        try
        {
            if (services.GetService(typeof(ITaskManagerReplacementService)) is not ITaskManagerReplacementService replacement)
            {
                return;
            }

            var check = await replacement.ReconcileOnStartAsync(CancellationToken.None);
            if (!interactive)
            {
                return;
            }

            var notifications = services.GetService(typeof(INotificationService)) as INotificationService;
            if (!string.IsNullOrWhiteSpace(check.Note))
            {
                notifications?.Info(Loc.T("Task Manager"), check.Note!);
            }

            if (check.OtherCopyPath is { } other)
            {
                bool windowShown = System.Windows.Application.Current?.MainWindow is { IsVisible: true, WindowState: not System.Windows.WindowState.Minimized };
                if (windowShown && services.GetService(typeof(IDialogService)) is IDialogService dialogs && notifications is not null)
                {
                    await TaskManagerReplacementPrompts.OfferRepointAsync(replacement, dialogs, notifications, other);
                }
                else
                {
                    notifications?.Warning(Loc.T("Task Manager"),
                        Loc.F("Task Manager opens another copy of PC Checker: {0}. Fix it in Settings.", other));
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "The Task Manager replacement setting could not be checked at start.");
        }
    }
}
