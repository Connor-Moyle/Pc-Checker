using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Network;
using PcChecker.Core.Network.Resolvers;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// DNS page: the DNS servers each connected adapter uses, a benchmark of real lookups against them and the big public
/// services with a plain recommendation, and switching servers (optionally encrypted) with an exact Undo. Changes are
/// in DnsViewModel.Actions.cs.
/// </summary>
public sealed partial class DnsViewModel : ViewModelBase
{
    private readonly IDnsService _dns;
    private readonly INetworkService _network;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<DnsViewModel> _logger;
    private DnsStatus _status = DnsStatus.Empty;
    private DnsRecommendation? _advice;

    public DnsViewModel(ISettingsService settings, IDnsService dns, INetworkService network, IDialogService dialogs,
        INotificationService notifications, ILogger<DnsViewModel> logger)
        : base(settings)
    {
        _dns = dns;
        _network = network;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        Choices =
        [
            .. DnsResolverCatalog.Providers.Select(p => new DnsChoice(p.Id, p.Name, $"{p.Primary}, {p.Secondary} · {DnsText.Blocks(DnsResolverCatalog.FilteringOf(p.Id))}", p)),
            new DnsChoice(DnsChoice.CustomId, Loc.T("Other servers (type them in)"), Loc.T("For example your own Pi-hole or a provider not listed here."), null),
        ];
        _selectedChoice = Choices[0];
    }

    public ObservableCollection<DnsAdapterRow> Adapters { get; } = [];

    public ObservableCollection<DnsResultRow> Results { get; } = [];

    public IReadOnlyList<DnsChoice> Choices { get; }

