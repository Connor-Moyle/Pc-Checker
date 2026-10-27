using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Checkup;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Check-up page: one button runs the Health, Cleanup, Drivers, Windows Update, Startup, Speed check, Debloat and
/// Old drivers scans through <see cref="ICheckupService"/> with one combined progress view, and shows the findings as
/// one ranked to-do list. Safe automatic fixes are ticked; "Fix selected" applies the ticked ones through the owning
/// services after a confirmation. Split across partial files: this one runs the check-up, the other applies fixes.
/// </summary>
public sealed partial class CheckupViewModel : ViewModelBase
{
    private static readonly IReadOnlyDictionary<CheckupSource, Type> Pages = new Dictionary<CheckupSource, Type>
    {
        [CheckupSource.Health] = typeof(HealthPage),
        [CheckupSource.Cleanup] = typeof(CleanupPage),
        [CheckupSource.Drivers] = typeof(DriversPage),
        [CheckupSource.WindowsUpdate] = typeof(WindowsUpdatePage),
        [CheckupSource.Startup] = typeof(StartupPage),
        [CheckupSource.Performance] = typeof(PerformancePage),
        [CheckupSource.Debloat] = typeof(DebloatPage),
        [CheckupSource.OldDrivers] = typeof(DriverCleanupPage),
        [CheckupSource.Sensors] = typeof(FansPage),
    };

    private readonly ICheckupService _checkup;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<CheckupViewModel> _logger;
    private CheckupReport? _report;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    [NotifyPropertyChangedFor(nameof(HasNoItems))]
    private bool _hasReport;

    [ObservableProperty]
    private string _subtitle;

    [ObservableProperty]
    private double? _overallProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    [NotifyPropertyChangedFor(nameof(HasNoItems))]
    private int _itemCount;

    [ObservableProperty]
    private string _itemsSubtitle = string.Empty;

    [ObservableProperty]
    private int _fixableCount;

    [ObservableProperty]
    private string _fixableSubtitle = string.Empty;

    [ObservableProperty]
    private string _urgentText = "0";

    [ObservableProperty]
    private string _urgentSubtitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FixSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private string _selectedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    public CheckupViewModel(ISettingsService settings, ICheckupService checkup, IDialogService dialogs, INotificationService notifications,
        INavigationService navigation, ILogger<CheckupViewModel> logger)
        : base(settings)
    {
        _checkup = checkup;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        _selectedText = Loc.T("Nothing selected");
        _subtitle = Loc.T("One check of everything, as a to-do list. Nothing changes until you choose to fix it.");
        ResultLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResultLines));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                foreach (var row in Items)
                {
                    row.UpdateMode(IsAdvancedMode);
                }

                UpdateSelection();
            }
        };
    }

    /// <summary>The ranked to-do list.</summary>
    public ObservableCollection<CheckupItemRow> Items { get; } = [];

    public bool HasItems => HasReport && ItemCount > 0;

    public bool HasNoItems => HasReport && ItemCount == 0;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    /// <summary>
    /// Shows the last check-up of this session when there is one. Otherwise normal mode waits for the button (a full
    /// check-up can take a few minutes), and demo mode runs it so the page has content.
    /// </summary>
    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_checkup.Last is { } last)
        {
            Apply(last);
            return Task.CompletedTask;
        }

        return AppOptions.Current.IsDemo ? RunCheckupAsync(cancellationToken) : Task.CompletedTask;
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunCheckupAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Check-up page operation failed.");

    [RelayCommand]
    private Task RunAsync() => RunCheckupAsync(CancellationToken.None);

    private Task RunCheckupAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        try
        {
            var report = await _checkup.RunAsync(ProgressReporter(), linked.Token);
            ClearResults();
            Apply(report);
            _notifications.Success(Loc.T("Check-up finished"), report.Items.Count == 0
                ? Loc.T("Nothing needs your attention right now.")
                : Loc.F("{0} things to look at, {1} can be fixed from here.", report.Items.Count, report.FixableCount));
        }
        finally
        {
            OverallProgress = null;
        }
    }, Loc.T("Starting the check-up…"));

    [RelayCommand]
    private void Open(CheckupItemRow? row)
    {
        if (row?.Page is { } page)
        {
            _navigation.Navigate(page);
        }
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var row in Items)
        {
            row.IsSelected = row.CanSelect && CheckupSelection.IsSelectedByDefault(row.Item);
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Items)
        {
            row.IsSelected = false;
        }
    }

    private void Apply(CheckupReport report)
    {
        _report = report;
        Items.Clear();
        foreach (var item in report.Items)
        {
            var page = Pages.GetValueOrDefault(item.Source);
            var title = NavigationCatalog.Pages.FirstOrDefault(p => p.PageType == page)?.Title ?? item.Source.ToString();
            Items.Add(new CheckupItemRow(item, page, Loc.T(title), IsAdvancedMode, UpdateSelection));
        }

        ItemCount = report.Items.Count;
        int needYou = report.Items.Count(i => !i.HasFix);
        ItemsSubtitle = needYou == 0 ? Loc.T("All can be fixed from here") : Loc.F("{0} need you on their page", needYou);
        FixableCount = report.FixableCount;
        int safe = report.Items.Count(i => i.HasFix && i.Risk == Core.Optimization.RiskLevel.Safe);
        FixableSubtitle = Loc.F("{0} of them rated Safe", safe);
        int urgent = report.Items.Count(i => i.Severity >= Severity.High);
        UrgentText = urgent.ToString(CultureInfo.CurrentCulture);
        UrgentSubtitle = urgent == 0 ? Loc.T("Nothing serious") : Loc.T("High or critical, listed first");
        Warnings = report.Results.Gaps.Count == 0 ? null : string.Join("\n", report.Results.Gaps.Select(g => g.Reason));
        Subtitle = Loc.F("Checked {0}. Nothing changes until you choose to fix it.", report.Captured.ToString("t", CultureInfo.CurrentCulture));
        HasReport = true;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = Items.Where(r => r.IsSelected).ToList();
        SelectedCount = selected.Count;
        if (selected.Count == 0)
        {
            SelectedText = Loc.T("Nothing selected");
            return;
        }

        var plan = CheckupBatchPlanner.Plan(selected.Select(r => r.Item), IsAdvancedMode, Settings.Current.RestorePoints.CreateBeforeChanges);
        SelectedText = plan.SharedRestorePoint
            ? Loc.F("{0} selected · one restore point first", selected.Count)
            : Loc.F("{0} selected", selected.Count);
    }

    /// <summary>Progress for the busy overlay: the combined stage line, the part's own detail and the overall percentage.</summary>
    private IProgress<ProgressUpdate> ProgressReporter() => new Progress<ProgressUpdate>(p =>
    {
        BusyText = p.Detail is null ? p.Stage : p.Stage + "\n" + p.Detail;
        OverallProgress = p.Percent;
    });
}
