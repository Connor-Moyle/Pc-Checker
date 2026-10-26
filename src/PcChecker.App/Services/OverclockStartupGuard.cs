using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Settings;

namespace PcChecker.App.Services;

/// <summary>
/// Crash watchdog hooks called by <c>App</c>: on startup, revert an overclock left over from an unclean shutdown
/// (this is the only place that runs the check, so the GPU is reset once and the user is told once); on exit, mark
/// the session clean. Both never throw.
/// </summary>
public static class OverclockStartupGuard
{
    public static async Task CheckAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            var overclock = services.GetRequiredService<IOverclockService>();
            var settings = services.GetService<ISettingsService>();
            bool uncleanBefore = settings is not null && !settings.Current.Overclock.LastSessionClean;
            var result = await overclock.CheckUncleanShutdownAndRevertAsync(CancellationToken.None);
            logger.LogInformation("Overclock watchdog: {Message}", result.Message);
            bool reverted = result.Message.Contains("reverted", StringComparison.OrdinalIgnoreCase) || result.Message.Contains("uncleanly", StringComparison.OrdinalIgnoreCase);
            if (!uncleanBefore && !reverted && result.Success)
            {
                return;
            }

            string title = result.Success ? "Overclock settings were reset" : "Could not reset overclock settings";
            string text = result.Success
                ? "The last session did not end cleanly, so the GPU and CPU tuning were put back to their defaults as a precaution. " +
                  "Open the Overclock page to re-apply a profile, or keep the defaults if the PC was unstable. " + result.Message
                : "The last session did not end cleanly and the tuning could not be reverted automatically: " + result.Message +
                  " Open the Overclock page and press Reset to defaults.";
            logger.LogWarning("Unclean shutdown handled: {Message}", result.Message);
            StartupNotices.Add(title, text, result.Success ? Severity.Medium : Severity.High);
            services.GetService<OverclockViewModel>()?.ShowRecoveryNotice(text);
            services.GetService<INotificationService>()?.Warning(title, result.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Overclock watchdog check failed.");
        }
    }

    public static void MarkClean(IServiceProvider? services)
    {
        try
        {
            services?.GetService<IOverclockService>()?.MarkSessionCleanAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown must not fail because of the watchdog file.
        }
    }
}
