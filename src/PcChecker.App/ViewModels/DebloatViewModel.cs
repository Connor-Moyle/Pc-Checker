using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Debloat;
using PcChecker.Core.Settings;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the Debloat page: presets, category groups of tweak rows with live state, selection, and the
/// guarded batch apply/revert (see DebloatViewModel.Actions.cs).
/// </summary>
public sealed partial class DebloatViewModel : ViewModelBase
{
    private readonly IDebloatService _debloat;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<DebloatViewModel> _logger;
    private readonly Dictionary<string, DebloatTweakRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressCounts;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ApplyButtonText))] private int _applicableCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RevertButtonText))] private int _revertibleCount;

    /// <summary>Footer button text (Button.Content is an object, so a binding StringFormat would be ignored).</summary>
    public string ApplyButtonText => $"Apply selected ({ApplicableCount})";

    public string RevertButtonText => $"Revert selected ({RevertibleCount})";
    [ObservableProperty] private string _selectionText = "Nothing selected";
    [ObservableProperty] private string _stateSummary = "Checking the current state of each tweak…";
    [ObservableProperty] private bool _isLoadingStates;
    [ObservableProperty] private bool _isIntroOpen = true;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _resultsTitle = string.Empty;
    [ObservableProperty] private string _resultsMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _resultsSeverity = InfoBarSeverity.Success;
    [ObservableProperty] private bool _isRestartExplorerSuggested;
    [ObservableProperty] private bool _isRebootRequired;
    [ObservableProperty] private string? _activePresetName;

    public DebloatViewModel(ISettingsService settings, IDebloatService debloat, IDialogService dialogs, INotificationService notifications,
        ILogger<DebloatViewModel> logger)
        : base(settings)
    {
        _debloat = debloat;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;

        BuildGroups();
        foreach (var preset in debloat.Presets)
        {
            Presets.Add(new DebloatPresetCard(preset, preset.TweakIds.Count(id => _rows.ContainsKey(id))));
        }

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsAdvancedMode) or nameof(SearchText))
            {
                UpdateVisibility();
            }
        };
    }

    public ObservableCollection<DebloatCategoryGroup> Groups { get; } = [];
    public ObservableCollection<DebloatPresetCard> Presets { get; } = [];
    public ObservableCollection<DebloatResultRow> Results { get; } = [];
    public IReadOnlyList<DebloatUnsafeItem> NotOffered => _debloat.NotOffered;
    public IReadOnlyList<DebloatNote> Notes => _debloat.Notes;

    /// <summary>Bound to the Apps group's "for all users" switch.</summary>
    public bool RemoveAppsForAllUsers
    {
        get => _debloat.RemoveAppsForAllUsers;
        set
        {
            if (_debloat.RemoveAppsForAllUsers != value)
            {
                _debloat.RemoveAppsForAllUsers = value;
                OnPropertyChanged();
            }
        }
    }

    public int TotalTweaks => _rows.Count;

    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadStatesAsync();

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadStatesAsync();

    /// <summary>Re-reads every tweak's state from the PC.</summary>
    [RelayCommand]
    private async Task LoadStatesAsync()
    {
        if (IsLoadingStates)
        {
            return;
        }

        IsLoadingStates = true;
        try
        {
            await RunBusyAsync(async ct =>
            {
                var states = await _debloat.GetStatesAsync(ct);
                ApplyStates(states);
            }, "Checking which tweaks are already applied…");
        }
        finally
        {
            IsLoadingStates = false;
        }
    }

    private void ApplyStates(IReadOnlyDictionary<string, TweakState> states)
    {
        _suppressCounts = true;
        try
        {
            foreach (var row in _rows.Values)
            {
                row.Apply(states.TryGetValue(row.Id, out var state) ? state : TweakState.Unknown(row.Id));
            }
        }
        finally
        {
            _suppressCounts = false;
        }

        UpdateVisibility();
    }

    private void BuildGroups()
    {
        foreach (var category in DebloatCatalog.Categories)
        {
            var group = new DebloatCategoryGroup(category, DescribeCategory(category), IconFor(category))
            {
                IsExpanded = category == DebloatCatalog.PrivacyCategory,
            };
            foreach (var tweak in _debloat.Tweaks.Where(t => t.Category == category))
            {
                var row = new DebloatTweakRow(tweak);
                row.PropertyChanged += (_, e) =>
                {
                    if (!_suppressCounts && e.PropertyName is nameof(DebloatTweakRow.IsSelected) or nameof(DebloatTweakRow.Status))
                    {
                        UpdateCounts();
                    }
                };
                group.Rows.Add(row);
                _rows[tweak.Id] = row;
            }

            Groups.Add(group);
        }

        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        _suppressCounts = true;
        try
        {
            foreach (var group in Groups)
            {
                foreach (var row in group.Rows)
                {
                    row.IsVisible = IsAllowed(row) && row.Matches(SearchText);
                    if (!IsAllowed(row))
                    {
                        row.IsSelected = false;
                    }
                }

                group.IsVisible = group.Rows.Any(r => r.IsVisible);
                if (!string.IsNullOrWhiteSpace(SearchText) && group.IsVisible)
                {
                    group.IsExpanded = true;
                }
            }
        }
        finally
        {
            _suppressCounts = false;
        }

        UpdateCounts();
    }

    /// <summary>True when the row may be shown and run in the current mode (search filtering aside).</summary>
    private bool IsAllowed(DebloatTweakRow row) => IsAdvancedMode || !row.AdvancedOnly;

    private void UpdateCounts()
    {
        var visible = _rows.Values.Where(IsAllowed).ToList();
        var selected = visible.Where(r => r.IsSelected).ToList();
        SelectedCount = selected.Count;
        ApplicableCount = selected.Count(r => r.CanApply);
        RevertibleCount = selected.Count(r => r.CanRevert);
        SelectionText = selected.Count == 0
            ? "Nothing selected. Pick a preset or tick the tweaks you want."
            : $"{selected.Count} selected · {ApplicableCount} to apply · {RevertibleCount} can be reverted";

        int applied = visible.Count(r => r.Status == TweakStatus.Applied);
        int available = visible.Count(r => r.Status != TweakStatus.NotApplicable);
        int partial = visible.Count(r => r.Status == TweakStatus.Partial);
        StateSummary = $"{applied} of {available} tweaks applied" + (partial > 0 ? $" · {partial} partly applied" : string.Empty)
            + $" · {visible.Count(r => r.Status == TweakStatus.NotApplicable)} not available on this Windows";
        foreach (var group in Groups)
        {
            group.UpdateSummary();
        }
    }

    private static string DescribeCategory(string category) => category switch
    {
        DebloatCatalog.PrivacyCategory => "What Windows collects and sends about you: diagnostic data, advertising ID, activity history, typing and location.",
        DebloatCatalog.AdsCategory => "Promoted apps, tips, suggestions and nags in Start, Settings, the lock screen and File Explorer.",
        DebloatCatalog.AiCategory => "Copilot, Recall, Click to Do, AI in Paint and Notepad, and Bing web results in Start search.",
        DebloatCatalog.TaskbarCategory => "Personal preferences for a cleaner taskbar and Start menu. All reversible.",
        DebloatCatalog.ExplorerCategory => "Preferences that make File Explorer clearer and safer (for example, showing file extensions).",
        DebloatCatalog.EdgeCategory => "Stop Edge and OneDrive running in the background and nagging. Edge itself is never removed.",
        DebloatCatalog.GamingCategory => "Background recording, Game Mode, fullscreen behaviour and Xbox services.",
        DebloatCatalog.UpdateCategory => "Control when updates install. Windows Update itself is never disabled.",
        DebloatCatalog.ServicesCategory => "Background services that are safe to turn off if you don't use the feature they belong to.",
        DebloatCatalog.AppsCategory => "Pre-installed consumer apps. Removal is one-way here; reinstall from the Microsoft Store any time.",
        _ => string.Empty,
    };

    private static SymbolRegular IconFor(string category) => category switch
    {
        DebloatCatalog.PrivacyCategory => SymbolRegular.ShieldCheckmark24,
        DebloatCatalog.AdsCategory => SymbolRegular.MegaphoneOff24,
        DebloatCatalog.AiCategory => SymbolRegular.Sparkle24,
        DebloatCatalog.TaskbarCategory => SymbolRegular.Desktop24,
        DebloatCatalog.ExplorerCategory => SymbolRegular.Folder24,
        DebloatCatalog.EdgeCategory => SymbolRegular.CloudOff24,
        DebloatCatalog.GamingCategory => SymbolRegular.Games24,
        DebloatCatalog.UpdateCategory => SymbolRegular.ArrowSync24,
        DebloatCatalog.ServicesCategory => SymbolRegular.Server24,
        DebloatCatalog.AppsCategory => SymbolRegular.Apps24,
        _ => SymbolRegular.ShieldTask24,
    };

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Debloat page operation failed.");
}
