using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Privacy;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Privacy page: which apps recently used the camera, microphone and location (from Windows' own records), the on/off
/// switch for each, turning one Store app's access off (asks first, journaled, Undo puts it back), and a quick look at
/// activity history, the advertising ID and diagnostic data with a link to the Debloat tweak that changes each.
/// </summary>
public sealed partial class PrivacyViewModel : ViewModelBase, IRefreshablePage
{
    private readonly IPrivacyService _privacy;
    private readonly IDebloatService _debloat;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly IServiceProvider _services;
    private readonly ILogger<PrivacyViewModel> _logger;

    public PrivacyViewModel(IPrivacyService privacy, IDebloatService debloat, ISettingsService settings, IDialogService dialogs,
        INotificationService notifications, INavigationService navigation, IServiceProvider services, ILogger<PrivacyViewModel> logger)
        : base(settings)
    {
        _privacy = privacy;
        _debloat = debloat;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _services = services;
        _logger = logger;
    }

    public ObservableCollection<CapabilityGroup> Capabilities { get; } = [];

    public ObservableCollection<PrivacyQuickRow> QuickSettings { get; } = [];

    [ObservableProperty] private string _statusText = Loc.T("Reading which apps used the camera, microphone and location…");
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private string _inUseText = "—";
    [ObservableProperty] private string _inUseSubtitle = string.Empty;
    [ObservableProperty] private string _recentText = "—";
    [ObservableProperty] private string? _warnings;
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private string? _userNote;
    [ObservableProperty] private bool _hasUserNote;

    ICommand IRefreshablePage.RefreshKeyCommand => RefreshCommand;

    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync();

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync();

    /// <summary>The header's Refresh button and F5.</summary>
    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private Task LoadAsync() =>
        RunBusyAsync(async ct =>
        {
            var snapshot = await _privacy.ReadAsync(ct);
            IReadOnlyDictionary<string, TweakState> states;
            try
            {
                states = await _debloat.GetStatesAsync(PrivacyQuickStatusRules.TweakIds, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read the privacy tweak states.");
                states = new Dictionary<string, TweakState>();
            }

            Apply(snapshot, states);
        }, Loc.T("Reading which apps used the camera, microphone and location…"));

    private void Apply(PrivacySnapshot snapshot, IReadOnlyDictionary<string, TweakState> states)
    {
        Capabilities.Clear();
        foreach (var status in snapshot.Capabilities)
        {
            var group = new CapabilityGroup(status, new RelayCommand(() => OpenSettings(status.Capability)));
            foreach (var app in status.Apps)
            {
                group.Apps.Add(new CapabilityAppRow(app, RevokeAsync, group.OpenSettingsCommand, CopyApp));
            }

            Capabilities.Add(group);
        }

        QuickSettings.Clear();
        foreach (var id in PrivacyQuickStatusRules.TweakIds)
        {
            var state = states.TryGetValue(id, out var s) ? s.Status : TweakStatus.Unknown;
            string tweakId = id;
            QuickSettings.Add(new PrivacyQuickRow(PrivacyQuickStatusRules.Map(id, state), new RelayCommand(() => OpenDebloat(tweakId))));
        }

        var inUse = Capabilities.Where(c => c.InUseNow).Select(c => c.Title).ToList();
        InUseText = inUse.Count == 0 ? Loc.T("Nothing") : string.Join(", ", inUse);
        InUseSubtitle = inUse.Count == 0
            ? Loc.T("No app is using the camera, microphone or location")
            : string.Join(", ", Capabilities.SelectMany(c => c.Apps).Where(a => a.InUseNow).Select(a => a.Name).Distinct());
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        int recent = snapshot.Capabilities.SelectMany(c => c.Apps).Where(a => a.InUseNow || a.LastStart >= weekAgo).Select(a => a.KeyName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        RecentText = recent.ToString(CultureInfo.CurrentCulture);

        Warnings = snapshot.Warnings.Count == 0 ? null : string.Join(" ", snapshot.Warnings);
        HasWarnings = Warnings is not null;
        UserNote = snapshot.UserNote;
        HasUserNote = UserNote is not null;
        HasLoaded = true;
        StatusText = Loc.F("Checked at {0}", DateTimeOffset.Now.ToString("t", CultureInfo.CurrentCulture));
    }

    private async Task RevokeAsync(CapabilityAppRow row)
    {
        var app = row.Usage;
        if (!app.CanRevoke || IsBusy)
        {
            return;
        }

        string capability = ConsentStoreParser.Name(app.Capability).ToLower(CultureInfo.CurrentCulture);
        if (!await _dialogs.ConfirmAsync(Loc.T("Turn off access?"),
            Loc.F("Stop {0} from using the {1}? The app asks again or shows an error the next time it needs it. You can turn it back on with Undo or in Windows Settings.", app.DisplayName, capability),
            Loc.T("Turn off")))
        {
            return;
        }

        var previous = app.Value;
        var denied = app with { Value = ConsentValue.Deny };
        var result = await UndoOffers.RunAsync(
            () => _privacy.SetAppAccessAsync(app, ConsentValue.Deny, CancellationToken.None),
            async ct =>
            {
                var undo = await _privacy.SetAppAccessAsync(denied, previous, ct);
                DispatcherHelper.PostToUi(() => _ = LoadAsync());
                return undo;
            },
            Loc.F("Turn off {0} access for {1}", capability, app.DisplayName));

        if (result.Success)
        {
            _notifications.Success(Loc.T("Access turned off"), result.Message);
        }
        else
        {
            _notifications.Error(Loc.T("Could not turn off access"), result.Message);
        }

        await LoadAsync();
    }

    private void OpenSettings(CapabilityKind capability)
    {
        var result = _privacy.OpenSettings(capability);
        if (!result.Success)
        {
            _notifications.Error(Loc.T("Could not open Settings"), result.Message);
        }
    }

    private void CopyApp(CapabilityAppRow row)
    {
        try
        {
            Clipboard.SetText(row.Usage.Path ?? row.Usage.KeyName);
            _notifications.Success(Loc.T("Copied"), row.Name);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
        }
    }

    /// <summary>Opens the Debloat page filtered to the tweak, so the change is made with its own checks and Revert.</summary>
    private void OpenDebloat(string tweakId)
    {
        _navigation.Navigate(typeof(DebloatPage));
        if (_services.GetService<DebloatViewModel>() is { } debloat)
        {
            debloat.SearchText = tweakId;
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Privacy page operation failed.");
}
