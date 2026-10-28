using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Security;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Security page: a traffic light for each protection (antivirus, firewall, Windows version, UAC, SmartScreen,
/// encryption…), an overall score with the three things to fix first, Defender scans and recent threats. Reading never
/// changes anything; the few fixes ask first and run through the change guard.
/// </summary>
public sealed partial class SecurityViewModel : ViewModelBase, IRefreshablePage
{
    private readonly ISecurityStatusService _security;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<SecurityViewModel> _logger;

    public SecurityViewModel(ISecurityStatusService security, ISettingsService settings, IDialogService dialogs,
        INotificationService notifications, INavigationService navigation, ILogger<SecurityViewModel> logger)
        : base(settings)
    {
        _security = security;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<SecurityCheckRow> Checks { get; } = [];

    public ObservableCollection<SecurityCheckRow> TopFixes { get; } = [];

    public ObservableCollection<ThreatRow> Threats { get; } = [];

    [ObservableProperty] private string _statusText = Loc.T("Checking how well this PC is protected…");
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private string _scoreText = "—";
    [ObservableProperty] private string _gradeText = string.Empty;
    [ObservableProperty] private HealthStatus _scoreStatus = HealthStatus.Unknown;
    [ObservableProperty] private string _redCount = "0";
    [ObservableProperty] private string _yellowCount = "0";
    [ObservableProperty] private string _greenCount = "0";
    [ObservableProperty] private bool _hasTopFixes;
    [ObservableProperty] private string? _warnings;
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private bool _rebootRequired;
    [ObservableProperty] private bool _hasThreats;
    [ObservableProperty] private string _threatsText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuickScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(FullScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateDefinitionsCommand))]
    private bool _defenderIsActive;

    [ObservableProperty] private string _defenderNote = string.Empty;

    ICommand IRefreshablePage.RefreshKeyCommand => RefreshCommand;

    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync();

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync();

    /// <summary>The header's Check again button and F5.</summary>
    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private Task LoadAsync() =>
        RunBusyAsync(async ct =>
        {
            var facts = await _security.ReadAsync(ct);
            var threats = await _security.GetRecentThreatsAsync(ct);
            Apply(facts, threats);
        }, Loc.T("Checking how well this PC is protected…"));

    private void Apply(SecurityFacts facts, IReadOnlyList<ThreatDetection> threats)
    {
        var now = DateTimeOffset.Now;
        var checks = SecurityCheckBuilder.Build(facts, now);
        var score = SecurityScoring.Score(checks);

        Checks.Clear();
        foreach (var check in checks)
        {
            Checks.Add(NewRow(check));
        }

        TopFixes.Clear();
        foreach (var check in score.TopFixes)
        {
            TopFixes.Add(NewRow(check));
        }

        HasTopFixes = TopFixes.Count > 0;
        ScoreText = score.Value.ToString(CultureInfo.CurrentCulture);
        GradeText = Loc.T(score.Grade);
        ScoreStatus = score.Status;
        RedCount = Count(checks, HealthStatus.Critical);
        YellowCount = Count(checks, HealthStatus.Warning);
        GreenCount = Count(checks, HealthStatus.Good);

        Threats.Clear();
        foreach (var threat in threats)
        {
            Threats.Add(new ThreatRow(threat));
        }

        HasThreats = Threats.Count > 0;
        int open = Threats.Count(t => t.NeedsAttention);
        ThreatsText = Threats.Count == 0
            ? Loc.T("Microsoft Defender has not found any threats recently.")
            : open > 0
                ? Loc.F("{0} found recently; {1} still need you.", Threats.Count, open)
                : Loc.F("{0} found recently, all dealt with.", Threats.Count);

        var thirdParty = SecurityCenterState.ActiveThirdParty(facts.Antivirus);
        DefenderIsActive = thirdParty is null && DefenderText.IsActive(facts.Defender);
        DefenderNote = thirdParty is not null
            ? Loc.F("{0} protects this PC, so Microsoft Defender's scans are turned off here. Use {0} to scan.", thirdParty.Name)
            : DefenderIsActive
                ? Loc.T("Scans run in the background; you can keep using the PC.")
                : Loc.T("Microsoft Defender is not running, so it cannot scan. Open Windows Security to turn it on.");

        Warnings = facts.Warnings.Count == 0 ? null : string.Join(" ", facts.Warnings);
        HasWarnings = Warnings is not null;
        HasLoaded = true;
        StatusText = Loc.F("Checked at {0}", now.ToString("t", CultureInfo.CurrentCulture));
    }

    private static string Count(IEnumerable<SecurityCheck> checks, HealthStatus status) =>
        checks.Count(c => c.Status == status).ToString(CultureInfo.CurrentCulture);

    private SecurityCheckRow NewRow(SecurityCheck check) => new(check, OpenSettingAsync, FixAsync, CopyRow);

    private Task OpenSettingAsync(SecurityCheckRow row)
    {
        if (row.Check.SettingsUri is not { } link)
        {
            return Task.CompletedTask;
        }

        if (SecurityLinks.AppPage(link) is { } key)
        {
            if (NavigationCatalog.Pages.FirstOrDefault(p => p.FileName == key) is { } page)
            {
                _navigation.Navigate(page.PageType);
            }

            return Task.CompletedTask;
        }

        Report(_security.OpenSetting(link), Loc.T("Could not open the setting"));
        return Task.CompletedTask;
    }

    private Task FixAsync(SecurityCheckRow row) => row.Check.Fix switch
    {
        SecurityFix.UpdateDefinitions => UpdateDefinitions(),
        SecurityFix.TurnOnFirewall => TurnOnFirewallAsync(row.Check.FixTarget),
        SecurityFix.TurnOffSmb1 => TurnOffSmb1Async(),
        _ => Task.CompletedTask,
    };

    private void CopyRow(SecurityCheckRow row)
    {
        try
        {
            Clipboard.SetText(row.ToText());
            _notifications.Success(Loc.T("Copied"), row.Title);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
        }
    }

    private bool CanUseDefender() => DefenderIsActive;

    [RelayCommand(CanExecute = nameof(CanUseDefender))]
    private async Task UpdateDefinitions()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Update Microsoft Defender?"),
            Loc.T("Download the newest virus definitions from Microsoft now? This only adds protection and takes a minute or two."), Loc.T("Update")))
        {
            return;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct => result = await _security.UpdateDefinitionsAsync(null, ct), Loc.T("Updating Microsoft Defender…"));
        await FinishAsync(result, Loc.T("Definitions updated"), Loc.T("Could not update"));
    }

    [RelayCommand(CanExecute = nameof(CanUseDefender))]
    private Task QuickScan() => ScanAsync(DefenderScanType.Quick);

    [RelayCommand(CanExecute = nameof(CanUseDefender))]
    private async Task FullScan()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Run a full scan?"),
            Loc.T("A full scan checks every file and can take an hour or more. The PC may feel slower meanwhile. You can cancel it at any time."),
            Loc.T("Start full scan")))
        {
            return;
        }

        await ScanAsync(DefenderScanType.Full);
    }

    private async Task ScanAsync(DefenderScanType type)
    {
        OperationResult? result = null;
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage);
        string busy = type == DefenderScanType.Full ? Loc.T("Full scan running…") : Loc.T("Quick scan running…");
        await RunBusyAsync(async ct => result = await _security.RunScanAsync(type, progress, ct), busy);
        await FinishAsync(result, Loc.T("Scan finished"), Loc.T("Scan finished with a warning"));
    }

    private async Task TurnOnFirewallAsync(string? target)
    {
        var profiles = (target ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (profiles.Length == 0)
        {
            return;
        }

        string names = string.Join(", ", profiles.Select(SecurityCheckBuilder.ProfileName));
        if (!await _dialogs.ConfirmAsync(Loc.T("Turn the firewall back on?"),
            Loc.F("Turn Windows Firewall on for {0}? A restore point is made first (per your settings) and the change is written to the Action Journal. Programs you allowed through the firewall keep working.", names),
            Loc.T("Turn on")))
        {
            return;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct => result = await _security.TurnOnFirewallAsync(profiles, ct), Loc.T("Turning on Windows Firewall…"), cancellable: false);
        await FinishAsync(result, Loc.T("Firewall on"), Loc.T("Could not turn on the firewall"));
    }

    private async Task TurnOffSmb1Async()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Turn off SMB 1.0?"),
            Loc.T("SMB 1.0 is an old, unsafe way of sharing files. Turning it off can stop very old network drives or printers from working; you can turn it back on from Windows Features. A restore point is made first and Windows needs a restart to finish."),
            Loc.T("Turn off")))
        {
            return;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct => result = await _security.TurnOffSmb1Async(ct), Loc.T("Turning off SMB 1.0…"), cancellable: false);
        await FinishAsync(result, Loc.T("SMB 1.0 turned off"), Loc.T("Could not turn off SMB 1.0"));
    }

    [RelayCommand]
    private void OpenWindowsSecurity() => Report(_security.OpenSetting(SecurityLinks.VirusProtection), Loc.T("Could not open Windows Security"));

    [RelayCommand]
    private void OpenProtectionHistory() => Report(_security.OpenSetting(SecurityLinks.ProtectionHistory), Loc.T("Could not open Protection history"));

    [RelayCommand]
    private void CopyReport()
    {
        if (Checks.Count == 0)
        {
            return;
        }

        var text = Loc.F("Security score: {0} ({1})", ScoreText, GradeText) + Environment.NewLine
            + string.Join(Environment.NewLine, Checks.Select(c => c.ToText()));
        try
        {
            Clipboard.SetText(text);
            _notifications.Success(Loc.T("Copied"), Loc.T("The security summary is on the clipboard."));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
        }
    }

    [RelayCommand]
    private void DismissReboot() => RebootRequired = false;

    private async Task FinishAsync(OperationResult? result, string okTitle, string failTitle)
    {
        if (result is null)
        {
            return; // Cancelled or failed with ErrorMessage shown.
        }

        if (result.Success)
        {
            _notifications.Success(okTitle, result.Message);
        }
        else
        {
            _notifications.Warning(failTitle, result.Message);
        }

        RebootRequired |= result.RebootRequired;
        await LoadAsync();
    }

    private void Report(OperationResult result, string failTitle)
    {
        if (!result.Success)
        {
            _notifications.Error(failTitle, result.Message);
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Security page operation failed.");
}
