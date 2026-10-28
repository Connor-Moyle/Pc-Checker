using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Helpers;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Games;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Games page: every installed game from Steam, Epic, GOG, the EA app, Ubisoft Connect, Battle.net, Riot and the Xbox
/// app as cards with cover art, size, drive and last played; sorting, filters, search and totals. Per game: play,
/// open the folder, verify or uninstall through the launcher, scan for missing DLLs, make a game profile and clear a
/// Steam game's shader cache. Reading is safe, so the page fills itself when first shown.
/// </summary>
public sealed partial class GamesViewModel : ViewModelBase, IRefreshablePage
{
    private readonly IGameLibraryService _library;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly GameFolderScanHandOff _dllScans;
    private readonly GameProfileHandOff _profiles;
    private readonly ILogger<GamesViewModel> _logger;
    private readonly List<GameCardRow> _allRows = [];
    private CancellationTokenSource? _backgroundCts;
    private bool _loadingFilters;

    [ObservableProperty]
    private string _subtitle = Loc.T("Every installed game from your launchers, in one place.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoGames))]
    [NotifyPropertyChangedFor(nameof(HasGames))]
    private bool _hasScanned;

    [ObservableProperty]
    private string _gameCountText = "—";

    [ObservableProperty]
    private string _gameCountSubtitle = string.Empty;

    [ObservableProperty]
    private string _spaceUsedText = "—";

    [ObservableProperty]
    private string _spaceUsedSubtitle = string.Empty;

    [ObservableProperty]
    private string _lastPlayedText = "—";

    [ObservableProperty]
    private string _lastPlayedSubtitle = string.Empty;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private LauncherFilterOption? _selectedLauncher;

    [ObservableProperty]
    private DriveFilterOption? _selectedDrive;

    [ObservableProperty]
    private GameSortOption? _selectedSort;

    [ObservableProperty]
    private string _listSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeasuringText))]
    private string? _measuringText;

    [ObservableProperty]
    private string _launcherSummary = string.Empty;

    public GamesViewModel(ISettingsService settings, IGameLibraryService library, IDialogService dialogs, INotificationService notifications,
        INavigationService navigation, GameFolderScanHandOff dllScans, GameProfileHandOff profiles, ILogger<GamesViewModel> logger)
        : base(settings)
    {
        _library = library;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _dllScans = dllScans;
        _profiles = profiles;
        _logger = logger;

        SortOptions.Add(new GameSortOption(GameSortOrder.Name, Loc.T("Name")));
        SortOptions.Add(new GameSortOption(GameSortOrder.Size, Loc.T("Size")));
        SortOptions.Add(new GameSortOption(GameSortOrder.LastPlayed, Loc.T("Last played")));
        _selectedSort = SortOptions[0];
        LauncherFilters.Add(new LauncherFilterOption(null, Loc.T("All launchers")));
        _selectedLauncher = LauncherFilters[0];
        DriveFilters.Add(new DriveFilterOption(null, Loc.T("All drives")));
        _selectedDrive = DriveFilters[0];
    }

    ICommand IRefreshablePage.RefreshKeyCommand => RescanCommand;

    public ObservableCollection<GameCardRow> Games { get; } = [];

    public ObservableCollection<GameSortOption> SortOptions { get; } = [];

    public ObservableCollection<LauncherFilterOption> LauncherFilters { get; } = [];

    public ObservableCollection<DriveFilterOption> DriveFilters { get; } = [];

    public ObservableCollection<DriveTotalRow> DriveTotals { get; } = [];

    /// <summary>True after a scan that found no games at all.</summary>
    public bool HasNoGames => HasScanned && _allRows.Count == 0;

    public bool HasGames => HasScanned && _allRows.Count > 0;

    /// <summary>True when there are games but the search and filters hide all of them.</summary>
    public bool HasNoMatches => _allRows.Count > 0 && Games.Count == 0;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public bool HasMeasuringText => !string.IsNullOrWhiteSpace(MeasuringText);

    /// <summary>Reading launcher files changes nothing, so the page looks as soon as it is first shown.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => RunScanAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunScanAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Games page operation failed.");

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnSelectedLauncherChanged(LauncherFilterOption? value) => ApplyFilter();

    partial void OnSelectedDriveChanged(DriveFilterOption? value) => ApplyFilter();

    partial void OnSelectedSortChanged(GameSortOption? value) => ApplyFilter();

    [RelayCommand]
    private Task RescanAsync() => RunScanAsync(CancellationToken.None);

    [RelayCommand]
    private void ClearFilters()
    {
        _loadingFilters = true;
        try
        {
            SearchText = null;
            SelectedLauncher = LauncherFilters.FirstOrDefault();
            SelectedDrive = DriveFilters.FirstOrDefault();
        }
        finally
        {
            _loadingFilters = false;
        }

        ApplyFilter();
    }

    private Task RunScanAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var progress = new Progress<ProgressUpdate>(p => BusyText = Loc.T(p.Stage) + (p.Detail is null ? string.Empty : " — " + p.Detail));
        var scan = await _library.ScanAsync(progress, linked.Token);
        Apply(scan);
        StartBackgroundWork();
    }, Loc.T("Looking for games…"));

    private void Apply(GameLibraryScan scan)
    {
        _allRows.Clear();
        foreach (var game in scan.Games)
        {
            _allRows.Add(new GameCardRow(game));
        }

        Warnings = scan.Warnings.Count > 0 ? string.Join(Environment.NewLine, scan.Warnings) : null;
        var found = scan.Launchers.Where(l => l.IsInstalled).Select(l => GameLibraryMath.LauncherName(l.Launcher)).ToList();
        LauncherSummary = found.Count == 0
            ? Loc.T("No game launcher was found on this PC.")
            : Loc.F("Found: {0}.", string.Join(", ", found));
        Subtitle = Loc.F("Checked {0:t}. Launchers are never started to find games.", scan.ScannedAt.LocalDateTime);
        RebuildFilterChoices();
        HasScanned = true;
        UpdateTotals();
        ApplyFilter();
        OnPropertyChanged(nameof(HasNoGames));
        OnPropertyChanged(nameof(HasGames));
    }

    private void RebuildFilterChoices()
    {
        _loadingFilters = true;
        try
        {
            var launcher = SelectedLauncher?.Launcher;
            var drive = SelectedDrive?.Drive;
            LauncherFilters.Clear();
            LauncherFilters.Add(new LauncherFilterOption(null, Loc.T("All launchers")));
            foreach (var group in _allRows.GroupBy(r => r.Launcher).OrderBy(g => GameLibraryMath.LauncherName(g.Key), StringComparer.CurrentCultureIgnoreCase))
            {
                LauncherFilters.Add(new LauncherFilterOption(group.Key, $"{GameLibraryMath.LauncherName(group.Key)} ({group.Count()})"));
            }

            DriveFilters.Clear();
            DriveFilters.Add(new DriveFilterOption(null, Loc.T("All drives")));
            foreach (var group in _allRows.Where(r => r.Game.Drive is not null).GroupBy(r => r.Game.Drive!).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                DriveFilters.Add(new DriveFilterOption(group.Key, $"{group.Key} ({group.Count()})"));
            }

            SelectedLauncher = LauncherFilters.FirstOrDefault(l => l.Launcher == launcher) ?? LauncherFilters[0];
            SelectedDrive = DriveFilters.FirstOrDefault(d => d.Drive == drive) ?? DriveFilters[0];
        }
        finally
        {
            _loadingFilters = false;
        }
    }

    private void UpdateTotals()
    {
        var games = _allRows.Select(r => r.Game).ToList();
        var totals = GameLibraryMath.Totals(games);
        GameCountText = totals.GameCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        int launchers = games.Select(g => g.Launcher).Distinct().Count();
        GameCountSubtitle = launchers == 1 ? Loc.T("From 1 launcher") : Loc.F("From {0} launchers", launchers);
        SpaceUsedText = ByteSize.Format(totals.SizeBytes);
        SpaceUsedSubtitle = totals.UnknownSizeCount > 0
            ? Loc.F("{0} still being measured", totals.UnknownSizeCount)
            : totals.Drives.Count == 1 ? Loc.T("On 1 drive") : Loc.F("On {0} drives", totals.Drives.Count);
        var recent = GameLibraryMath.MostRecentlyPlayed(games);
        LastPlayedText = recent?.Name ?? "—";
        LastPlayedSubtitle = recent is null ? Loc.T("Only Steam records this") : GameText.LastPlayed(recent.LastPlayed);

        DriveTotals.Clear();
        foreach (var drive in totals.Drives)
        {
            DriveTotals.Add(new DriveTotalRow(drive.Drive,
                Loc.F("{0} {1} games, {2}", drive.Drive, drive.GameCount, ByteSize.Format(drive.SizeBytes))));
        }
    }

    private void ApplyFilter()
    {
        if (_loadingFilters)
        {
            return;
        }

        var byId = _allRows.ToDictionary(r => r.Game.Id, StringComparer.Ordinal);
        var filtered = GameLibraryMath.Filter(_allRows.Select(r => r.Game), SelectedLauncher?.Launcher, SelectedDrive?.Drive, SearchText);
        var sorted = GameLibraryMath.Sort(filtered, SelectedSort?.Order ?? GameSortOrder.Name);
        Games.Clear();
        foreach (var game in sorted)
        {
            if (byId.TryGetValue(game.Id, out var row))
            {
                Games.Add(row);
            }
        }

        ListSummary = Loc.F("{0} of {1} games shown", Games.Count, _allRows.Count);
        OnPropertyChanged(nameof(HasNoMatches));
    }

    /// <summary>After a scan: cover art and icons, then the sizes the launchers do not record. Neither blocks the page.</summary>
    private void StartBackgroundWork()
    {
        _backgroundCts?.Cancel();
        _backgroundCts = new CancellationTokenSource();
        var ct = _backgroundCts.Token;
        _ = LoadArtAsync(ct);
        _ = MeasureSizesAsync(ct);
    }

    private async Task LoadArtAsync(CancellationToken ct)
    {
        try
        {
            var requests = _allRows.Select(r => (r.Game.Id, r.Game.CoverImagePath, r.Game.ExePath)).ToList();
            var art = await GameArtLoader.LoadAsync(requests, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var byId = _allRows.ToDictionary(r => r.Game.Id, StringComparer.Ordinal);
            foreach (var (id, image, isIcon) in art)
            {
                if (byId.TryGetValue(id, out var row))
                {
                    row.ArtIsIcon = isIcon;
                    row.Art = image;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Game art could not be loaded.");
        }
    }

    private async Task MeasureSizesAsync(CancellationToken ct)
    {
        var games = _allRows.Select(r => r.Game).ToList();
        int missing = games.Count(g => g.SizeBytes is null);
        if (missing == 0)
        {
            return;
        }

        MeasuringText = Loc.F("Measuring the folders of {0} games…", missing);
        try
        {
            var measured = await _library.MeasureMissingSizesAsync(games, null, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var byId = measured.ToDictionary(g => g.Id, StringComparer.Ordinal);
            foreach (var row in _allRows)
            {
                if (byId.TryGetValue(row.Game.Id, out var game) && game.SizeBytes != row.Game.SizeBytes)
                {
                    row.Update(game);
                }
            }

            UpdateTotals();
            if (SelectedSort?.Order == GameSortOrder.Size)
            {
                ApplyFilter();
            }
        }
        catch (OperationCanceledException)
        {
            // A new scan started.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Game folder sizes could not be measured.");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                MeasuringText = null;
            }
        }
    }
}
