using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.Core.Common;
using PcChecker.Core.Network;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Optimization;

namespace PcChecker.App.ViewModels;

/// <summary>
/// "Quick improve Wi-Fi": a checklist of recommended changes that differ from the current state, applied one by one,
/// followed by an optional re-test that shows the results before and after side by side.
/// </summary>
public sealed partial class NetworkViewModel
{
    private NetworkSnapshot? _before;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMeasureBaseline))]
    private bool _isQuickImproveOpen;

    [ObservableProperty]
    private string? _quickImproveSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompareOffer), nameof(ShowMeasureBaseline))]
    private bool _quickImproveApplied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison), nameof(ShowCompareOffer))]
    private int _comparisonCount;

    public ObservableCollection<QuickImproveRow> QuickImproveRows { get; } = [];
    public ObservableCollection<ComparisonRow> Comparison { get; } = [];

    public bool HasQuickImproveItems => QuickImproveRows.Count > 0;
    public bool HasComparison => ComparisonCount > 0;
    public bool ShowCompareOffer => QuickImproveApplied && !HasComparison;
    public bool HasBeforeSpeed => _before?.DownloadMbps is not null;

    /// <summary>Offer a "before" speed test while nothing has been applied yet and no speed numbers exist.</summary>
    public bool ShowMeasureBaseline => IsQuickImproveOpen && !QuickImproveApplied && !HasBeforeSpeed;

    [RelayCommand]
    private Task OpenQuickImproveAsync() => RunBusyAsync(async ct =>
    {
        var actions = _tuning.Actions;
        var read = await Task.WhenAll(actions.Select(a => _tuning.GetStateAsync(a.Id, ct)));
        var states = actions.Zip(read, (a, s) => (a, s)).ToList();

        _adapterSettings = await _tuning.GetAdapterSettingsAsync(null, ct);
        var plan = QuickImprovePlanner.Plan(states, _adapterSettings);
        QuickImproveRows.Clear();
        foreach (var item in plan)
        {
            QuickImproveRows.Add(new QuickImproveRow(item));
        }

        QuickImproveSummary = QuickImprovePlanner.Summarize(plan);
        _before = NetworkSnapshot.From(WifiReport, SpeedResult);
        QuickImproveApplied = false;
        Comparison.Clear();
        ComparisonCount = 0;
        IsQuickImproveOpen = true;
        OnPropertyChanged(nameof(HasQuickImproveItems));
        OnPropertyChanged(nameof(HasBeforeSpeed));
        OnPropertyChanged(nameof(ShowMeasureBaseline));
    }, "Finding recommended changes…");

    [RelayCommand]
    private async Task MeasureBaselineAsync()
    {
        if (!IsIdle || QuickImproveApplied)
        {
            return;
        }

        if (await RunSpeedTestCoreAsync())
        {
            // Nothing has been applied yet, so the current Wi-Fi report and this speed test are the "before" state.
            _before = NetworkSnapshot.From(WifiReport, SpeedResult);
            OnPropertyChanged(nameof(HasBeforeSpeed));
            OnPropertyChanged(nameof(ShowMeasureBaseline));
        }
    }

    [RelayCommand]
    private void CloseQuickImprove()
    {
        IsQuickImproveOpen = false;
        QuickImproveApplied = false;
    }

    [RelayCommand]
    private async Task ApplyQuickImproveAsync()
    {
        var selected = QuickImproveRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _notifications.Info("Nothing selected", "Tick the changes you want to apply first.");
            return;
        }

        bool disconnects = selected.Any(r => r.MayDisconnect);
        bool restart = selected.Any(r => r.RequiresRestart);
        string list = string.Join("\n", selected.Select(r => $"• {r.Title}: {r.ChangeText}"));
        bool ok = await _dialogs.ConfirmAsync($"Apply {selected.Count} change{(selected.Count == 1 ? string.Empty : "s")}?",
            list
            + (disconnects ? "\n\nThe Wi-Fi adapter restarts for some of these, so the connection drops for a few seconds each time." : string.Empty)
            + (restart ? "\n\nSome changes finish after a restart." : string.Empty)
            + "\n\nEvery change is recorded and can be undone with Revert in the tuning section.",
            "Apply changes", isDanger: selected.Any(r => r.Item.Risk == RiskLevel.Advanced));
        if (!ok)
        {
            return;
        }

        int succeeded = 0;
        bool reboot = false;
        await RunBusyAsync(async ct =>
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var row = selected[i];
                BusyText = $"Applying {i + 1} of {selected.Count}: {row.Title}…";
                var item = row.Item;
                var result = item.Kind == QuickImproveKind.Action
                    ? await _tuning.ApplyAsync(item.Id, ct)
                    : await _tuning.SetAdapterSettingAsync(item.AdapterName!, item.Key!, item.TargetValue!, ct);
                row.Outcome = (result.Success ? "Done. " : "Failed: ") + result.Message;
                if (result.Success)
                {
                    succeeded++;
                    reboot |= result.RebootRequired;
                }
            }
        }, "Applying changes…", cancellable: false);

        if (reboot)
        {
            IsRebootRequired = true;
        }

        if (succeeded == selected.Count)
        {
            _notifications.Success("Wi-Fi tuned", $"All {succeeded} changes were applied. Re-run the tests to compare.");
        }
        else
        {
            _notifications.Warning("Some changes failed", $"{succeeded} of {selected.Count} changes were applied; see the list for details.");
        }

        QuickImproveApplied = succeeded > 0;
        await RunBusyAsync(LoadTuningCoreAsync, "Reading tuning settings…");
    }

    [RelayCommand]
    private async Task CompareAfterAsync()
    {
        if (!IsIdle)
        {
            return;
        }

        bool analyzed = await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage);
            ApplyAdapters(await _network.GetAdaptersAsync(ct));
            ApplyWifi(await _network.AnalyzeWifiAsync(progress, ct));
        }, "Analyzing Wi-Fi again…");
        if (!analyzed)
        {
            return; // Cancelled or failed: do not go on to the speed test, which uses data.
        }

        bool runSpeed = HasBeforeSpeed || await _dialogs.ConfirmAsync("Run the speed test too?",
            "No speed test was run before the changes, so there are no \"before\" speed numbers to compare with. "
            + "The speed test still shows how fast the connection is now, but it uses data.\n\nChoose Cancel to compare only the Wi-Fi numbers.",
            "Run speed test");
        bool speed = runSpeed && await RunSpeedTestCoreAsync();
        var after = NetworkSnapshot.From(WifiReport, speed ? SpeedResult : null);
        Comparison.Clear();
        foreach (var row in NetworkSnapshot.Compare(_before ?? new NetworkSnapshot(null, null, null, null, null, null, null, null), after))
        {
            Comparison.Add(row);
        }

        ComparisonCount = Comparison.Count;
    }
}

