using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Demo going back for the sample Windows 11 24H2 desktop, upgraded from 23H2 three days ago: last month's cumulative
/// update (removable with DISM), its servicing stack update (permanent), an older cumulative update a newer one
/// replaced, a .NET update and an ordinary update (removable with wusa); the failed Windows 11 25H2 upgrade hidden;
/// and a 10-day window to go back with 7 days left. Everything is kept in memory. Pure .NET.
/// </summary>
public sealed class DemoUpdateRollbackService : IUpdateRollbackService
{
    private const string RollupPackage = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.6584.1.7";

    private readonly object _gate = new();
    private readonly List<InstalledUpdate> _installed;
    private readonly List<HiddenUpdate> _hidden;
    private readonly DateTimeOffset _upgradedOn;
    private readonly DemoWindowsUpdateService? _updates;
    private int _windowDays = RollbackRules.DefaultWindowDays;
    private bool _goingBack;

    public DemoUpdateRollbackService()
        : this(DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Shares hidden updates with the demo Windows Update service, so a hidden update leaves its next check.</summary>
    public DemoUpdateRollbackService(IWindowsUpdateService updates)
        : this(DateTimeOffset.UtcNow)
    {
        _updates = updates as DemoWindowsUpdateService;
    }

    /// <summary>Builds the sample data relative to <paramref name="now"/> (tests pass a fixed time).</summary>
    public DemoUpdateRollbackService(DateTimeOffset now)
    {
        _upgradedOn = now.AddDays(-3).AddHours(-2);
        _installed = SampleInstalled(now);
        _hidden =
        [
            new("demo-hidden-25h2", "Windows 11, version 25H2", null, UpdateClassification.Feature),
        ];
    }

    public async Task<InstalledUpdateList> GetInstalledUpdatesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        lock (_gate)
        {
            return new InstalledUpdateList(_installed.ToList(), [], DateTimeOffset.Now);
        }
    }

    public async Task<OperationResult> UninstallAsync(InstalledUpdate update, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var plan = InstalledUpdateRules.PlanUninstall(update);
        if (!plan.CanUninstall)
        {
            return OperationResult.Fail(plan.Explanation);
        }

        progress?.Report(new ProgressUpdate($"Removing {update.Kb}", null, "This can take several minutes. Do not turn off the PC."));
        await DemoData.DelayAsync(CancellationToken.None, 900, 1500).ConfigureAwait(false);
        lock (_gate)
        {
            if (_installed.RemoveAll(u => u.Kb == update.Kb) == 0)
            {
                var notInstalled = UpdateToolExitCodes.Map(UpdateToolExitCodes.WusaNotInstalled, "wusa");
                return OperationResult.Fail($"{update.Kb} was not removed: {notInstalled.Message} (code {notInstalled.CodeText})");
            }
        }

        return OperationResult.Ok($"{update.Kb} was removed (demo). Restart Windows to finish.", rebootRequired: true);
    }

