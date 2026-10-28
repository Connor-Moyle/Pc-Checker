using PcChecker.Core.Localization;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.GameReady;

/// <summary>
/// Turns what one check read (<see cref="GameReadyFacts"/>) into the game-ready list: one item per thing that commonly
/// costs gamers performance, with its status, why it matters, what was found and the fix. A value that could not be
/// read becomes "not checked" (with where to look), never a problem. Items come back biggest wins first. Pure; the
/// text goes through <paramref name="tr"/> so the page can show it translated.
/// </summary>
public static partial class GameReadyRules
{
    /// <summary>Optimize page action ids the fixes reuse.</summary>
    public const string GameModeAction = "gamemode.enable";
    public const string GameBarAction = "gamebar.disable";
    public const string HighPerformanceAction = "power.highperformance";
    public const string HagsAction = Latency.LatencyActionIds.HagsOn;
    public const string PointerPrecisionAction = Latency.LatencyActionIds.PointerPrecisionOff;

    /// <summary>Navigation key of the RAM tuning page (falls back to the Memory page where it does not exist).</summary>
    public const string RamTuningPage = "ram-tuning";

    public static GameReadyReport Evaluate(GameReadyFacts facts, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(tr);
        var items = new List<GameReadyItem>();
        Displays(facts, tr, items);
        DirectXSettings(facts, tr, items);
        WindowsGaming(facts, tr, items);
        Power(facts, tr, items);
        Graphics(facts, tr, items);
        Memory(facts, tr, items);
        BackgroundApps(facts, tr, items);
        Mouse(facts, tr, items);
        Storage(facts, tr, items);
        Updates(facts, tr, items);
        var ordered = Order(items);
        return new GameReadyReport(ordered, Score(ordered), facts.Now, facts.Warnings);
    }

    // ----- scoring and order -----------------------------------------------------------------------------------------

    /// <summary>Points an item costs: problems more than suggestions, high impact more than low. Others cost nothing.</summary>
    public static int Penalty(GameReadyItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (item.Status, item.Impact) switch
        {
            (GameReadyStatus.Problem, GameReadyImpact.High) => 15,
            (GameReadyStatus.Problem, GameReadyImpact.Medium) => 10,
            (GameReadyStatus.Problem, _) => 6,
            (GameReadyStatus.Suggestion, GameReadyImpact.High) => 8,
            (GameReadyStatus.Suggestion, GameReadyImpact.Medium) => 5,
            (GameReadyStatus.Suggestion, _) => 2,
            _ => 0,
        };
    }

    /// <summary>100 minus every item's penalty, never below 0.</summary>
    public static int Score(IEnumerable<GameReadyItem> items) => Math.Clamp(100 - items.Sum(Penalty), 0, 100);

    /// <summary>
    /// Biggest wins first: by penalty, then problems before suggestions, then items with a one-click change before those
    /// that only explain, then "not checked", information and good items, each group in area order.
    /// </summary>
    public static IReadOnlyList<GameReadyItem> Order(IEnumerable<GameReadyItem> items) =>
        items.OrderByDescending(Penalty)
            .ThenByDescending(i => (int)i.Status)
            .ThenByDescending(i => i.Fix?.ChangesSystem == true)
            .ThenBy(i => (int)i.Area)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>"Excellent", "Good", "Room to improve" or "Needs attention" for a score.</summary>
    public static string Grade(int score, Translator tr) => score switch
    {
        >= 90 => tr.T("Excellent"),
        >= 75 => tr.T("Good"),
        >= 50 => tr.T("Room to improve"),
        _ => tr.T("Needs attention"),
    };

    // ----- displays -------------------------------------------------------------------------------------------------

