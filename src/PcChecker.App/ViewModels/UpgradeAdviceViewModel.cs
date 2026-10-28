using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Assistant;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;
using PcChecker.Core.Upgrades;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Upgrade advice page: the PC at a glance, the setup problems that cost nothing to fix, the ranked upgrades with why
/// and how much they help, the gaming balance from History and Windows 11 readiness. Everything here only reads; the
/// buttons open other pages, copy a summary or hand a question to Ask Claude.
/// </summary>
public sealed partial class UpgradeAdviceViewModel : ViewModelBase
{
    /// <summary>Pages that may not be in this build, and the page to open instead.</summary>
    private static readonly IReadOnlyDictionary<string, string> PageFallbacks = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [UpgradeAdvisor.RamTuningPage] = "memory",
    };

    private readonly IUpgradeAdvisorService _advisor;
    private readonly IPcAssistantService _assistant;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly ILogger<UpgradeAdviceViewModel> _logger;

    [ObservableProperty]
    private string _subtitle = Loc.T("What to upgrade next, and what to fix first for free.");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskClaudeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySummaryCommand))]
    private bool _hasReport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    private string _balanceTitle = string.Empty;

    [ObservableProperty]
    private string _balanceDetail = string.Empty;

    [ObservableProperty]
    private string _balanceConfidence = string.Empty;

    [ObservableProperty]
    private bool _hasBalance;

    [ObservableProperty]
    private bool _showWindows11;

    [ObservableProperty]
    private string _windows11Summary = string.Empty;

    [ObservableProperty]
    private string _notesText = string.Empty;

    [ObservableProperty]
    private bool _isLaptop;

    public UpgradeAdviceViewModel(ISettingsService settings, IUpgradeAdvisorService advisor, IPcAssistantService assistant,
        INavigationService navigation, INotificationService notifications, ILogger<UpgradeAdviceViewModel> logger)
        : base(settings)
    {
        _advisor = advisor;
        _assistant = assistant;
        _navigation = navigation;
        _notifications = notifications;
        _logger = logger;
        Fixes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoFixes));
        Upgrades.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoUpgrades));
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSessions));
    }

    public ObservableCollection<UpgradeGlanceRow> Glance { get; } = [];

    public ObservableCollection<UpgradeFixRow> Fixes { get; } = [];

    public ObservableCollection<UpgradeSuggestionRow> Upgrades { get; } = [];

    public ObservableCollection<UpgradeSessionRow> Sessions { get; } = [];

    public ObservableCollection<UpgradeCheckRow> Windows11Checks { get; } = [];

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public bool HasNoFixes => HasReport && Fixes.Count == 0;

    public bool HasNoUpgrades => HasReport && Upgrades.Count == 0;

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>Reading changes nothing, so the page fills itself in every mode.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Upgrade advice failed.");

    [RelayCommand]
    private Task CheckAgainAsync() => LoadAsync(CancellationToken.None);

    private Task LoadAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + "…");
        var translator = Loc.Current;
        var report = await Task.Run(() => _advisor.AnalyzeAsync(translator, progress, linked.Token), linked.Token);
        Apply(report, _advisor.LastFacts?.HistoryRecording ?? false);
    }, Loc.T("Checking your PC…"));

    private void Apply(UpgradeReport report, bool historyRecording)
    {
        var tr = Loc.Current;
        Glance.Clear();
        foreach (var item in report.Glance)
        {
            Glance.Add(UpgradeGlanceRow.From(item));
        }

        Fixes.Clear();
        foreach (var fix in report.FixFirst)
        {
            Fixes.Add(new UpgradeFixRow(fix, OpenPage));
        }

        Upgrades.Clear();
        int rank = 1;
        foreach (var upgrade in report.Upgrades)
        {
            Upgrades.Add(new UpgradeSuggestionRow(rank++, upgrade, OpenPage));
        }

        BalanceTitle = UpgradeSummaryText.VerdictTitle(report.Balance, tr);
        BalanceDetail = UpgradeSummaryText.VerdictDetail(report.Balance, historyRecording, tr);
        BalanceConfidence = UpgradeSummaryText.ConfidenceText(report.Balance, tr);
        HasBalance = report.Balance.Verdict != BoundVerdict.NoData;
        Sessions.Clear();
        foreach (var session in report.Balance.Sessions.Take(6))
        {
            Sessions.Add(new UpgradeSessionRow(
                session.Start.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture),
                UpgradeSummaryText.Duration(session.Minutes, tr),
                UpgradeSummaryText.VerdictShort(session.Verdict, tr),
                Loc.F("Graphics {0} %, processor {1} %", Math.Round(session.GpuAverage).ToString("0", CultureInfo.CurrentCulture),
                    Math.Round(session.CpuAverage).ToString("0", CultureInfo.CurrentCulture))));
        }

        var w11 = report.Windows11;
        ShowWindows11 = w11.Status != Win11Status.AlreadyWindows11 && w11.Checks.Count > 0;
        Windows11Summary = w11.Summary;
        Windows11Checks.Clear();
        foreach (var check in w11.Checks)
        {
            var status = check.Passed switch { true => HealthStatus.Good, false => HealthStatus.Critical, _ => HealthStatus.Unknown };
            Windows11Checks.Add(new UpgradeCheckRow(check.Title, check.Detail, status, check.Passed switch
            {
                true => Loc.T("OK"),
                false when check.FixableInBios => Loc.T("Change in BIOS"),
                false => Loc.T("Not met"),
                _ => Loc.T("Unknown"),
            }));
        }

        IsLaptop = report.IsLaptop;
        NotesText = string.Join("\n", report.Notes);
        Warnings = report.Warnings.Count == 0 ? null : string.Join("\n", report.Warnings);
        HasReport = true;
        OnPropertyChanged(nameof(HasNoFixes));
        OnPropertyChanged(nameof(HasNoUpgrades));
        Subtitle = Loc.F("Checked {0}. What to upgrade next, and what to fix first for free.",
            report.Captured.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void AskClaude()
    {
        if (_advisor.Last is not { } report)
        {
            return;
        }

        AssistantPrompt.Set(UpgradeSummaryText.AskPrompt(report, _advisor.LastFacts?.HistoryRecording ?? false, Loc.Current));
        bool configured;
        try
        {
            configured = _assistant.IsConfigured;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Assistant configuration could not be read.");
            configured = false;
        }

        if (!configured)
        {
            _notifications.Warning(Loc.T("Ask Claude is not set up yet"), Loc.T("Add an Anthropic API key on the Ask Claude page; your question is ready to send once it is."));
        }

        _navigation.Navigate(typeof(AssistantPage));
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopySummary()
    {
        if (_advisor.Last is not { } report)
        {
            return;
        }

        try
        {
            var text = UpgradeSummaryText.BuildSummary(report, _advisor.LastFacts?.HistoryRecording ?? false, Loc.Current);
            Clipboard.SetText(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
            _notifications.Success(Loc.T("Copied"), Loc.T("The upgrade summary was copied. Paste it into a forum post or a message to a friend."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copying the upgrade summary failed.");
            _notifications.Warning(Loc.T("Could not copy"), ex.Message);
        }
    }

    [RelayCommand]
    private void OpenHistory() => OpenPage("history");

    private void OpenPage(string key)
    {
        var entry = NavigationCatalog.Pages.FirstOrDefault(p => p.FileName == key)
            ?? (PageFallbacks.TryGetValue(key, out var fallback) ? NavigationCatalog.Pages.FirstOrDefault(p => p.FileName == fallback) : null);
        if (entry is null)
        {
            _logger.LogDebug("Upgrade advice: no page for {Key}.", key);
            return;
        }

        _navigation.Navigate(entry.PageType);
    }
}