    public async Task<HiddenUpdateList> GetHiddenUpdatesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        lock (_gate)
        {
            return new HiddenUpdateList(_hidden.OrderBy(h => h.Title, StringComparer.CurrentCultureIgnoreCase).ToList(), []);
        }
    }

    public async Task<OperationResult> HideAsync(UpdateHideTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        lock (_gate)
        {
            var id = target.UpdateId ?? "demo-" + (target.Kb ?? target.Title);
            bool already = _hidden.Any(h => h.Id == id || (target.Kb is not null && h.Kb == target.Kb));
            var decision = HiddenUpdateRules.Decide(true, already, false);
            if (decision == HideDecision.Change)
            {
                _hidden.Add(new HiddenUpdate(id, target.Title, target.Kb, UpdateClassifier.Classify([], target.Title)));
                _updates?.SetHidden(id, true);
            }

            return OperationResult.Ok(HiddenUpdateRules.Message(decision, true, target.Title) + " (demo)");
        }
    }

    public async Task<OperationResult> UnhideAsync(HiddenUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        lock (_gate)
        {
            bool hidden = _hidden.RemoveAll(h => h.Id == update.Id) > 0;
            _updates?.SetHidden(update.Id, false);
            var decision = HiddenUpdateRules.Decide(false, hidden, false);
            return OperationResult.Ok(HiddenUpdateRules.Message(decision, false, update.Title) + " (demo)");
        }
    }

    public async Task<RollbackWindow> GetRollbackWindowAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 400).ConfigureAwait(false);
        return Window(DateTimeOffset.UtcNow);
    }

    public async Task<OperationResult> GoBackAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var window = Window(DateTimeOffset.UtcNow);
        if (window.State != RollbackState.Available)
        {
            return OperationResult.Fail(window.Message);
        }

        progress?.Report(new ProgressUpdate("Preparing to go back to the previous version of Windows", null, "This can take a few minutes."));
        await DemoData.DelayAsync(CancellationToken.None, 900, 1500).ConfigureAwait(false);
        lock (_gate)
        {
            _goingBack = true;
        }

        return OperationResult.Ok("Windows is ready to go back to the previous version (demo: nothing was changed). It does so while the PC restarts.", rebootRequired: true);
    }

    public async Task<OperationResult> SetRollbackWindowAsync(int days, CancellationToken cancellationToken)
    {
        var window = Window(DateTimeOffset.UtcNow);
        if (window.State != RollbackState.Available)
        {
            return OperationResult.Fail(window.Message);
        }

        if (RollbackRules.ValidateExtension(days, window.DaysSinceUpgrade) is { } problem)
        {
            return OperationResult.Fail(problem);
        }

        await DemoData.DelayAsync(cancellationToken, 300, 600).ConfigureAwait(false);
        lock (_gate)
        {
            _windowDays = days;
        }

        int left = RollbackRules.DaysLeft(days, window.DaysSinceUpgrade ?? 0);
        return OperationResult.Ok($"Going back is now possible for {days} days after the upgrade ({left} days left). (demo)");
    }

    /// <summary>The demo window at <paramref name="now"/>.</summary>
    public RollbackWindow Window(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _goingBack
                ? RollbackRules.Evaluate(_windowDays, false, _upgradedOn, true, now, ["Windows goes back to the previous version at the next restart (demo)."])
                : RollbackRules.Evaluate(_windowDays, false, _upgradedOn, true, now);
        }
    }

    private static List<InstalledUpdate> SampleInstalled(DateTimeOffset now)
    {
        var lastMonth = now.AddMonths(-1);
        var twoMonths = now.AddMonths(-2);
        string Month(DateTimeOffset d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var patch = new DateTimeOffset(lastMonth.Year, lastMonth.Month, 10, 3, 0, 0, TimeSpan.Zero);
        var olderPatch = new DateTimeOffset(twoMonths.Year, twoMonths.Month, 13, 3, 0, 0, TimeSpan.Zero);
        return
        [
            new("KB5063708", $"{Month(lastMonth)} Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5063708) (26100.6584)",
                patch, InstalledUpdateType.Cumulative)
            {
                UpdateId = "8a2c1f60-4d1e-4c4b-9a64-5f0e2d7b9c31",
                IsWindowsCumulative = true,
                PackageName = RollupPackage,
            },
            new("KB5065381", $"{Month(lastMonth)} Servicing Stack Update for Windows 11 Version 24H2 for x64-based Systems (KB5065381)",
                patch.AddMinutes(-10), InstalledUpdateType.ServicingStack),
            new("KB5063004", $"{Month(lastMonth)} Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 24H2 for x64 (KB5063004)",
                patch.AddMinutes(-20), InstalledUpdateType.Cumulative)
            {
                UpdateId = "3f9b7c21-6a0d-4e88-b1c5-2e7d4a9f0b16",
            },
            new("KB5062660", $"{Month(olderPatch)} Update for Windows 11 Version 24H2 for x64-based Systems (KB5062660)",
                olderPatch.AddDays(9), InstalledUpdateType.Update)
            {
                UpdateId = "c41d0e8b-2f73-4b6a-8e19-7d5a3c2b1e04",
            },
            new("KB5060581", $"{Month(olderPatch)} Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5060581) (26100.4349)",
                olderPatch, InstalledUpdateType.Cumulative)
            {
                IsWindowsCumulative = true,
                IsSuperseded = true,
            },
        ];
    }
}