    private static void Displays(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        if (facts.Displays.Count == 0)
        {
            items.Add(new("display-refresh", GameReadyArea.Display, GameReadyStatus.NotChecked, GameReadyImpact.None,
                tr.T("Refresh rate could not be checked"), RefreshWhy(tr),
                tr.T("Windows did not list any monitors. Check Settings → Display → Advanced display."),
                new GameReadyFix(GameReadyFixKind.OpenLink, tr.T("Open display settings"), "ms-settings:display-advanced")));
            return;
        }

        var good = new List<string>();
        foreach (var display in facts.Displays.OrderByDescending(d => d.IsPrimary))
        {
            var best = RefreshRateRules.BestAtCurrentResolution(display.Current, display.Modes);
            if (best is null)
            {
                good.Add(tr.F("{0}: {1} Hz", display.Name, display.Current.RefreshHz));
                continue;
            }

            var impact = RefreshRateRules.Impact(display.Current.RefreshHz, best.RefreshHz);
            items.Add(new("display-refresh-" + Slug(display.DeviceName), GameReadyArea.Display,
                impact >= GameReadyImpact.Medium ? GameReadyStatus.Problem : GameReadyStatus.Suggestion, impact,
                tr.F("{0} runs at {1} Hz but can do {2} Hz", display.Name, display.Current.RefreshHz, best.RefreshHz),
                RefreshWhy(tr),
                tr.F("Now {0} × {1} at {2} Hz. The fastest mode at this resolution is {3} Hz. You get 15 seconds to keep or undo the change.",
                    display.Current.Width, display.Current.Height, display.Current.RefreshHz, best.RefreshHz),
                new GameReadyFix(GameReadyFixKind.RefreshRate, tr.F("Switch to {0} Hz", best.RefreshHz), display.DeviceName, Safe: true)
                {
                    Mode = best,
                    PreviousMode = display.Current,
                }));
        }

        if (good.Count == facts.Displays.Count)
        {
            items.Add(new("display-refresh", GameReadyArea.Display, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("Monitors run at their fastest refresh rate"), RefreshWhy(tr), string.Join(", ", good)));
        }

        if (facts.Displays.FirstOrDefault(d => d.HdrSupported) is { } hdr)
        {
            items.Add(new("display-hdr", GameReadyArea.Display, GameReadyStatus.Info, GameReadyImpact.None,
                hdr.HdrEnabled ? tr.F("HDR is on for {0}", hdr.Name) : tr.F("{0} supports HDR", hdr.Name),
                tr.T("Auto HDR adds HDR to DirectX 11 and 12 games that were made without it. It looks brighter on a good HDR monitor; turn it off if colours look washed out."),
                tr.T("HDR and Auto HDR are in Settings → Display → HDR."),
                new GameReadyFix(GameReadyFixKind.OpenLink, tr.T("Open HDR settings"), "ms-settings:display-hdr")));
        }
    }

    private static string RefreshWhy(Translator tr) =>
        tr.T("A faster refresh rate shows more frames each second, so motion looks smoother and the game reacts sooner. Windows often falls back to 60 Hz after a driver update or a new cable.");

    // ----- DirectX graphics settings ----------------------------------------------------------------------------------

    private static void DirectXSettings(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        string why = tr.T("Lets older windowed and borderless games use the faster, lower-lag way of showing frames that full-screen games use. Needed for Auto HDR and variable refresh in windowed games.");
        if (!facts.DirectXReadable)
        {
            items.Add(new("windowed-optimizations", GameReadyArea.Windows, GameReadyStatus.NotChecked, GameReadyImpact.None,
                tr.T("Optimizations for windowed games could not be checked"), why,
                tr.T("See Settings → Display → Graphics → Change default graphics settings."),
                new GameReadyFix(GameReadyFixKind.OpenLink, tr.T("Open graphics settings"), "ms-settings:display-advancedgraphics")));
        }
        else
        {
            var fix = new GameReadyFix(GameReadyFixKind.DirectXSetting, tr.T("Turn on"), DirectXSettingsText.WindowedOptimizations, Safe: true);
            switch (DirectXSettingsText.Switch(facts.DirectXGlobalSettings, DirectXSettingsText.WindowedOptimizations))
            {
                case true:
                    items.Add(new("windowed-optimizations", GameReadyArea.Windows, GameReadyStatus.Good, GameReadyImpact.None,
                        tr.T("Optimizations for windowed games are on"), why, tr.T("On")));
                    break;
                case false:
                    items.Add(new("windowed-optimizations", GameReadyArea.Windows, GameReadyStatus.Suggestion, GameReadyImpact.Low,
                        tr.T("Optimizations for windowed games are off"), why, tr.T("Turned off in Settings → Display → Graphics."), fix));
                    break;
                default:
                    items.Add(new("windowed-optimizations", GameReadyArea.Windows, GameReadyStatus.Info, GameReadyImpact.None,
                        tr.T("Optimizations for windowed games: Windows default"), why,
                        tr.T("Not set, so Windows decides (on in recent versions of Windows 11). Turning it on makes sure."), fix));
                    break;
            }
        }

        string vrrWhy = tr.T("With G-SYNC or FreeSync the monitor waits for each frame, which removes tearing and stutter without the input lag of V-Sync.");
        if (DirectXSettingsText.Switch(facts.DirectXGlobalSettings, DirectXSettingsText.VariableRefreshRate) == false)
        {
            items.Add(new("variable-refresh", GameReadyArea.Display, GameReadyStatus.Suggestion, GameReadyImpact.Low,
                tr.T("Variable refresh rate is off in Windows"), vrrWhy,
                tr.T("Windows' own switch brings variable refresh to games that do not support it. Turn on G-SYNC or FreeSync in the graphics control panel and in the monitor's menu too."),
                new GameReadyFix(GameReadyFixKind.DirectXSetting, tr.T("Turn on"), DirectXSettingsText.VariableRefreshRate, Safe: true)));
        }
        else
        {
            items.Add(new("variable-refresh", GameReadyArea.Display, GameReadyStatus.Info, GameReadyImpact.None,
                tr.T("Check that G-SYNC or FreeSync is on"), vrrWhy,
                tr.T("Windows cannot tell whether it is on: it is set in the graphics control panel and in the monitor's own menu."),
                new GameReadyFix(GameReadyFixKind.GpuControlPanel, tr.T("Open graphics control panel"))));
        }
    }

