using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Converters;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Checkup;
using PcChecker.Core.Common;
using PcChecker.Core.History;
using PcChecker.Core.Shell;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The "at a glance" row of the Dashboard: Windows Update status, the last 24 hours of CPU and GPU temperature from
/// History, the fullest drive from Disk space, and the check-up strip. Each is read on its own with a short time limit,
/// so a slow or missing source only leaves its own tile empty.
/// </summary>
public sealed partial class DashboardViewModel
{
    private static readonly TimeSpan GlanceTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TrendRefreshInterval = TimeSpan.FromMinutes(5);
    private WindowsUpdateStatus? _updateStatus;
    private DateTimeOffset _lastTrendRefresh;
    private int _trendRefreshRunning;

    // Windows Update tile
    [ObservableProperty] private string _updateText = "—";
    [ObservableProperty] private string? _updateDetail;
    [ObservableProperty] private Brush _updateBrush = TemperatureToBrushConverter.Unknown;

    // Temperature trend tile
    [ObservableProperty] private IReadOnlyList<double> _cpuTrend = [];
    [ObservableProperty] private IReadOnlyList<double> _gpuTrend = [];
    [ObservableProperty] private string? _cpuTrendText;
    [ObservableProperty] private string? _gpuTrendText;
    [ObservableProperty] private bool _hasTrend;
    [ObservableProperty] private string _trendNote = Loc.T("Temperatures appear here once History has recorded a while.");

    // Fullest drive tile
    [ObservableProperty] private string _fullestDriveText = "—";
    [ObservableProperty] private string? _fullestDriveDetail;
    [ObservableProperty] private double _fullestDrivePercent;
    [ObservableProperty] private bool _hasFullestDrive;

    // Check-up strip
    [ObservableProperty] private string _checkupText = Loc.T("Runs every check and turns the results into one to-do list, most important first.");
    [ObservableProperty] private bool _hasCheckup;

    [RelayCommand]
    private Task RunCheckupAsync() => _quickActions.RunAsync(QuickActionIds.Checkup);

    [RelayCommand] private void GoToCheckup() => _navigation.Navigate(typeof(CheckupPage));
    [RelayCommand] private void GoToWindowsUpdate() => _navigation.Navigate(typeof(WindowsUpdatePage));
    [RelayCommand] private void GoToHistory() => _navigation.Navigate(typeof(HistoryPage));
    [RelayCommand] private void GoToDiskSpace() => _navigation.Navigate(typeof(DiskSpacePage));

    /// <summary>Reads the three glance sources one after another; each failure is logged and leaves its tile as it was.</summary>
    private async Task RefreshGlanceAsync(CancellationToken ct)
    {
        await Guard(async () => _updateStatus = await _windowsUpdate.GetStatusAsync(ct).WaitAsync(GlanceTimeout, ct), "Windows Update status");
        ApplyUpdates();
        await RefreshTrendAsync(ct);
        await Guard(async () => ApplyFullestDrive(await _diskSpace.GetDrivesAsync(ct).WaitAsync(GlanceTimeout, ct)), "drive list");
        ApplyCheckup(_checkup.Last);
    }

