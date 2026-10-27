using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Gaming;
using PcChecker.Core.Logging;
using PcChecker.Core.Settings;

namespace PcChecker.App.Services;

/// <summary>
/// Applies game profiles automatically. Listens to the foreground tracker, lets <see cref="ProfileSwitchPlanner"/>
/// decide (2-second debounce, switch back only when the game's process exits), remembers the overlay layout and
/// visibility, fan profile and power plan before the first switch, applies the profile's choices and puts them back
/// when the game closes. Overlay presets reuse <see cref="OverlayPresetCatalog"/> and <see cref="IOverlayService"/>;
/// fans go through <see cref="IFanControlService"/>; power plans through <see cref="IGameProfileService"/> (change
/// guard + journal). Each switch is logged and journaled (module "Gaming"). Screenshot runs only plan, never apply.
/// The apply/revert steps for each part live in <c>GameProfileSwitcher.Steps.cs</c>.
/// </summary>
public sealed partial class GameProfileSwitcher : IDisposable
{
    public const string JournalModule = WindowsGameProfileService.JournalModule;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly ISettingsService _settings;
    private readonly IForegroundAppTracker _tracker;
    private readonly IGameProfileService _profiles;
    private readonly IOverlayService _overlay;
    private readonly IFanControlService _fans;
    private readonly IActionJournal _journal;
    private readonly INotificationService _notifications;
    private readonly ILogger<GameProfileSwitcher> _logger;
    private readonly ProfileSwitchPlanner _planner = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _dryRun;
    private Baseline? _baseline;
    private IDisposable? _exitWatch;
    private Timer? _timer;
    private bool _started;

    public GameProfileSwitcher(ISettingsService settings, IForegroundAppTracker tracker, IGameProfileService profiles, IOverlayService overlay,
        IFanControlService fans, IActionJournal journal, INotificationService notifications, ILogger<GameProfileSwitcher> logger)
    {
        _settings = settings;
        _tracker = tracker;
        _profiles = profiles;
        _overlay = overlay;
        _fans = fans;
        _journal = journal;
        _notifications = notifications;
        _logger = logger;
        _dryRun = AppOptions.Current.ScreenshotsRequested;
    }

    /// <summary>The profile currently applied, or null.</summary>
    public ActiveGameProfile? Active { get; private set; }

    /// <summary>What happened last, in plain words (e.g. "Elden Ring closed: settings put back.").</summary>
    public string? LastMessage { get; private set; }

    /// <summary>Raised (on a background thread) when <see cref="Active"/> or <see cref="LastMessage"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Starts watching the foreground app. Called once after the main window is shown.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        SeedSampleProfiles();
        _tracker.ForegroundChanged += OnForegroundChanged;
        _settings.Changed += OnSettingsChanged;
        try
        {
            _tracker.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foreground tracking could not start; game profiles will not switch automatically.");
        }

        _timer = new Timer(_ => _ = RunAsync(Tick), null, TickInterval, TickInterval);
        if (LiveForeground() is { } current)
        {
            _ = RunAsync(now => _planner.OnForeground(current, GamingSnapshot(), now));
        }

