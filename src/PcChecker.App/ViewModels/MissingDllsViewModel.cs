using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.GameRuntimes;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Missing DLLs page: lists every DLL old games commonly need with its 32-bit and 64-bit state, the official package
/// that provides each one, and scans a game folder for the DLLs its programs import. Install buttons install the
/// whole official package (never a single DLL file) after a confirmation that names the source and the signer.
/// </summary>
public sealed partial class MissingDllsViewModel : ViewModelBase, IRefreshablePage
{
    private const string AllPackages = "*";

    private readonly IGameRuntimeService _runtimes;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<MissingDllsViewModel> _logger;
    private readonly List<MissingDllRow> _allRows = [];
    private RuntimeScanResult _scan = RuntimeScanResult.Empty;
    private bool? _wingetAvailable;
    private string? _lastGameTarget;

    [ObservableProperty]
    private bool _hasScanned;

    [ObservableProperty]
    private string _subtitle = Loc.T("Find and install the official runtimes old games need. Never single DLL files.");

    [ObservableProperty]
    private string _missingText = "—";

    [ObservableProperty]
    private string _missingSubtitle = Loc.T("64-bit / 32-bit programs");

    [ObservableProperty]
    private int _packagesInstalled;

    [ObservableProperty]
    private string _packagesInstalledUnit = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallAllMissingCommand))]
    private int _packagesMissing;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _showMissingOnly;

    [ObservableProperty]
    private DllFilterOption? _selectedFilter;

    [ObservableProperty]
    private string _listSummary = string.Empty;

    [ObservableProperty]
    private bool _showExplanation = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGameResult))]
    private GameScanResult? _gameResult;

    [ObservableProperty]
    private string _gameSummary = string.Empty;

    [ObservableProperty]
    private string _gameNeedsText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallGameNeedsCommand))]
    private bool _gameHasNeeds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private string? _resultMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private bool _resultSuccess;

    [ObservableProperty]
    private bool _rebootRequired;

    public MissingDllsViewModel(ISettingsService settings, IGameRuntimeService runtimes, IDialogService dialogs, INotificationService notifications,
        INavigationService navigation, ILogger<MissingDllsViewModel> logger)
        : base(settings)
    {
        _runtimes = runtimes;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;

        Filters.Add(new DllFilterOption(AllPackages, Loc.T("All packages")));
        foreach (var family in RuntimeCatalog.Packages.GroupBy(p => p.FamilyId))
        {
            Filters.Add(new DllFilterOption(family.Key, MissingDllRow.FamilyName(family.First())));
        }

        _selectedFilter = Filters[0];
    }

    ICommand IRefreshablePage.RefreshKeyCommand => RescanCommand;

    public ObservableCollection<DllFilterOption> Filters { get; } = [];

    public ObservableCollection<MissingDllRow> Dlls { get; } = [];

    public ObservableCollection<RuntimePackageRow> Packages { get; } = [];

    public ObservableCollection<GameImportRow> GameImports { get; } = [];

    public bool HasGameResult => GameResult is not null;

    public bool HasNoDlls => HasScanned && Dlls.Count == 0;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public bool ShowSuccessResult => ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public bool ShowFailureResult => !ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    /// <summary>Checks the PC right away (it only reads files and the registry); demo mode also shows a sample game scan.</summary>
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunScanAsync(cancellationToken);
        if (AppOptions.Current.IsDemo && !cancellationToken.IsCancellationRequested)
        {
            await RunBusyAsync(async ct =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                await ScanGameCoreAsync(DemoGameRuntimeService.DemoGameFolder, linked.Token);
            }, Loc.T("Reading the game's files…"));
        }
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunScanAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Missing DLLs page operation failed.");

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnShowMissingOnlyChanged(bool value) => ApplyFilter();

    partial void OnSelectedFilterChanged(DllFilterOption? value) => ApplyFilter();

    [RelayCommand]
    private Task RescanAsync() => RunScanAsync(CancellationToken.None);

    private Task RunScanAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        await ScanCoreAsync(linked.Token);
    }, Loc.T("Checking which DLLs are installed…"));

    private async Task ScanCoreAsync(CancellationToken ct)
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        var scan = await _runtimes.ScanAsync(progress, ct);
        _wingetAvailable ??= await _runtimes.IsWingetAvailableAsync(ct);
        Apply(scan);
    }

    private void Apply(RuntimeScanResult scan)
    {
        _scan = scan;
        _allRows.Clear();
        foreach (var row in scan.Dlls)
        {
            _allRows.Add(new MissingDllRow(row, RuntimeStatusRules.InstallTargets(row), RuntimeStatusRules.CountsAsProblem(row, scan.IsNEdition)));
        }

        Packages.Clear();
        foreach (var p in scan.Packages.OrderBy(p => PackageOrder(p.State)))
        {
            Packages.Add(new RuntimePackageRow(p));
        }

        var (m32, m64) = RuntimeStatusRules.MissingCounts(scan);
        MissingText = $"{m64} / {m32}";
        var applicable = scan.Packages.Where(p => p.State != PackageState.NotApplicable).ToList();
        PackagesInstalled = applicable.Count(p => p.State == PackageState.Installed);
        PackagesInstalledUnit = Loc.F("of {0}", applicable.Count);
        PackagesMissing = RuntimeStatusRules.MissingInstallable(scan).Count;
        Warnings = scan.Warnings.Count > 0 ? string.Join(Environment.NewLine, scan.Warnings) : null;
        Subtitle = scan.ScannedAt == DateTimeOffset.MinValue
            ? Subtitle
            : Loc.F("Checked {0:t}. {1} DLLs from {2} official packages.", scan.ScannedAt.LocalDateTime, scan.Dlls.Count,
                scan.Packages.Count(p => p.Package.CanInstall));
        HasScanned = true;
        ApplyFilter();
    }

    private static int PackageOrder(PackageState state) => state switch
    {
        PackageState.Missing => 0,
        PackageState.Partial => 1,
        PackageState.Installed => 2,
        _ => 3,
    };

    private void ApplyFilter()
    {
        var search = SearchText?.Trim();
        string family = SelectedFilter?.Key ?? AllPackages;
        var visible = _allRows.Where(r =>
            (family == AllPackages || r.FamilyId == family)
            && (!ShowMissingOnly || (r.IsMissing && r.CountsAsProblem))
            && (string.IsNullOrEmpty(search) || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.PackageName.Contains(search, StringComparison.OrdinalIgnoreCase)));

        Dlls.Clear();
        foreach (var row in visible)
        {
            Dlls.Add(row);
        }

        ListSummary = Loc.F("{0} of {1} DLLs shown", Dlls.Count, _allRows.Count);
        OnPropertyChanged(nameof(HasNoDlls));
    }

    [RelayCommand]
    private async Task ScanGameAsync()
    {
        var choice = await _dialogs.ChooseAsync(Loc.T("Scan a game"),
            Loc.T("Pick the game's folder, or its main .exe file. PC Checker reads which DLLs the game's programs load. Nothing in the folder is run or changed."),
            Loc.T("Pick folder"), Loc.T("Pick .exe file"));
        string? target = choice switch
        {
            DialogChoice.Primary => await _dialogs.PickFolderAsync(Loc.T("Choose the game's folder")),
            DialogChoice.Secondary => await _dialogs.OpenFileAsync(Loc.T("Choose the game's program"), Loc.T("Programs") + "|*.exe"),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await RunBusyAsync(ct => ScanGameCoreAsync(target, ct), Loc.T("Reading the game's files…"));
    }

    private async Task ScanGameCoreAsync(string target, CancellationToken ct)
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        var result = await _runtimes.ScanGameAsync(target, progress, ct);
        _lastGameTarget = target;
        ApplyGame(result);
    }

    private void ApplyGame(GameScanResult result)
    {
        GameResult = result;
        GameImports.Clear();
        foreach (var import in result.Imports)
        {
            GameImports.Add(new GameImportRow(import));
        }

        string bits = (result.Has32Bit, result.Has64Bit) switch
        {
            (true, true) => Loc.T("32-bit and 64-bit programs"),
            (true, false) => Loc.T("a 32-bit game"),
            (false, true) => Loc.T("a 64-bit game"),
            _ => Loc.T("no Windows programs"),
        };
        GameSummary = Loc.F("{0}: {1}, {2} files read, {3} DLLs needed, {4} problems.", result.GameName, bits, result.FilesRead, result.Imports.Count,
            result.ProblemCount) + (result.HitFileLimit ? " " + Loc.F("Only the first {0} files were read.", GameScanRules.MaxFiles) : string.Empty);
        var needed = result.NeededPackageIds.Select(RuntimeCatalog.Package).OfType<RuntimePackage>().ToList();
        GameHasNeeds = needed.Count > 0;
        GameNeedsText = needed.Count > 0
            ? Loc.F("To fix it, install: {0}.", string.Join(", ", needed.Select(p => p.Name)))
            : result.ProblemCount > 0
                ? Loc.T("Nothing PC Checker can install would help. Verify the game files in its launcher, or reinstall the game.")
                : Loc.T("Everything this game loads is there.");
    }

    [RelayCommand]
    private void CloseGameResult()
    {
        GameResult = null;
        GameImports.Clear();
        GameHasNeeds = false;
        _lastGameTarget = null;
    }

    [RelayCommand(CanExecute = nameof(GameHasNeeds))]
    private Task InstallGameNeedsAsync()
    {
        var needed = GameResult?.NeededPackageIds.Select(RuntimeCatalog.Package).OfType<RuntimePackage>().ToList() ?? [];
        return InstallPackagesAsync(needed, Loc.F("Install what {0} needs?", GameResult?.GameName ?? Loc.T("the game")));
    }

    [RelayCommand(CanExecute = nameof(CanInstallAllMissing))]
    private Task InstallAllMissingAsync() =>
        InstallPackagesAsync(RuntimeStatusRules.MissingInstallable(_scan), Loc.T("Install every missing runtime?"));

    private bool CanInstallAllMissing() => PackagesMissing > 0;

    [RelayCommand]
    private Task InstallDllAsync(MissingDllRow? row) =>
        row is { CanInstall: true } ? InstallPackagesAsync(row.InstallTargets, Loc.F("Install the package that contains {0}?", row.Name)) : Task.CompletedTask;

    [RelayCommand]
    private Task PackageActionAsync(RuntimePackageRow? row)
    {
        if (row is null || !row.CanAct)
        {
            return Task.CompletedTask;
        }

        switch (row.Package.Kind)
        {
            case RuntimeInstallKind.WindowsSettings:
                Open(row.Package.InfoUrl ?? "ms-settings:optionalfeatures");
                return Task.CompletedTask;
            case RuntimeInstallKind.GraphicsDriver:
                _navigation.Navigate(typeof(DriversPage));
                return Task.CompletedTask;
            default:
                return InstallPackagesAsync([row.Package], Loc.F("Install {0}?", row.Name));
        }
    }

    [RelayCommand]
    private void OpenVendorPage(RuntimePackageRow? row)
    {
        if (row?.Package.InfoUrl is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Open(url);
        }
    }

    [RelayCommand]
    private void OpenWindowsFeatures() => Open("optionalfeatures.exe");

    [RelayCommand]
    private void DismissReboot() => RebootRequired = false;

    private async Task InstallPackagesAsync(IReadOnlyList<RuntimePackage> requested, string title)
    {
        var order = RuntimeInstallRules.InstallOrder(requested);
        if (order.Count == 0)
        {
            return;
        }

        bool winget = _wingetAvailable ?? false;
        if (!await _dialogs.ConfirmAsync(title, RuntimeInstallRules.ConfirmationText(order, winget), Loc.T("Install")))
        {
            return;
        }

        var lines = new List<string>();
        bool allOk = true;
        bool restart = false;
        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
            foreach (var package in order)
            {
                if (ct.IsCancellationRequested)
                {
                    lines.Add(Loc.F("{0}: not started (cancelled).", package.Name));
                    allOk = false;
                    continue;
                }

                BusyText = Loc.F("Installing {0}…", package.Name);
                OperationResult result;
                try
                {
                    result = await _runtimes.InstallAsync(package, progress, ct);
                }
                catch (OperationCanceledException)
                {
                    result = OperationResult.Fail(Loc.T("Cancelled."));
                }

                lines.Add(package.Name + ": " + result.Message);
                allOk &= result.Success;
                restart |= result.RebootRequired;
            }

            // Look again so the list shows what is really there now, even after a cancel.
            BusyText = Loc.T("Checking which DLLs are installed…");
            await ScanCoreAsync(CancellationToken.None);
            if (_lastGameTarget is { } game)
            {
                await ScanGameCoreAsync(game, CancellationToken.None);
            }
        }, Loc.T("Installing…"));

        RebootRequired |= restart;
        ResultSuccess = allOk;
        ResultMessage = string.Join(Environment.NewLine, lines);
        if (allOk)
        {
            _notifications.Success(Loc.T("Runtimes installed"), order.Count == 1 ? lines.FirstOrDefault() ?? string.Empty : Loc.F("{0} packages installed.", order.Count));
        }
        else
        {
            _notifications.Warning(Loc.T("Not everything was installed"), Loc.T("See the message at the top of the page."));
        }
    }

    private void Open(string target)
    {
        var result = ShellLauncher.Open(target);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }
}
