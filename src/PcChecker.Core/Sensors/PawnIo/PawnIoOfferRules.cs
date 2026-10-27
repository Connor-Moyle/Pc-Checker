using PcChecker.Core.Common;

namespace PcChecker.Core.Sensors.PawnIo;

/// <summary>
/// When PC Checker offers to install the PawnIO sensor driver: as a ticked choice on the first-start welcome, and
/// otherwise once at start-up, again a week after "Not now", never after "Don't ask again". Settings always has the
/// install button, whatever was chosen here.
/// </summary>
public static class PawnIoOfferRules
{
    /// <summary>How long "Not now" keeps the start-up offer away.</summary>
    public static readonly TimeSpan NotNowInterval = TimeSpan.FromDays(7);

    /// <summary>True when the welcome should show the "Install PawnIO" choice (only when it would change something).</summary>
    public static bool ShowOnWelcome(PawnIoStatus status, CommandLineOptions options) =>
        status.NeedsAttention && !options.IsDemo;

    /// <summary>
    /// True when the start-up offer should appear: PawnIO is missing or not running, the user has not said "Don't ask
    /// again", the last "Not now" is at least a week old, and someone is there to answer (no screenshot, scheduled or
    /// demo run).
    /// </summary>
    public static bool ShouldOfferAtStart(PawnIoStatus status, bool dismissed, DateTimeOffset? lastOfferedUtc, DateTimeOffset nowUtc,
        CommandLineOptions options)
    {
        if (!status.NeedsAttention || dismissed || options.IsDemo || options.ScreenshotsRequested || options.IsHeadless)
        {
            return false;
        }

        // A clock set back makes the gap negative: treat that as "asked recently" rather than asking every start.
        return lastOfferedUtc is not { } last || nowUtc - last >= NotNowInterval;
    }

    /// <summary>True when the driver is there but not running, so the action is a repair rather than a first install.</summary>
    public static bool IsRepair(PawnIoStatus status) => status.State == PawnIoState.InstalledNotRunning;
}