        _logger.LogInformation("Game profile switching started ({Count} profile(s), auto-switch {Auto}).",
            _settings.Current.Gaming.Profiles.Count, _settings.Current.Gaming.AutoSwitch);
    }

    /// <summary>Undoes the active profile (when it reverts on exit) and stops watching. Waits at most a few seconds.</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _timer?.Dispose();
        _timer = null;
        _tracker.ForegroundChanged -= OnForegroundChanged;
        _settings.Changed -= OnSettingsChanged;
        try
        {
            _tracker.Stop();
            // On exit the UI thread is busy closing, so the overlay part is restored in settings only.
            Task.Run(() => RunAsync(_ => _planner.Deactivate(), shuttingDown: true)).Wait(TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Game profile could not be undone on exit.");
        }
    }

    public void Dispose()
    {
        Stop();
        _exitWatch?.Dispose();
        _gate.Dispose();
    }

    private void OnForegroundChanged(object? sender, ForegroundApp app) =>
        _ = RunAsync(now => _planner.OnForeground(app, GamingSnapshot(), now));

    private void OnSettingsChanged(object? sender, AppSettings settings) => _ = RunAsync(now =>
    {
        var gaming = GameProfileRules.Snapshot(settings.Gaming);
        var decision = _planner.OnSettingsChanged(gaming, now);
        return decision.IsNone && _planner.Active is null && LiveForeground() is { } current
            ? _planner.OnForeground(current, gaming, now)
            : decision;
    });

    /// <summary>
    /// A copy of the profile list for the planner. The planner runs on background threads while the Overlay page edits
    /// the live list on the UI thread, so it never enumerates the live list.
    /// </summary>
    private GamingSettings GamingSnapshot() => GameProfileRules.Snapshot(_settings.Current.Gaming);

    /// <summary>Applies a pending game once the debounce has passed, unless it closed in the meantime.</summary>
    private ProfileSwitchDecision Tick(DateTimeOffset now)
    {
        if (_planner.PendingApp is { } pending && !_tracker.IsProcessRunning(pending.Pid))
        {
            _planner.OnProcessExited(pending.Pid, now);
        }

        return _planner.OnTick(now);
    }

    /// <summary>The last foreground app when its process is still running (a closed game must not be re-applied).</summary>
    private ForegroundApp? LiveForeground() =>
        _tracker.Current is { } current && _tracker.IsProcessRunning(current.Pid) ? current : null;

    private void OnProcessExited(int processId)
    {
        _logger.LogDebug("Watched game process {Pid} exited.", processId);
        _ = RunAsync(now => _planner.OnProcessExited(processId, now));
    }

    /// <summary>Asks the planner under the gate and carries out what it decides.</summary>
    private async Task RunAsync(Func<DateTimeOffset, ProfileSwitchDecision> plan, bool shuttingDown = false)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            var decision = plan(DateTimeOffset.Now);
            if (decision.IsNone)
            {
                return;
            }

            _logger.LogInformation("Game profile switch: {Reason}.", decision.Reason);
            if (decision.Exit is { } exit)
            {
                _exitWatch?.Dispose();
                _exitWatch = null;
                await ExitAsync(exit, decision.RevertExit, decision.Reason, shuttingDown).ConfigureAwait(false);
            }

            if (decision.Enter is { } enter && decision.EnterProcessId is { } pid)
            {
                // Watch the game before applying anything: the planner already counts the profile as active, so if
                // applying fails part-way, whatever did change is still put back when the game closes. An exit that
                // fires straight away waits for the gate and is handled after this switch.
                _exitWatch = _tracker.WatchProcessExit(pid, () => OnProcessExited(pid));
                await EnterAsync(enter, pid, decision.EnterExeName ?? enter.ExeName, decision.CaptureBaseline).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game profile switch failed.");
            SetState(Active, "The game profile could not be switched: " + ex.Message);
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }
    }

    private async Task EnterAsync(GameProfile profile, int processId, string exeName, bool captureBaseline)
    {
        if (captureBaseline || _baseline is null)
        {
            _baseline = await CaptureAsync().ConfigureAwait(false);
        }

        var results = new List<string>();
        if (!_dryRun)
        {
            // Each part is applied on its own, so one failing does not skip the others. A part that failed may have
            // changed partly, so it is marked as changed and put back with the rest when the game closes.
            bool presetWanted = profile.OverlayPresetName is { Length: > 0 };
            await TryStepAsync("overlay", () => ApplyOverlayAsync(profile, results), results, b =>
            {
                b.OverlayLayoutChanged |= presetWanted;
                b.OverlayVisibilityChanged = true;
            }).ConfigureAwait(false);
            bool fansWanted = profile.FanProfileName is { Length: > 0 };
            await TryStepAsync("fans", () => ApplyFansAsync(profile, results), results, b => b.FansChanged |= fansWanted).ConfigureAwait(false);
            bool powerWanted = profile.PowerPlanId is { } plan && plan != Guid.Empty;
            await TryStepAsync("power plan", () => ApplyPowerAsync(profile, results), results, b => b.PowerChanged |= powerWanted).ConfigureAwait(false);
        }

        var since = DateTimeOffset.Now;
        string summary = results.Count == 0 ? "nothing to change" : string.Join("; ", results);
        _logger.LogInformation("Game profile \"{Profile}\" applied for {Exe} (pid {Pid}): {Summary}.", profile.Name, exeName, processId, summary);
        if (!_dryRun)
        {
            await RecordAsync($"Applied game profile \"{profile.Name}\"", exeName, _baseline?.Describe(), GameProfileRules.Describe(profile), summary).ConfigureAwait(false);
            _notifications.Info("Game profile", $"\"{profile.Name}\" is active: {summary}.");
        }

        SetState(new ActiveGameProfile(profile.Name, exeName, processId, since), $"\"{profile.Name}\" applied when {exeName} came to the front.");
    }

    private async Task ExitAsync(GameProfile profile, bool revert, string reason, bool shuttingDown)
    {
        var results = new List<string>();
        var baseline = _baseline;
        if (revert && baseline is not null && !_dryRun)
        {
            // One part failing to go back must not stop the others from being put back.
            await TryStepAsync("overlay", () => RevertOverlayAsync(baseline, results, shuttingDown), results, null).ConfigureAwait(false);
            await TryStepAsync("fans", () => RevertFansAsync(baseline, results), results, null).ConfigureAwait(false);
            await TryStepAsync("power plan", () => RevertPowerAsync(baseline, results), results, null).ConfigureAwait(false);
        }

        _baseline = revert ? baseline?.Cleared() : null;
        string summary = !revert ? "settings kept as the profile left them" : results.Count == 0 ? "nothing to put back" : string.Join("; ", results);
        _logger.LogInformation("Game profile \"{Profile}\" ended ({Reason}): {Summary}.", profile.Name, reason, summary);
        if (!_dryRun)
        {
            await RecordAsync($"Ended game profile \"{profile.Name}\"", profile.ExeName, GameProfileRules.Describe(profile), baseline?.Describe(), $"{reason}: {summary}").ConfigureAwait(false);
        }

        SetState(null, $"\"{profile.Name}\" ended because {reason}: {summary}.");
    }

    // ----- Baseline -----

    private async Task<Baseline> CaptureAsync()
    {
        var overlay = _settings.Current.Overlay;
        var layout = OverlayPresetCatalog.Capture(overlay, "Before game");
        FanProfile? fanProfile = null;
        try
        {
            fanProfile = _fans.IsAvailable ? _fans.ActiveProfile : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Active fan profile could not be read.");
        }

        Guid? plan = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            plan = (await _profiles.GetPowerPlansAsync(cts.Token).ConfigureAwait(false)).FirstOrDefault(p => p.IsActive)?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Active power plan could not be read.");
        }

        return new Baseline(layout, _overlay.IsVisible, fanProfile, plan);
    }

    // ----- Helpers -----

    /// <summary>Runs one apply or put-back step; a failure is logged and noted in the summary instead of ending the switch.</summary>
    private async Task TryStepAsync(string part, Func<Task> step, List<string> results, Action<Baseline>? onFailure)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Game profile step failed: {Part}.", part);
            if (onFailure is not null && _baseline is { } baseline)
            {
                onFailure(baseline);
            }

            results.Add($"{part} failed ({ex.Message.TrimEnd('.')})");
        }
    }

    private void SeedSampleProfiles()
    {
        var gaming = _settings.Current.Gaming;
        if (gaming.Profiles.Count > 0 || _profiles.SampleProfiles.Count == 0)
        {
            return;
        }

        GameProfileRules.AddCopies(gaming, _profiles.SampleProfiles);

        // The demo's sample game is always in front, so with auto-switch on its profile would pop up the overlay and
        // switch fans and power plan a moment after launch with no action from the user. Only the screenshot run
        // (which plans but never applies) starts with it on; otherwise the user turns it on to try it.
        gaming.AutoSwitch = _dryRun;

        // Sample data: show the FPS counter in the sample overlay as well.
        var overlay = _settings.Current.Overlay;
        overlay.Metrics = OverlayPresetCatalog.Normalize(overlay.Metrics);
        if (!overlay.Metrics.Any(m => m.Enabled && OverlayMetricFormatter.IsFrameMetric(m.Id)))
        {
            var fps = overlay.Metrics.First(m => m.Id == OverlayMetricIds.Fps);
            fps.Enabled = true;
            fps.Order = -1;
            overlay.Metrics = OverlayPresetCatalog.Normalize(overlay.Metrics);
            _overlay.ApplySettings();
        }

        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settings.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settings could not be saved after a game profile switch.");
        }
    }

    private async Task RecordAsync(string action, string target, string? before, string? after, string outcome)
    {
        try
        {
            await _journal.RecordAsync(ActionRecord.Create(JournalModule, action, target, outcome, before, after, reversible: true,
                undoHint: "Close the game, or turn the profile off on the Overlay page.")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Game profile switch could not be journaled.");
        }
    }

    private void SetState(ActiveGameProfile? active, string message)
    {
        Active = active;
        LastMessage = message;
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A game profile listener failed.");
        }
    }

    /// <summary>The state before the first profile was applied, plus which parts the profile changed.</summary>
    private sealed class Baseline(OverlayPreset overlayLayout, bool overlayWasVisible, FanProfile? fanProfile, Guid? powerPlanId)
    {
        public OverlayPreset OverlayLayout { get; } = overlayLayout;

        public bool OverlayWasVisible { get; } = overlayWasVisible;

        public FanProfile? FanProfile { get; } = fanProfile;

        public Guid? PowerPlanId { get; } = powerPlanId;

        public bool OverlayLayoutChanged { get; set; }

        public bool OverlayVisibilityChanged { get; set; }

        public bool FansChanged { get; set; }

        public bool PowerChanged { get; set; }

        /// <summary>The same remembered state with nothing marked as changed (after everything was put back).</summary>
        public Baseline Cleared() => new(OverlayLayout, OverlayWasVisible, FanProfile, PowerPlanId);

        public string Describe() =>
            $"Overlay {(OverlayWasVisible ? "shown" : "hidden")}; fans {(FanProfile?.Name ?? "automatic")}; power plan "
            + (PowerPlanId is { } id ? PcChecker.Core.Power.PowerPlanParser.DescribeKnown(id) ?? id.ToString() : "unknown");
    }
}
