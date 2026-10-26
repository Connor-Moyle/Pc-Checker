using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Optimization;
using PcChecker.Core.Settings;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Optimize page: grouped, reversible tweaks with live state detection and a one-click safe set.</summary>
public sealed partial class OptimizeViewModel : ViewModelBase
{
    private const int MaxOutputLines = 400;

    private readonly IOptimizationService _optimization;
    private readonly OptimizationActionRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<OptimizeViewModel> _logger;
    private readonly Dictionary<string, OptimizationActionRow> _rows = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _isRebootRequired;
    [ObservableProperty] private string _restorePointNote = string.Empty;
    [ObservableProperty] private string _summaryText = "Checking the current state of each tweak…";
    [ObservableProperty] private int _selectedSafeCount;
    [ObservableProperty] private bool _hasOutput;
    [ObservableProperty] private bool _isBatchRunning;
    [ObservableProperty] private string _batchProgressText = string.Empty;
    [ObservableProperty] private bool _isRefreshing;

    public OptimizeViewModel(ISettingsService settings, IOptimizationService optimization, OptimizationActionRunner runner,
        IDialogService dialogs, INotificationService notifications, INavigationService navigation, ILogger<OptimizeViewModel> logger)
        : base(settings)
    {
        _optimization = optimization;
        _runner = runner;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        _isRebootRequired = runner.IsRebootRequired;
        _restorePointNote = runner.RestorePointNote;

        runner.RebootRequiredChanged += (_, _) => DispatcherHelper.PostToUi(() => IsRebootRequired = _runner.IsRebootRequired);
        runner.ActionCompleted += (_, e) => DispatcherHelper.PostToUi(() => _ = RefreshRowAsync(e.ActionId));
        settings.Changed += (_, _) => DispatcherHelper.PostToUi(() => RestorePointNote = _runner.RestorePointNote);
        if (optimization is IOptimizationOutputSource source)
        {
            source.OutputLine += (_, line) => DispatcherHelper.PostToUi(() => AppendOutput(line));
        }

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                UpdateVisibility();
            }
        };
    }

    public ObservableCollection<OptimizationGroup> Groups { get; } = [];
    public ObservableCollection<string> OutputLines { get; } = [];

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        BuildGroups();
        await RefreshStatesAsync();
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RefreshStatesAsync();

    private void BuildGroups()
    {
        Groups.Clear();
        _rows.Clear();
        var catalog = OptimizationCatalog.All;
        var providerActions = _optimization.Actions.Where(a => OptimizationCatalog.Find(a.Id) is null).ToList();

        var safe = new OptimizationGroup("Safe tweaks", "Standard Windows settings that make the desktop feel snappier. All reversible.", SymbolRegular.ShieldCheckmark24, false);
        AddRows(safe, catalog.Where(a => a.Risk == RiskLevel.Safe && !a.AdvancedOnly
            && a.Category is not (OptimizationCatalog.GamingCategory or OptimizationCatalog.StorageCategory)), selectable: true);

        var gaming = new OptimizationGroup("Gaming", "Give games priority and remove overlays that cost frames.", SymbolRegular.XboxController24, false);
        AddRows(gaming, catalog.Where(a => a.Category == OptimizationCatalog.GamingCategory && !a.AdvancedOnly), selectable: true);

        var storage = new OptimizationGroup("Storage", "Keep drives fast and free up space.", SymbolRegular.HardDrive24, false);
        AddRows(storage, catalog.Where(a => a.Category == OptimizationCatalog.StorageCategory && !a.AdvancedOnly), selectable: false);

        Groups.Add(safe);
        Groups.Add(gaming);
        Groups.Add(storage);

        foreach (var category in providerActions.Select(a => a.Category).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var group = new OptimizationGroup(category, $"Actions from the {category} page.", IconFor(category), false);
            AddRows(group, providerActions.Where(a => a.Category.Equals(category, StringComparison.OrdinalIgnoreCase)), selectable: false);
            Groups.Add(group);
        }

        var advanced = new OptimizationGroup("Advanced", "Deeper changes: network, privacy, services and repair tools. Shown only in Advanced mode.", SymbolRegular.WrenchScrewdriver24, true);
        AddRows(advanced, catalog.Where(a => a.AdvancedOnly), selectable: false);
        Groups.Add(advanced);

        UpdateVisibility();
    }

    private void AddRows(OptimizationGroup group, IEnumerable<OptimizationAction> actions, bool selectable)
    {
        foreach (var action in actions)
        {
            var row = new OptimizationActionRow(action, selectable && action.Reversible);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(OptimizationActionRow.IsSelected) or nameof(OptimizationActionRow.IsApplied))
                {
                    UpdateCounts();
                }
            };
            group.Actions.Add(row);
            _rows[action.Id] = row;
        }
    }

    private static SymbolRegular IconFor(string category) => category.ToLowerInvariant() switch
    {
        "latency" => SymbolRegular.Timer24,
        "memory" => SymbolRegular.Memory16,
        "startup" => SymbolRegular.Power24,
        "input" or "keyboard & mouse" or "keyboard and mouse" => SymbolRegular.Keyboard24,
        "network" => SymbolRegular.Globe24,
        _ => SymbolRegular.Toolbox24,
    };

    private void UpdateVisibility()
    {
        foreach (var group in Groups)
        {
            foreach (var row in group.Actions)
            {
                row.IsVisible = IsAdvancedMode || !row.AdvancedOnly;
            }

            group.IsVisible = group.Actions.Any(a => a.IsVisible) && (!group.IsAdvanced || IsAdvancedMode);
            group.UpdateSummary();
        }

        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var rows = _rows.Values.Where(r => r.IsVisible).ToList();
        int stateful = rows.Count(r => !r.IsOnDemand);
        int applied = rows.Count(r => r.IsApplied == true);
        SummaryText = $"{applied} of {stateful} tweaks applied · {rows.Count(r => r.IsOnDemand)} on-demand tools";
        SelectedSafeCount = rows.Count(r => r.IsSelectable && r.IsSelected && r.IsApplied != true);
        foreach (var group in Groups)
        {
            group.UpdateSummary();
        }
    }

    [RelayCommand]
    private async Task RefreshStatesAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        ErrorMessage = null;
        try
        {
            foreach (var row in _rows.Values)
            {
                await RefreshRowAsync(row.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "State refresh failed.");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            UpdateCounts();
        }
    }

    private async Task RefreshRowAsync(string actionId)
    {
        if (!_rows.TryGetValue(actionId, out var row))
        {
            return;
        }

        try
        {
            var state = await _optimization.GetStateAsync(actionId, CancellationToken.None);
            row.IsApplied = state.IsApplied;
            row.StateText = state.CurrentValue ?? (state.IsApplied switch { true => "Applied", false => "Not applied", _ => "Unknown" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "State check failed for {Action}.", actionId);
            row.IsApplied = null;
            row.StateText = "Unknown";
        }
    }

    [RelayCommand]
    private async Task ApplyAsync(OptimizationActionRow? row)
    {
        if (row is null || row.IsBusy)
        {
            return;
        }

        if (row.Id.Equals("tempfiles.clean", StringComparison.OrdinalIgnoreCase))
        {
            _navigation.Navigate(typeof(CleanupPage));
            return;
        }

        if (row.IsOnDemand)
        {
            OutputLines.Clear();
            HasOutput = false;
        }

        row.IsBusy = true;
        try
        {
            await _runner.RunAsync(row.Id);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevertAsync(OptimizationActionRow? row)
    {
        if (row is null || row.IsBusy)
        {
            return;
        }

        row.IsBusy = true;
        try
        {
            await _runner.RunAsync(row.Id, revert: true);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyAllSafeAsync()
    {
        var selected = _rows.Values.Where(r => r.IsVisible && r.IsSelectable && r.IsSelected && r.IsApplied != true).ToList();
        if (selected.Count == 0)
        {
            _notifications.Info("Nothing to apply", "Tick the safe tweaks you want, or they are all applied already.");
            return;
        }

        string list = string.Join("\n", selected.Select(r => "• " + r.Title));
        if (!await _dialogs.ConfirmAsync($"Apply {selected.Count} safe tweak{(selected.Count == 1 ? string.Empty : "s")}?",
                $"{list}\n\nEach one is reversible from this page.\n{_runner.RestorePointNote}", "Apply all"))
        {
            return;
        }

        IsBatchRunning = true;
        int done = 0, ok = 0;
        try
        {
            foreach (var row in selected)
            {
                done++;
                BatchProgressText = $"Applying {done} of {selected.Count}: {row.Title}";
                row.IsBusy = true;
                try
                {
                    if (await _runner.RunAsync(row.Id, revert: false, confirm: false, CancellationToken.None))
                    {
                        ok++;
                    }
                }
                finally
                {
                    row.IsBusy = false;
                }
            }
        }
        finally
        {
            IsBatchRunning = false;
            BatchProgressText = string.Empty;
        }

        _notifications.Show("Safe tweaks applied", $"{ok} of {selected.Count} tweaks applied.", ok == selected.Count ? NotificationKind.Success : NotificationKind.Warning);
    }

    [RelayCommand]
    private void SelectAllSafe()
    {
        bool anyUnselected = _rows.Values.Any(r => r.IsSelectable && !r.IsSelected);
        foreach (var row in _rows.Values.Where(r => r.IsSelectable))
        {
            row.IsSelected = anyUnselected;
        }
    }

    [RelayCommand]
    private void ClearOutput()
    {
        OutputLines.Clear();
        HasOutput = false;
    }

    private void AppendOutput(string line)
    {
        OutputLines.Add(line);
        while (OutputLines.Count > MaxOutputLines)
        {
            OutputLines.RemoveAt(0);
        }

        HasOutput = true;
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Optimize page operation failed.");
}