    // ----- Windows gaming settings ------------------------------------------------------------------------------------

    private static void WindowsGaming(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        bool frameGeneration = facts.Gpus.Any(g => !g.IsIntegrated && SupportsFrameGeneration(g.Name));
        string hagsWhy = frameGeneration
            ? tr.T("Lets the graphics card schedule its own work, which can lower input lag. DLSS Frame Generation on GeForce RTX 40 and 50 cards needs it.")
            : tr.T("Lets the graphics card schedule its own work, which can lower input lag a little on recent cards.");
        items.Add(facts.HagsOn switch
        {
            true => new("hags", GameReadyArea.Windows, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("Hardware-accelerated GPU scheduling is on"), hagsWhy, tr.T("On")),
            false => new("hags", GameReadyArea.Windows, GameReadyStatus.Suggestion, frameGeneration ? GameReadyImpact.Medium : GameReadyImpact.Low,
                tr.T("Hardware-accelerated GPU scheduling is off"), hagsWhy,
                tr.T("Needs a restart. If a game stutters afterwards, turn it off again on the Latency page."),
                new GameReadyFix(GameReadyFixKind.OptimizationAction, tr.T("Turn on"), HagsAction, Safe: false, RequiresRestart: true)),
            null => new("hags", GameReadyArea.Windows, GameReadyStatus.NotChecked, GameReadyImpact.None,
                tr.T("GPU scheduling could not be checked"), hagsWhy, tr.T("See the Latency page."),
                new GameReadyFix(GameReadyFixKind.OpenPage, tr.T("Open Latency"), "latency")),
        });

        string modeWhy = tr.T("Game Mode tells Windows to hold back updates and background work while a game runs and to give the game more CPU time.");
        items.Add(facts.GameModeOn switch
        {
            true => new("game-mode", GameReadyArea.Windows, GameReadyStatus.Good, GameReadyImpact.None, tr.T("Game Mode is on"), modeWhy, tr.T("On")),
            false => new("game-mode", GameReadyArea.Windows, GameReadyStatus.Suggestion, GameReadyImpact.Low, tr.T("Game Mode is off"), modeWhy,
                tr.T("Turned off in Settings → Gaming → Game Mode."),
                new GameReadyFix(GameReadyFixKind.OptimizationAction, tr.T("Turn on"), GameModeAction, Safe: true)),
            null => new("game-mode", GameReadyArea.Windows, GameReadyStatus.NotChecked, GameReadyImpact.None, tr.T("Game Mode could not be checked"), modeWhy,
                tr.T("See Settings → Gaming → Game Mode."), new GameReadyFix(GameReadyFixKind.OpenLink, tr.T("Open Game Mode settings"), "ms-settings:gaming-gamemode")),
        });

        string dvrWhy = tr.T("Background recording keeps the last minutes of every game so you can save a clip later. While a game runs it records all the time, which costs a few frames per second and some video memory.");
        items.Add(facts.GameDvrOn switch
        {
            false => new("game-dvr", GameReadyArea.Windows, GameReadyStatus.Good, GameReadyImpact.None, tr.T("Xbox Game Bar background recording is off"), dvrWhy, tr.T("Off")),
            true => new("game-dvr", GameReadyArea.Windows, GameReadyStatus.Suggestion, GameReadyImpact.Medium, tr.T("Xbox Game Bar background recording is on"), dvrWhy,
                tr.T("Turning it off turns off Game Bar captures, including Win+Alt+R. Recorders from NVIDIA, AMD or OBS still work."),
                new GameReadyFix(GameReadyFixKind.OptimizationAction, tr.T("Turn off"), GameBarAction, Safe: true)),
            null => new("game-dvr", GameReadyArea.Windows, GameReadyStatus.NotChecked, GameReadyImpact.None, tr.T("Game Bar recording could not be checked"), dvrWhy,
                tr.T("See Settings → Gaming → Captures."), new GameReadyFix(GameReadyFixKind.OpenLink, tr.T("Open capture settings"), "ms-settings:gaming-gamedvr")),
        });
    }

