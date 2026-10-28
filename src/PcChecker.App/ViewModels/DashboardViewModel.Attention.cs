using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Converters;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Checkup;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Sensors.PawnIo;
using PcChecker.Core.Shell;
using PcChecker.Core.Performance;
using PcChecker.Core.Startup;

namespace PcChecker.App.ViewModels;

/// <summary>One line of the "What needs attention" card: the recommendation and its one button.</summary>
public sealed record AttentionRow(string Id, Severity Severity, string Title, string Detail, string ActionText, IRelayCommand Command);

/// <summary>
/// The Dashboard's "What needs attention" card: up to five plain-language recommendations ranked by
/// <see cref="AttentionRules"/> from what the Dashboard already read (updates, drives, the last check-up) plus the
/// startup list and the PawnIO status. Each has one button that opens the right page or starts the right action.
/// </summary>
public sealed partial class DashboardViewModel
{
    private readonly IStartupService _startup;
    private readonly PawnIoInstallFlow _pawnIo;
    private IReadOnlyList<DriveSpaceInfo> _attentionDrives = [];
    private IReadOnlyList<StartupItem>? _liveStartupItems;
    private DateTimeOffset _attentionReadAt;
    private bool _attentionRefreshRunning;
    private PawnIoState? _pawnIoState;
    private readonly Core.Crashes.ICrashService _crashes;
    private Core.Crashes.CrashGlance? _crashGlance;

    [ObservableProperty] private bool _hasAttentionItems;
    [ObservableProperty] private string _attentionAllClearText = Loc.T("Nothing to do right now.");

    public ObservableCollection<AttentionRow> AttentionItems { get; } = [];

    /// <summary>The one-time "You're in Simple mode" hint (shared with Settings).</summary>
    public SimpleModeHintViewModel SimpleModeHint { get; }

    /// <summary>
    /// Reads the sources only this card needs (the live startup list, PawnIO), then rebuilds it. The startup list is
    /// always read live, so apps turned off on the Startup page drop out of the card even when a check-up exists.
    /// </summary>
    private async Task RefreshAttentionAsync(CancellationToken ct)
    {
        _liveStartupItems = null;
        await Guard(async () => _liveStartupItems = await _startup.GetStartupItemsAsync(ct).WaitAsync(GlanceTimeout, ct), "startup list");
        await Guard(async () => _pawnIoState = await Task.Run(() => _pawnIo.GetStatus().State, ct).WaitAsync(GlanceTimeout, ct), "PawnIO status");
        await Guard(async () => _crashGlance = await _crashes.GetGlanceAsync(ct).WaitAsync(GlanceTimeout, ct), "recent crashes");
        _attentionReadAt = DateTimeOffset.Now;
        RebuildAttention();
    }

