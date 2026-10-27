using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.App.Views.Pages;
using PcChecker.Core.History;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>One choice for how often the history recorder writes a sample.</summary>
public sealed record HistoryIntervalOption(int Seconds, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings → History: record on/off, sample interval and how many days to keep.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool _historyEnabled;
    [ObservableProperty] private HistoryIntervalOption? _historyInterval;
    [ObservableProperty] private double _historyRetentionDays;

    public IReadOnlyList<HistoryIntervalOption> HistoryIntervals { get; } =
    [
        new(30, Loc.T("Every 30 seconds")),
        new(60, Loc.T("Every minute")),
        new(120, Loc.T("Every 2 minutes")),
        new(300, Loc.T("Every 5 minutes")),
        new(900, Loc.T("Every 15 minutes")),
    ];

    private void LoadHistorySettings(AppSettings s)
    {
        var history = s.History ??= new HistorySettings();
        HistoryEnabled = history.Enabled;
        int seconds = HistorySettings.ClampInterval(history.SampleIntervalSeconds);
        HistoryInterval = HistoryIntervals.FirstOrDefault(o => o.Seconds == seconds)
            ?? HistoryIntervals.OrderBy(o => Math.Abs(o.Seconds - seconds)).First();
        HistoryRetentionDays = HistorySettings.ClampRetention(history.RetentionDays);
    }

    partial void OnHistoryEnabledChanged(bool value) => Persist(s => (s.History ??= new HistorySettings()).Enabled = value);

    partial void OnHistoryIntervalChanged(HistoryIntervalOption? value)
    {
        if (value is not null)
        {
            Persist(s => (s.History ??= new HistorySettings()).SampleIntervalSeconds = value.Seconds);
        }
    }

    // Lowering the retention only deletes older days after a grace period, so stepping the number box down by mistake
    // can be undone.
    partial void OnHistoryRetentionDaysChanged(double value) =>
        Persist(s => (s.History ??= new HistorySettings()).SetRetention(value, DateTimeOffset.UtcNow));

    [RelayCommand]
    private void OpenHistoryPage() => _navigation.Navigate(typeof(HistoryPage));
}