    /// <summary>
    /// Background recording is on only while Game Bar captures are allowed and "Record what happened" is switched on.
    /// <paramref name="capturesOff"/> is the Optimize page's Game Bar action state (true when captures are turned off);
    /// <paramref name="recordWhatHappened"/> is HistoricalCaptureEnabled (off by default).
    /// </summary>
    public static bool? BackgroundRecordingOn(bool? capturesOff, bool? recordWhatHappened) =>
        capturesOff == true ? false : recordWhatHappened;

    /// <summary>True for cards whose frame generation needs GPU scheduling (GeForce RTX 40/50 series).</summary>
    public static bool SupportsFrameGeneration(string? gpuName) =>
        gpuName is not null && (gpuName.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("RTX 50", StringComparison.OrdinalIgnoreCase));

    // ----- power -------------------------------------------------------------------------------------------------------

    private static void Power(GameReadyFacts facts, Translator tr, List<GameReadyItem> items)
    {
        int before = items.Count;
        if (facts.HasBattery && facts.OnBattery == true)
        {
            items.Add(new("power-battery", GameReadyArea.Power, GameReadyStatus.Problem, GameReadyImpact.High,
                tr.T("The laptop is running on battery"),
                tr.T("On battery, laptops cut the power of the processor and graphics card, often to half or less. Games run much slower."),
                tr.T("Plug in the charger before you play.")));
        }

        if (facts.BatterySaverOn == true)
        {
            items.Add(new("power-saver", GameReadyArea.Power, GameReadyStatus.Problem, GameReadyImpact.Medium,
                tr.T("Energy saver is on"),
                tr.T("Energy saver lowers the processor's speed and limits background work to make the battery last."),
                tr.T("Turn it off in Settings → System → Power & battery."),
                new GameReadyFix(GameReadyFixKind.OpenLink, tr.T("Open power settings"), "ms-settings:batterysaver")));
        }

        if (facts.PowerMode == PowerModeOverlay.BestPowerEfficiency)
        {
            items.Add(new("power-mode", GameReadyArea.Power, GameReadyStatus.Suggestion, GameReadyImpact.Medium,
                tr.T("Power mode is set to Best power efficiency"),
                tr.T("This power mode keeps the processor at lower speeds. Balanced or Best performance lets it boost while you play."),
                tr.T("Change it in Settings → System → Power & battery → Power mode."),
                new GameReadyFix(GameReadyFixKind.OpenLink, tr.T("Open power mode"), "ms-settings:powersleep")));
        }

        if (!facts.HasBattery && facts.HighPerformancePlan == false)
        {
            items.Add(new("power-plan", GameReadyArea.Power, GameReadyStatus.Suggestion, GameReadyImpact.Low,
                tr.T("The High performance power plan is not active"),
                tr.T("High performance keeps the processor ready to boost instead of waking it from power saving between frames. On a desktop it costs only a little electricity."),
                facts.PowerPlanText ?? tr.T("Another power plan is active"),
                new GameReadyFix(GameReadyFixKind.OptimizationAction, tr.T("Use High performance"), HighPerformanceAction, Safe: true)));
        }

        if (items.Count == before)
        {
            items.Add(new("power", GameReadyArea.Power, GameReadyStatus.Good, GameReadyImpact.None,
                tr.T("Power settings are ready for games"),
                tr.T("Battery saving modes slow the processor and graphics card down."),
                facts.PowerPlanText ?? (facts.HasBattery ? tr.T("Plugged in") : tr.T("Desktop power plan"))));
        }
    }

    /// <summary>A short, stable id part from a device name ("\\.\DISPLAY1" → "display1").</summary>
    public static string Slug(string? text) =>
        new((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Optimize page actions the safe fixes use (for tests and the confirmation text).</summary>
    public static IReadOnlyList<string> SafeOptimizationActions { get; } = [GameModeAction, GameBarAction, HighPerformanceAction, PointerPrecisionAction];

    /// <summary>True when <paramref name="actionId"/> is a reversible Safe action in <paramref name="catalog"/>.</summary>
    public static bool IsSafeAction(string actionId, IEnumerable<OptimizationAction> catalog) =>
        catalog.Any(a => a.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase) && a.Reversible && a.Risk == RiskLevel.Safe);
}