    /// <summary>
    /// The Dashboard is shown again (back from Startup, Cleanup, Fans…): quietly read the drives, the startup list and
    /// PawnIO again so the card reflects what the user just did. Skipped during the first load, while busy, and when
    /// the last read was a moment ago.
    /// </summary>
    public void OnPageShown()
    {
        if (!IsInitialized || IsBusy || _attentionRefreshRunning || DateTimeOffset.Now - _attentionReadAt < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _attentionRefreshRunning = true;
        DispatcherHelper.PostToUi(async () =>
        {
            try
            {
                await Guard(async () => ApplyFullestDrive(_attentionDrives = await _diskSpace.GetDrivesAsync(CancellationToken.None).WaitAsync(GlanceTimeout)), "drive list");
                await RefreshAttentionAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Refreshing What needs attention failed.");
            }
            finally
            {
                _attentionRefreshRunning = false;
            }
        });
    }

    /// <summary>Builds the card from the facts known now. Cheap; called after every source changes.</summary>
    private void RebuildAttention()
    {
        var report = _checkup.Last;
        var lastCheckup = report?.Captured ?? Settings.Current.General.LastCheckupUtc?.ToLocalTime();
        var pending = report?.Results.PendingUpdates;
        var facts = new AttentionFacts
        {
            Now = DateTimeOffset.Now,
            LastCheckup = lastCheckup,
            CheckupItems = report?.Items.Count ?? 0,
            CheckupCriticalItems = report?.Items.Count(i => i.Severity == Severity.Critical) ?? 0,
            Drives = _attentionDrives
                .Where(d => d.TotalBytes > 0)
                .Select(d => new AttentionDrive(d.Letter, d.Label, DashboardGlance.UsedPercent(d), d.FreeBytes, d.IsSystemDrive))
                .ToList(),
            // On or off from the live list (changes on the Startup page show straight away), how heavy from the check-up.
            HeavyStartupApps = AttentionRules.CountHeavyStartupApps(_liveStartupItems, report?.Results.Startup),
            Updates = _updateStatus is null && pending is null ? null : DashboardGlance.Updates(_updateStatus, pending),
            PawnIo = _pawnIoState,
            DeviceProblems = report?.Results.Drivers?.Drivers.Count(d => d.HasProblem),
            RecentBlueScreens = _crashGlance?.BlueScreens,
            RecentSuddenShutdowns = _crashGlance?.SuddenShutdowns,
        };

        AttentionItems.Clear();
        foreach (var item in AttentionRules.Build(facts))
        {
            AttentionItems.Add(new AttentionRow(
                item.Id,
                item.Level switch
                {
                    AttentionLevel.Critical => Severity.Critical,
                    AttentionLevel.Warning => Severity.Medium,
                    _ => Severity.Info,
                },
                Loc.F(item.Title, item.TitleArgs.ToArray()),
                Loc.F(item.Detail, item.DetailArgs.ToArray()),
                Loc.T(item.ActionText),
                new AsyncRelayCommand(() => RunAttentionAsync(item))));
        }

        HasAttentionItems = AttentionItems.Count > 0;
        AttentionAllClearText = lastCheckup is { } last
            ? Loc.F("Last checked {0}.", RelativeTimeConverter.Format(last, DateTimeOffset.Now))
            : Loc.T("Nothing to do right now.");
    }

    private async Task RunAttentionAsync(AttentionItem item)
    {
        try
        {
            switch (item.Action)
            {
                case AttentionAction.RunCheckup:
                    await _quickActions.RunAsync(QuickActionIds.Checkup);
                    break;
                case AttentionAction.InstallPawnIo:
                    await InstallPawnIoAsync();
                    break;
                default:
                    if (PageHelpLocator.PageTypeOf(item.PageKey) is { } page)
                    {
                        _navigation.Navigate(page);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The attention action {Id} failed.", item.Id);
            _notifications.Error(Loc.T("That did not work"), ex.Message);
        }
    }

    /// <summary>The same confirm-install-reload flow as the Fans page, with the busy overlay on the Dashboard.</summary>
    private async Task InstallPawnIoAsync()
    {
        if (!await _pawnIo.ConfirmInstallAsync())
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => DispatcherHelper.PostToUi(() => BusyText = p.Stage));
            var result = await _pawnIo.InstallAndReloadAsync(progress, ct);
            if (result.Success)
            {
                _notifications.Success(Loc.T("PawnIO installed"), result.Message);
            }

            _pawnIoState = await Task.Run(() => _pawnIo.GetStatus().State, ct);
            RebuildAttention();
        }, Loc.T("Installing PawnIO…"), cancellable: false);
    }

    /// <summary>Remembers when the last check-up finished, so "Last check-up was 14 days ago" works after a restart.</summary>
    private void OnCheckupCompleted(object? sender, CheckupReport report)
    {
        Settings.Current.General.LastCheckupUtc = report.Captured.ToUniversalTime();
        DispatcherHelper.PostToUi(async () =>
        {
            try
            {
                await Settings.SaveAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not save the check-up time.");
            }
        });
    }
}
