using PcChecker.Core.Common;

namespace PcChecker.Core.Shell;

/// <summary>
/// When the first-start welcome appears. <c>GeneralSettings.WelcomeSeen</c> is null in settings written before the
/// welcome existed, false while a new user has not seen it yet, and true afterwards.
/// </summary>
public static class WelcomeRules
{
    /// <summary>
    /// Fills in a missing flag at startup: a settings file that already existed belongs to someone who has used PC
    /// Checker before (they skip the welcome); no file means a first start (they get it).
    /// </summary>
    public static bool ResolveSeen(bool? stored, bool settingsFileExisted) => stored ?? settingsFileExisted;

    /// <summary>
    /// True when the window should greet the user: only while the flag is false, and never in a screenshot run or a
    /// <c>--scheduled</c> run (nobody is there to answer).
    /// </summary>
    public static bool ShouldShow(bool? welcomeSeen, CommandLineOptions options) =>
        welcomeSeen == false && !options.ScreenshotsRequested && !options.IsHeadless;
}