    private async Task RefreshTrendAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _trendRefreshRunning, 1) == 1)
        {
            return;
        }

        try
        {
            _lastTrendRefresh = DateTimeOffset.Now;
            if (!Settings.Current.History.Enabled)
            {
                HasTrend = false;
                TrendNote = Loc.T("History recording is off (Settings → History), so there is no temperature trend.");
                return;
            }

            var now = DateTimeOffset.Now;
            HistoryData? data = null;
            await Guard(async () => data = await _history.LoadAsync(now.AddHours(-24), now, ct).WaitAsync(GlanceTimeout, ct), "temperature history");
            if (data is null)
            {
                return;
            }

            var (cpu, gpu) = await Task.Run(() => (DashboardGlance.Trend(data, HistoryMetrics.CpuTemp), DashboardGlance.Trend(data, HistoryMetrics.GpuTemp)), ct);
            CpuTrend = cpu.Points;
            GpuTrend = gpu.Points;
            CpuTrendText = cpu.Peak is { } cpuPeak ? Loc.F("CPU · peak {0} °C", cpuPeak.ToString("0", CultureInfo.CurrentCulture)) : Loc.T("CPU · no readings");
            GpuTrendText = gpu.Peak is { } gpuPeak ? Loc.F("GPU · peak {0} °C", gpuPeak.ToString("0", CultureInfo.CurrentCulture)) : Loc.T("GPU · no readings");
            HasTrend = cpu.HasData || gpu.HasData;
            TrendNote = Loc.T("Temperatures appear here once History has recorded a while.");
        }
        finally
        {
            Volatile.Write(ref _trendRefreshRunning, 0);
        }
    }

    /// <summary>History writes a sample about once a minute; the sparkline follows every few minutes.</summary>
    private void OnHistoryRecorded(object? sender, EventArgs e)
    {
        if (DateTimeOffset.Now - _lastTrendRefresh < TrendRefreshInterval || !IsInitialized)
        {
            return;
        }

        DispatcherHelper.PostToUi(async () =>
        {
            try
            {
                await RefreshTrendAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Temperature trend refresh failed.");
            }
        });
    }

    /// <summary>
    /// A check-up finished or its fixes were applied: show the new to-do count, and read the update status again,
    /// since installed updates may now wait for a restart.
    /// </summary>
    private void OnCheckupChanged(object? sender, CheckupReport report) => DispatcherHelper.PostToUi(async () =>
    {
        try
        {
            ApplyCheckup(report);
            await Guard(async () => _updateStatus = await _windowsUpdate.GetStatusAsync(CancellationToken.None).WaitAsync(GlanceTimeout), "Windows Update status");
            ApplyUpdates();
            RebuildAlerts();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Check-up glance refresh failed.");
        }
    });

    /// <summary>True when the Windows Update tile already says a restart is pending, so the alerts strip need not repeat it.</summary>
    private bool UpdateTileShowsRestart =>
        DashboardGlance.Updates(_updateStatus, _checkup.Last?.Results.PendingUpdates).State == UpdateGlanceState.RestartPending;

    private void ApplyUpdates()
    {
        var glance = DashboardGlance.Updates(_updateStatus, _checkup.Last?.Results.PendingUpdates);
        string? checkedText = glance.LastCheck is { } check ? Loc.F("Checked {0}", RelativeTimeConverter.Format(check.ToLocalTime(), DateTimeOffset.Now)) : null;
        (UpdateText, UpdateDetail, UpdateBrush) = glance.State switch
        {
            UpdateGlanceState.ServiceOff => (Loc.T("Turned off"), Loc.T("The update service is disabled, so no security fixes arrive."), TemperatureToBrushConverter.Hot),
            UpdateGlanceState.RestartPending => (Loc.T("Restart pending"), Loc.T("Installed updates finish after a restart."), TemperatureToBrushConverter.Warm),
            UpdateGlanceState.UpdatesWaiting => (glance.Waiting == 1 ? Loc.T("1 update waiting") : Loc.F("{0} updates waiting", glance.Waiting), checkedText, TemperatureToBrushConverter.Warm),
            UpdateGlanceState.Paused => (Loc.T("Paused"), glance.PausedUntil is { } until ? Loc.F("Until {0}", until.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)) : null, TemperatureToBrushConverter.Warm),
            UpdateGlanceState.UpToDate => (Loc.T("Up to date"), checkedText, TemperatureToBrushConverter.Normal),
            UpdateGlanceState.CheckedByWindows => (Loc.T("No restart pending"), checkedText is null ? null : checkedText + " " + Loc.T("by Windows"), TemperatureToBrushConverter.Normal),
            _ => ("—", Loc.T("Status not available"), TemperatureToBrushConverter.Unknown),
        };
    }

    private void ApplyFullestDrive(IReadOnlyList<PcChecker.Core.DiskSpace.DriveSpaceInfo> drives)
    {
        if (DashboardGlance.FullestDrive(drives) is not { } drive)
        {
            return;
        }

        double used = DashboardGlance.UsedPercent(drive);
        var name = string.IsNullOrWhiteSpace(drive.Label) ? drive.Letter : $"{drive.Letter} {drive.Label}";
        FullestDrivePercent = used;
        FullestDriveText = Loc.F("{0} · {1} % full", name, used.ToString("0", CultureInfo.CurrentCulture));
        FullestDriveDetail = Loc.F("{0} free of {1}", ByteSize.Format(drive.FreeBytes), ByteSize.Format(drive.TotalBytes));
        DiskBrush = used >= 90 ? TemperatureToBrushConverter.Hot : used >= 80 ? TemperatureToBrushConverter.Warm : TemperatureToBrushConverter.Normal;
        HasFullestDrive = true;
    }

    private void ApplyCheckup(CheckupReport? report)
    {
        HasCheckup = report is not null;
        if (report is null)
        {
            return;
        }

        var when = RelativeTimeConverter.Format(report.Captured, DateTimeOffset.Now);
        CheckupText = report.Items.Count == 0
            ? Loc.F("Last check-up {0}: nothing to do.", when)
            : Loc.F("Last check-up {0}: {1} things to do, {2} can be fixed from the list.", when, report.Items.Count, report.FixableCount);
    }
}