    [ObservableProperty] private string _subtitle = Loc.T("Find the fastest DNS server for your connection and switch to it safely.");
    [ObservableProperty] private bool _hasStatus;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _hasNoAdapters;
    [ObservableProperty] private string _currentDnsText = "—";
    [ObservableProperty] private string _currentDnsSubtitle = string.Empty;
    [ObservableProperty] private string _encryptionText = "—";
    [ObservableProperty] private string _fastestText = "—";
    [ObservableProperty] private string _fastestSubtitle = Loc.T("Run the test to find out");
    [ObservableProperty] private string _yourSpeedText = "—";
    [ObservableProperty] private string _yourSpeedSubtitle = Loc.T("Typical lookup time");
    [ObservableProperty] private string _recommendationTitle = string.Empty;
    [ObservableProperty] private string _recommendationText = string.Empty;
    [ObservableProperty] private string _alternativesText = string.Empty;
    [ObservableProperty] private string _resultsInfo = string.Empty;
    [ObservableProperty] private bool _encryptionSupported;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecommendationCommand))]
    [NotifyPropertyChangedFor(nameof(HasRecommendationAction))]
    private string? _applyRecommendationText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCustomFields))]
    [NotifyPropertyChangedFor(nameof(ChoiceHasIpv6))]
    [NotifyPropertyChangedFor(nameof(CanEncryptChoice))]
    private DnsChoice? _selectedChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEncryptChoice))]
    private string? _customPrimary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEncryptChoice))]
    private string? _customSecondary;

    [ObservableProperty] private bool _setIpv6 = true;
    [ObservableProperty] private bool _encrypt;
    [ObservableProperty] private string? _extraServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseSelectedCommand))]
    private DnsResultRow? _selectedResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private string? _resultMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private bool _resultSuccess;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoLastChangeCommand))]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    private string? _undoText;

    public bool ShowCustomFields => SelectedChoice?.IsCustom == true;

    public bool ChoiceHasIpv6 => SelectedChoice?.Provider is { } p && p.Servers6.Count > 0;

    /// <summary>Encryption is offered for the public services (all of them have a DNS-over-HTTPS address) on Windows 11.</summary>
    public bool CanEncryptChoice => EncryptionSupported && CurrentRequest(includeIpv6: false) is { } request && DnsSwitchPlan.CanEncrypt(request);

    public bool ShowSuccessResult => ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public bool ShowFailureResult => !ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public bool HasRecommendationAction => !string.IsNullOrWhiteSpace(ApplyRecommendationText);

    public bool CanUndo => !string.IsNullOrWhiteSpace(UndoText);

    partial void OnEncryptionSupportedChanged(bool value) => OnPropertyChanged(nameof(CanEncryptChoice));

    /// <summary>
    /// Reading the DNS settings changes nothing, so the page always fills itself. The benchmark sends a few hundred
    /// lookups, so a real PC waits for the button; demo mode runs it so the page shows results.
    /// </summary>
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await LoadStatusAsync(cancellationToken);
        if (AppOptions.Current.IsDemo)
        {
            await RunBenchmarkAsync(cancellationToken);
        }
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadStatusAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "DNS page operation failed.");

    [RelayCommand]
    private Task ReloadAsync() => LoadStatusAsync(CancellationToken.None);

    [RelayCommand]
    private Task BenchmarkAsync() => RunBenchmarkAsync(CancellationToken.None);

    private Task LoadStatusAsync(CancellationToken outer) => RunBusyAsync(async busy =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busy, outer);
        await LoadStatusCoreAsync(linked.Token);
    }, Loc.T("Reading your DNS settings…"));

    private async Task LoadStatusCoreAsync(CancellationToken ct)
    {
        _status = await _dns.GetStatusAsync(ct);
        // Keep the ticks the user removed; new adapters start ticked.
        var unticked = Adapters.Where(a => !a.IsSelected).Select(a => a.Info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Adapters.Clear();
        foreach (var adapter in _status.Adapters)
        {
            Adapters.Add(new DnsAdapterRow(adapter) { IsSelected = !unticked.Contains(adapter.Id) });
        }

        EncryptionSupported = _status.EncryptionSupported;
        HasNoAdapters = Adapters.Count == 0;
        HasStatus = true;
        UpdateStatusTiles();
        UndoText = _dns.LastChange is { } last
            ? Loc.F("Puts back what was there before \"{0}\" ({1}):", last.ChangeLabel, last.Taken.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)) + "\n" + DnsText.Previous(last)
            : null;
    }

    private void UpdateStatusTiles()
    {
        var main = _status.Adapters.FirstOrDefault();
        if (main is null)
        {
            CurrentDnsText = Loc.T("Not connected");
            CurrentDnsSubtitle = Loc.T("No network adapter with internet access");
            EncryptionText = "—";
            return;
        }

        CurrentDnsText = main.IsAutomatic4 ? Loc.T("Automatic") : DnsText.ProviderName(main.Servers4) ?? Loc.T("Set by hand");
        CurrentDnsSubtitle = $"{main.Name}: {string.Join(", ", main.AllServers.Take(2))}";
        EncryptionText = DnsText.Encryption(main.Encryption);
    }

    private Task RunBenchmarkAsync(CancellationToken outer) => RunBusyAsync(async busy =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busy, outer);
        if (!HasStatus)
        {
            await LoadStatusCoreAsync(linked.Token);
        }

        string? extra = DnsResolverCatalog.IsServerAddress(ExtraServer) ? ExtraServer : null;
        var targets = DnsResolverCatalog.BuildTargets(
            _status.Adapters.SelectMany(a => a.AllServers),
            _status.Adapters.SelectMany(a => a.RouterServers),
            extra);
        BusyText = Loc.F("Testing {0} DNS servers… this takes up to 30 seconds.", targets.Count);
        var progress = new Progress<ProgressUpdate>(p =>
            BusyText = Loc.F("Testing DNS servers… {0} % (last finished: {1})", Math.Round(p.Percent ?? 0), p.Detail ?? string.Empty));
        var report = await _dns.BenchmarkAsync(targets, progress, linked.Token);
        ShowReport(report);
    }, Loc.T("Testing DNS servers…"));

    private void ShowReport(DnsBenchmarkReport report)
    {
        var scored = report.Results.Select(r => (Result: r, Score: DnsRecommendationRules.Score(r))).ToList();
        double? best = scored.Where(s => s.Score is not null && DnsRecommendationRules.IsReliable(s.Result)).Select(s => s.Score).Min();
        Results.Clear();
        foreach (var (result, score) in scored.OrderBy(s => s.Score is null).ThenBy(s => s.Score))
        {
            Results.Add(new DnsResultRow(result, score, best));
        }

        HasResults = Results.Count > 0;
        ResultsInfo = Loc.F("Tested {0} servers in {1} seconds on {2}.", report.Results.Count, Math.Round(report.Duration.TotalSeconds), report.Finished.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))
            + (report.StoppedEarly ? " " + Loc.T("The test stopped at its time limit; some servers show fewer lookups.") : string.Empty);
        Subtitle = Loc.F("Tested {0}", report.Finished.LocalDateTime.ToString("t", CultureInfo.CurrentCulture));
        ShowAdvice(DnsRecommendationRules.Recommend(report.Results));
    }

    private void ShowAdvice(DnsRecommendation advice)
    {
        _advice = advice;
        var best = advice.Best;
        var current = advice.Current;
        FastestText = best is null ? "—" : DnsText.Ms(best.Cached.MedianMs);
        FastestSubtitle = best is null ? Loc.T("No server answered") : NameOf(best);
        YourSpeedText = current is null ? "—" : DnsText.Ms(current.Cached.MedianMs);
        YourSpeedSubtitle = current is null ? Loc.T("Your DNS was not tested") : Loc.F("{0}, new sites: {1}", NameOf(current), DnsText.Ms(current.Uncached.MedianMs));
        ApplyRecommendationText = null;

        switch (advice.Kind)
        {
            case DnsAdviceKind.NoData:
                RecommendationTitle = Loc.T("No DNS server answered");
                RecommendationText = Loc.T("Check that you are connected to the internet, then test again. A firewall, VPN or security program can also block DNS tests.");
                break;

            case DnsAdviceKind.KeepCurrent:
                RecommendationTitle = Loc.T("Your DNS is already fast");
                RecommendationText = advice.SavingMs is { } small && small > 0
                    ? Loc.F("Your DNS answers in {0}. The fastest server tested would save only {1} per lookup, which you would not notice. No change needed.", DnsText.Ms(current!.Cached.MedianMs), DnsText.Ms(small))
                    : Loc.F("{0} was the fastest reliable server tested, at {1} per lookup. No change needed.", NameOf(best!), DnsText.Ms(best!.Cached.MedianMs));
                break;

            case DnsAdviceKind.UseAutomatic:
                RecommendationTitle = Loc.T("Go back to automatic DNS");
                RecommendationText = Loc.F("Your router's DNS answered in {0}, faster than the servers set by hand ({1}). Going back to automatic uses it.",
                    DnsText.Ms(best!.Cached.MedianMs), DnsText.Ms(current?.Cached.MedianMs));
                ApplyRecommendationText = Loc.T("Reset to automatic");
                break;

            default:
                RecommendationTitle = Loc.F("Switch to {0}", NameOf(best!));
                RecommendationText = advice.CurrentUnreliable && current is not null
                    ? Loc.F("Your DNS missed {0} % of lookups, which shows up as pages that sometimes do not load. {1} answered every one in {2} on average.",
                        Math.Round((1 - current.Reliability) * 100), NameOf(best!), DnsText.Ms(best!.Cached.MedianMs))
                    : current is null
                        ? Loc.F("{0} was the fastest reliable server tested, at {1} per lookup.", NameOf(best!), DnsText.Ms(best!.Cached.MedianMs))
                        : Loc.F("{0} answered in {1} on average, {2} faster than your DNS ({3}). Websites and games start connecting a little sooner.",
                            NameOf(best!), DnsText.Ms(best!.Cached.MedianMs), DnsText.Ms(advice.SavingMs), DnsText.Ms(current.Cached.MedianMs));
                ApplyRecommendationText = Loc.F("Use {0}", NameOf(best!));
                break;
        }

        AlternativesText = advice.FilteringAlternatives.Count == 0
            ? string.Empty
            : Loc.T("Want something blocked for you?") + " " + string.Join("; ", advice.FilteringAlternatives.Take(3).Select(a =>
                Loc.F("{0} blocks {1} ({2})", NameOf(a), DnsText.Blocks(a.Target.Filtering).ToLower(CultureInfo.CurrentCulture), DnsText.Ms(a.Cached.MedianMs)))) + ".";
    }

    private static string NameOf(DnsServerResult result) =>
        result.Target.ProviderName
        ?? (result.Target.IsRouter ? Loc.F("your router ({0})", result.Target.Address) : result.Target.IsCustom ? Loc.F("your server ({0})", result.Target.Address) : result.Target.Address);
}
