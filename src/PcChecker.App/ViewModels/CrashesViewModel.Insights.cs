using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Crashes;
using PcChecker.Core.Games;
using PcChecker.Core.History;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Crashes page's second pass: after the timeline is shown, the context (History samples, Action Journal, Windows
/// Update history), the patterns across crashes, the installed games and the blamed driver's details are loaded off
/// the UI thread and folded into each crash's insight and the summary card.
/// </summary>
public sealed partial class CrashesViewModel
{
    private readonly Dictionary<string, CrashContext> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CrashDriverDetails?> _driverDetails = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CrashPattern> _patterns = [];
    private IReadOnlyList<InstalledGame> _gameList = [];
    private int _insightGeneration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summaryHeadline;

    [ObservableProperty]
    private string? _summaryDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummaryFix))]
    private CrashFixRow? _summaryFix;

    [ObservableProperty]
    private bool _isLoadingInsights;

    private string? _summaryCrashId;

    public bool HasSummary => !string.IsNullOrWhiteSpace(SummaryHeadline);

    public bool HasSummaryFix => SummaryFix is not null;

    /// <summary>Loads the context and games, then rebuilds every insight, the patterns and the summary card.</summary>
    private async Task LoadInsightsAsync(CrashReport report)
    {
        int generation = Interlocked.Increment(ref _insightGeneration);
        IsLoadingInsights = true;
        try
        {
            var contextData = await _crashes.LoadContextAsync(report.Events, CancellationToken.None);
            var games = await LoadGamesAsync();
            var thresholds = HistoryThresholds.From(Settings.Current.Monitoring);
            var rows = _allRows.ToList();
            var drivers = new Dictionary<string, CrashDriverDetails?>(_driverDetails, StringComparer.OrdinalIgnoreCase);
            var (contexts, patterns, insights) = await Task.Run(() =>
            {
                var map = new Dictionary<string, CrashContext>(StringComparer.Ordinal);
                foreach (var crash in report.Events.Where(CrashPatternRules.IsSerious))
                {
                    map[crash.Id] = CrashContextRules.Build(crash, contextData.Samples, report.SystemEvents, contextData.Journal, thresholds);
                }

                var found = CrashPatternRules.Find(new CrashPatternInput
                {
                    Crashes = report.Events,
                    SystemEvents = report.SystemEvents,
                    Updates = contextData.Updates,
                    Journal = contextData.Journal,
                    Contexts = map,
                });
                var built = rows.Select(r =>
                {
                    var game = CrashGameRules.FindGame(r.Crash.AppPath, games);
                    return (Row: r, Game: game, Insight: BuildInsight(r.Crash, report, map, found, drivers, game));
                }).ToList();
                return (map, found, built);
            });

            if (generation != _insightGeneration)
            {
                return;
            }

            _contexts.Clear();
            foreach (var (id, context) in contexts)
            {
                _contexts[id] = context;
            }

            _patterns = patterns;
            _gameList = games;
            foreach (var (row, game, insight) in insights)
            {
                row.Game = game;
                row.Insight = insight;
            }

            // A driver looked up while this ran (InspectDriverAsync) is not in the snapshot: rebuild those rows with it.
            foreach (var row in _allRows)
            {
                if (DriverModule(row.Crash) is { } key && _driverDetails.TryGetValue(key, out var now) && !ReferenceEquals(now, drivers.GetValueOrDefault(key)))
                {
                    Rebuild(row);
                }
            }

            UpdateSummaryCard();
            RefreshDetails();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Crash context could not be loaded.");
        }
        finally
        {
            if (generation == _insightGeneration)
            {
                IsLoadingInsights = false;
            }
        }
    }

    private async Task<IReadOnlyList<InstalledGame>> LoadGamesAsync()
    {
        if (!_allRows.Any(r => r.Crash.AppPath is { Length: > 0 }))
        {
            return [];
        }

        try
        {
            var scan = await _fix.Games.ScanAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            return scan.Games;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Games could not be listed for the Crashes page.");
            return [];
        }
    }

    /// <summary>Rebuilds one row's insight from everything known now (context, patterns, driver, game).</summary>
    private void Rebuild(CrashRow row)
    {
        if (_report is { } report)
        {
            row.Insight = BuildInsight(row.Crash, report, _contexts, _patterns, _driverDetails, row.Game);
        }
    }

    /// <summary>One crash's insight from the given context, patterns, driver details and game (safe off the UI thread).</summary>
    private CrashInsight BuildInsight(CrashEvent crash, CrashReport report, IReadOnlyDictionary<string, CrashContext> contexts, IReadOnlyList<CrashPattern> patterns,
        IReadOnlyDictionary<string, CrashDriverDetails?> drivers, InstalledGame? game)
    {
        var key = DriverModule(crash);
        var driver = key is not null && drivers.TryGetValue(key, out var d) ? WithOffer(d) : null;
        return CrashInsightBuilder.Build(crash, report.OverclockApplied, contexts.GetValueOrDefault(crash.Id), patterns, driver, game is not null, report.Events, Loc.Current);
    }

    /// <summary>The driver file a crash points at (blamed third-party driver or the reset graphics driver), or null.</summary>
    private static string? DriverModule(CrashEvent crash) => crash.Kind switch
    {
        CrashKind.BlueScreen when crash.Blame is { } b => b.Module,
        CrashKind.DriverReset when crash.Driver is { Length: > 0 } d => d.Contains('.') ? d : d + ".sys",
        CrashKind.AppCrash when crash.ModuleName is { } m && FaultingModuleCatalog.Find(m)?.Category == ModuleCategory.GraphicsDriver => GraphicsKernelDriver(m),
        _ => null,
    };

    /// <summary>The kernel driver behind a graphics user-mode driver file (nvwgf2umx.dll → nvlddmkm.sys).</summary>
    private static string? GraphicsKernelDriver(string module) =>
        module.StartsWith("nv", StringComparison.OrdinalIgnoreCase) ? "nvlddmkm.sys"
        : module.StartsWith("ig", StringComparison.OrdinalIgnoreCase) ? "igdkmdn64.sys"
        : module.StartsWith("am", StringComparison.OrdinalIgnoreCase) || module.StartsWith("at", StringComparison.OrdinalIgnoreCase) ? "amdkmdag.sys"
        : null;

    /// <summary>Looks up the selected crash's driver once (file, maker, device) and folds it into the insight.</summary>
    private async Task InspectDriverAsync(CrashRow row)
    {
        var module = DriverModule(row.Crash);
        if (module is null || _driverDetails.ContainsKey(module))
        {
            return;
        }

        _driverDetails[module] = null;
        try
        {
            var details = await _crashes.InspectDriverAsync(module, CancellationToken.None);
            _driverDetails[module] = details;
            foreach (var other in _allRows.Where(r => string.Equals(DriverModule(r.Crash), module, StringComparison.OrdinalIgnoreCase)))
            {
                Rebuild(other);
            }

            UpdateSummaryCard();
            if (SelectedRow is not null && string.Equals(DriverModule(SelectedRow.Crash), module, StringComparison.OrdinalIgnoreCase))
            {
                RefreshDetails();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Driver {Module} could not be inspected.", module);
        }
    }

    /// <summary>Adds the newest driver offer from the Drivers page's last search, when the service did not know one.</summary>
    private CrashDriverDetails? WithOffer(CrashDriverDetails? details)
    {
        if (details is null || details.NewerVersion is not null)
        {
            return details;
        }

        var search = _fix.DriverSearch.Last;
        var offer = CrashDriverRules.NewerOffer(search, details.DeviceId, details.DeviceName);
        return details with
        {
            NewerVersion = offer?.Version,
            NewerSource = offer?.Source,
            UpdatesChecked = details.UpdatesChecked || CrashDriverRules.SearchCovered(search, details.DeviceId, details.DeviceName),
        };
    }

    /// <summary>The Drivers page finished a search: show its newer versions here too.</summary>
    private void RefreshDriverOffers()
    {
        if (_allRows.Count == 0)
        {
            return;
        }

        foreach (var row in _allRows)
        {
            Rebuild(row);
        }

        UpdateSummaryCard();
        RefreshDetails();
    }

    private HashSet<string> SolvedIds()
    {
        try
        {
            return Settings.Current.Crashes?.SolvedIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Solved crash ids could not be read.");
            return [];
        }
    }

    private void UpdateSummaryCard()
    {
        var insights = _allRows.Select(r => r.Insight).ToList();
        var card = CrashSummaryRules.Build(insights, DateTimeOffset.Now, SolvedIds());
        if (card is null)
        {
            SummaryHeadline = null;
            SummaryDetail = null;
            SummaryFix = null;
            _summaryCrashId = null;
            return;
        }

        SummaryHeadline = card.Headline.Text(Loc.Current);
        SummaryDetail = card.Detail.Text(Loc.Current);
        _summaryCrashId = card.CrashId;
        var fixCrashId = card.FixCrashId ?? card.CrashId;
        var row = _allRows.FirstOrDefault(r => r.Crash.Id == fixCrashId);
        SummaryFix = card.TopFix is { } fix && row is not null ? new CrashFixRow(fix, row, 1) : null;
    }

    [RelayCommand]
    private void ShowSummaryCrash()
    {
        if (_summaryCrashId is null)
        {
            return;
        }

        if (Rows.FirstOrDefault(r => r.Crash.Id == _summaryCrashId) is not { } row)
        {
            ClearFilters();
            row = Rows.FirstOrDefault(r => r.Crash.Id == _summaryCrashId);
        }

        if (row is not null)
        {
            SelectedRow = row;
        }
    }
}