/// <summary>The numbers compared before and after Quick improve.</summary>
public sealed record NetworkSnapshot(int? Signal, double? LinkMbps, string? Band, double? DownloadMbps, double? UploadMbps, double? IdleMs, double? LoadedMs, string? Grade)
{
    public static NetworkSnapshot From(WifiReport? report, SpeedTestResult? speed)
    {
        var current = report?.Current is { IsConnected: true } c ? c : null;
        double? link = current is null ? null : Max(current.ReceiveRateMbps, current.TransmitRateMbps);
        return new NetworkSnapshot(current?.SignalPercent, link, current?.Band, speed?.DownloadMbps, speed?.UploadMbps, speed?.IdleLatencyMs, speed?.LoadedLatencyMs,
            speed is null ? null : speed.Grade);
    }

    /// <summary>Side-by-side rows; "better" ignores differences under 3 % (normal run-to-run noise).</summary>
    public static IReadOnlyList<ComparisonRow> Compare(NetworkSnapshot before, NetworkSnapshot after) =>
    [
        new("Wi-Fi signal", Percent(before.Signal), Percent(after.Signal), Better(before.Signal, after.Signal, higherIsBetter: true)),
        new("Wi-Fi link rate", Mbps(before.LinkMbps), Mbps(after.LinkMbps), Better(before.LinkMbps, after.LinkMbps, higherIsBetter: true)),
        new("Band", before.Band ?? "—", after.Band ?? "—", null),
        new("Download", Mbps(before.DownloadMbps), Mbps(after.DownloadMbps), Better(before.DownloadMbps, after.DownloadMbps, higherIsBetter: true)),
        new("Upload", Mbps(before.UploadMbps), Mbps(after.UploadMbps), Better(before.UploadMbps, after.UploadMbps, higherIsBetter: true)),
        new("Ping (idle)", NetworkUi.Ms(before.IdleMs), NetworkUi.Ms(after.IdleMs), Better(before.IdleMs, after.IdleMs, higherIsBetter: false)),
        new("Ping under load", NetworkUi.Ms(before.LoadedMs), NetworkUi.Ms(after.LoadedMs), Better(before.LoadedMs, after.LoadedMs, higherIsBetter: false)),
        new("Bufferbloat grade", before.Grade ?? "—", after.Grade ?? "—", Better(GradeRank(before.Grade), GradeRank(after.Grade), higherIsBetter: false)),
    ];

    private static bool? Better(double? before, double? after, bool higherIsBetter)
    {
        if (before is not { } b || after is not { } a)
        {
            return null;
        }

        double scale = Math.Max(Math.Abs(b), 1);
        if (Math.Abs(a - b) / scale < 0.03)
        {
            return null;
        }

        return higherIsBetter ? a > b : a < b;
    }

    private static double? GradeRank(string? grade) => grade switch
    {
        "A+" => 0,
        "A" => 1,
        "B" => 2,
        "C" => 3,
        "D" => 4,
        "F" => 5,
        _ => null,
    };

    private static string Percent(int? value) => value is null ? "—" : $"{value} %";

    private static string Mbps(double? value) => value is null ? "—" : $"{NetworkUi.Mbps(value)} Mbps";

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
