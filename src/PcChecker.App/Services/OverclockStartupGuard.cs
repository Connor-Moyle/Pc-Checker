using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;

namespace PcChecker.App.Services;

/// <summary>
/// Crash watchdog hooks called by <c>App</c>: on startup, revert an overclock left over from an unclean shutdown;
/// on exit, mark the session clean. Both never throw.
/// </summary>
public static class OverclockStartupGuard
{
    public static async Task CheckAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            var overclock = services.GetRequiredService<IOverclockService>();
            var result = await overclock.CheckUncleanShutdownAndRevertAsync(CancellationToken.None);
            logger.LogInformation("Overclock watchdog: {Message}", result.Message);
            if (result.Message.Contains("reverted", StringComparison.OrdinalIgnoreCase) || result.Message.Contains("uncleanly", StringComparison.OrdinalIgnoreCase) || !result.Success)
            {
                StartupNotices.Add("Overclock reverted", result.Message, result.Success ? Severity.Medium : Severity.High);
                services.GetService<OverclockViewModel>()?.ShowRecoveryNotice(result.Message);
                services.GetService<INotificationService>()?.Warning("Overclock reverted", result.Message);
            }
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
