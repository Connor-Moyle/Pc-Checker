using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Leftovers;
using PcChecker.Core.Settings;
using PcChecker.Core.Software;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the Software page: scans installed programs and Store apps, filters and sorts them, shows bundle
/// groups, and offers uninstall, size measurement, CSV export and winget upgrade checks (see the Actions partial).
/// </summary>
public sealed partial class SoftwareViewModel : ViewModelBase
{
    private readonly ISoftwareInventoryService _inventory;
    private readonly IUninstallService _uninstaller;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<SoftwareViewModel> _logger;
    private readonly LeftoverScanQueue _leftovers;
    private readonly INavigationService _navigation;
    private readonly List<ProgramRow> _allRows = [];

    /// <summary>Sizes measured with "Calculate sizes", keyed by program id. Carried across rescans so the work is not lost.</summary>
    private readonly Dictionary<string, long> _measuredSizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The inventory the page currently shows (the last scan plus any measured sizes); what export and size measurement read.</summary>
    private SoftwareInventory? _current;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SoftwareSourceFilter _sourceFilter = SoftwareSourceFilter.All;

    [ObservableProperty]
    private SoftwareSortOrder _sortOrder = SoftwareSortOrder.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBundleSection))]
    private bool _showBundles = true;

    /// <summary>When on, uninstallers run with their silent switches so no installer windows appear.</summary>
    [ObservableProperty]
    private bool _quietUninstall = true;

    /// <summary>
    /// Advanced mode only: remove Store apps from every account on this PC instead of just the signed-in one.
    /// Off by default; the confirmation dialog states which scope will be used.
    /// </summary>
    [ObservableProperty]
    private bool _removeStoreAppsForAllUsers;

    [ObservableProperty]
    private ProgramRow? _selectedProgram;

    [ObservableProperty]
    private int _programCount;

    [ObservableProperty]
    private int _storeAppCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBundleSection))]
    private int _bundleCount;

    [ObservableProperty]
    private string _totalSizeText = "—";

    [ObservableProperty]
    private string _sizeKnownText = string.Empty;

    [ObservableProperty]
    private string? _lastScanText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasScanned;

    [ObservableProperty]
    private string _filterSummary = string.Empty;

    [ObservableProperty]
    private double _scanPercent;

    public SoftwareViewModel(ISettingsService settings, ISoftwareInventoryService inventory, IUninstallService uninstaller,
        IDialogService dialogs, INotificationService notifications, LeftoverScanQueue leftovers, INavigationService navigation,
        ILogger<SoftwareViewModel> logger)
        : base(settings)
    {
        _inventory = inventory;
        _uninstaller = uninstaller;
        _dialogs = dialogs;
        _notifications = notifications;
        _leftovers = leftovers;
        _navigation = navigation;
        _logger = logger;
        RebuildSourceFilters(IsAdvancedMode);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                OnAdvancedModeToggled(IsAdvancedMode);
            }
        };
    }

    public ObservableCollection<ProgramRow> Programs { get; } = [];
    public ObservableCollection<BundleRow> Bundles { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<WingetEntry> WingetUpgrades { get; } = [];

    public ObservableCollection<SoftwareSourceFilter> SourceFilters { get; } = [];
    public IReadOnlyList<SoftwareSortOrder> SortOrders { get; } = Enum.GetValues<SoftwareSortOrder>();

    public bool HasResults => HasScanned && _allRows.Count > 0;
    public bool IsEmpty => HasScanned && _allRows.Count == 0;
    public bool HasWarnings => Warnings.Count > 0;
    public bool ShowBundleSection => ShowBundles && BundleCount > 0;
    public bool HasFilteredRows => Programs.Count > 0;
    public bool NoMatches => HasResults && Programs.Count == 0;

    /// <summary>The inventory currently shown, or null before the first scan.</summary>
    public SoftwareInventory? CurrentInventory => _current;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_current is not null)
        {
            // Coming back to the page: keep what was shown, including sizes measured since the scan.
            ApplyInventory(_current);
            return;
        }

        if (_inventory.Last is { } last)
        {
            ApplyInventory(last);
            return;
        }

        await ScanAsync();
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => ScanAsync();

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async ct =>
    {
        var progress = new Progress<ProgressUpdate>(p =>
        {
            BusyText = p.Detail is null ? p.Stage + "…" : $"{p.Stage}…\n{p.Detail}";
            ScanPercent = p.Percent ?? ScanPercent;
        });
        var inventory = await _inventory.ScanAsync(progress, ct);
        ApplyScan(inventory);
        _logger.LogInformation("Software page loaded {Count} programs.", inventory.Programs.Count);
    }, "Scanning installed programs…");

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSourceFilterChanged(SoftwareSourceFilter value) => ApplyFilter();
    partial void OnSortOrderChanged(SoftwareSortOrder value) => ApplyFilter();
    private void OnAdvancedModeToggled(bool advanced)
    {
        RebuildSourceFilters(advanced);
        if (!advanced && SourceFilter == SoftwareSourceFilter.SystemComponents)
        {
            SourceFilter = SoftwareSourceFilter.All;
        }

        ApplyFilter();
    }

    private void RebuildSourceFilters(bool advanced)
    {
        var wanted = Enum.GetValues<SoftwareSourceFilter>().Where(f => advanced || f != SoftwareSourceFilter.SystemComponents).ToList();
        if (SourceFilters.SequenceEqual(wanted))
        {
            return;
        }

        var current = SourceFilter;
        SourceFilters.Clear();
        foreach (var f in wanted)
        {
            SourceFilters.Add(f);
        }

        // Clearing the list made the combo box drop its selection; re-announce the value so it re-selects.
        SourceFilter = wanted.Contains(current) ? current : SoftwareSourceFilter.All;
        OnPropertyChanged(nameof(SourceFilter));
    }

    /// <summary>Shows a fresh scan, re-applying sizes measured earlier to programs the installer still reports no size for.</summary>
    private void ApplyScan(SoftwareInventory scan) => ApplyInventory(MergeMeasuredSizes(scan, _measuredSizes));

    /// <summary>Returns <paramref name="scan"/> with <paramref name="measured"/> filled into entries that lack a size; unchanged when nothing applies.</summary>
    internal static SoftwareInventory MergeMeasuredSizes(SoftwareInventory scan, IReadOnlyDictionary<string, long> measured)
    {
        if (measured.Count == 0 || !scan.Programs.Any(p => p.SizeBytes is null or 0 && measured.ContainsKey(p.Id)))
        {
            return scan;
        }

        var programs = scan.Programs
            .Select(p => p.SizeBytes is null or 0 && measured.TryGetValue(p.Id, out var size) ? p with { SizeBytes = size } : p)
            .ToList();
        return scan with { Programs = programs, Bundles = RelinkBundles(scan.Bundles, programs) };
    }

    private void ApplyInventory(SoftwareInventory inventory)
    {
        _current = inventory;
        DispatcherHelper.RunOnUi(() =>
        {
            var byId = inventory.Programs.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            var childRowsByParent = new Dictionary<string, List<BundleChildRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in inventory.Bundles)
            {
                childRowsByParent[group.Parent.Id] = group.Children.Select(c => new BundleChildRow(c, group.Reason)).ToList();
            }

            _allRows.Clear();
            foreach (var program in inventory.Programs)
            {
                var parentName = program.BundledWithId is not null && byId.TryGetValue(program.BundledWithId, out var parent) ? parent.Name : null;
                var children = childRowsByParent.TryGetValue(program.Id, out var kids) ? kids : [];
                _allRows.Add(new ProgramRow(program, SafeVerdict(program), parentName, children));
            }

            Bundles.Clear();
            foreach (var group in inventory.Bundles)
            {
                Bundles.Add(new BundleRow(group, childRowsByParent[group.Parent.Id]));
            }

            Warnings.Clear();
            foreach (var w in inventory.Warnings)
            {
                Warnings.Add(w);
            }

            ProgramCount = inventory.Programs.Count(p => p.Source != SoftwareSource.StoreApp);
            StoreAppCount = inventory.Programs.Count(p => p.Source == SoftwareSource.StoreApp);
            BundleCount = inventory.Bundles.Count;
            var sized = inventory.Programs.Where(p => p.SizeBytes is > 0).ToList();
            TotalSizeText = sized.Count == 0 ? "—" : ByteSize.Format(sized.Sum(p => p.SizeBytes!.Value));
            SizeKnownText = sized.Count == inventory.Programs.Count
                ? "all entries measured"
                : $"{sized.Count} of {inventory.Programs.Count} entries have a known size";
            LastScanText = "Last scan " + inventory.Captured.ToLocalTime().ToString("g");
            HasScanned = true;
            OnPropertyChanged(nameof(HasWarnings));
            ApplyFilter();
        });
    }

    private (bool, string) SafeVerdict(InstalledProgram program)
    {
        try
        {
            return _uninstaller.CanUninstall(program);
        }
        catch (Exception ex)
        {
            return (false, "Could not decide whether this can be uninstalled: " + ex.Message);
        }
    }

    private void ApplyFilter()
    {
        var search = (SearchText ?? string.Empty).Trim().ToLowerInvariant();
        IEnumerable<ProgramRow> rows = _allRows.Where(r => SourceFilter switch
        {
            SoftwareSourceFilter.Programs => !r.IsStoreApp && !r.IsSystemComponent,
            SoftwareSourceFilter.StoreApps => r.IsStoreApp,
            SoftwareSourceFilter.UserOnly => r.IsUserInstall,
            SoftwareSourceFilter.SystemComponents => r.IsSystemComponent,
            _ => IsAdvancedMode || !r.IsSystemComponent || r.IsStoreApp,
        });

        if (search.Length > 0)
        {
            rows = rows.Where(r => r.SearchText.Contains(search, StringComparison.Ordinal));
        }

        rows = SortOrder switch
        {
            SoftwareSortOrder.Size => rows.OrderByDescending(r => r.SortSize).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            SoftwareSortOrder.Date => rows.OrderByDescending(r => r.SortDate).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            SoftwareSortOrder.Publisher => rows.OrderBy(r => r.Publisher, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var selectedId = SelectedProgram?.Id;
        Programs.Clear();
        foreach (var row in rows)
        {
            Programs.Add(row);
        }

        SelectedProgram = selectedId is null ? null : Programs.FirstOrDefault(r => r.Id == selectedId);
        FilterSummary = Programs.Count == _allRows.Count
            ? $"{Programs.Count} entries"
            : $"{Programs.Count} of {_allRows.Count} entries";
        OnPropertyChanged(nameof(HasFilteredRows));
        OnPropertyChanged(nameof(NoMatches));
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Software page operation failed.");
}
