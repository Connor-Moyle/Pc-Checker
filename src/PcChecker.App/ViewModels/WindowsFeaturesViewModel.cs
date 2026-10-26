using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Windows Features page: optional features, capabilities ("features on demand") and removable built-in apps, with
/// search, advanced-mode filtering and confirmed, restore-point-guarded changes.
/// </summary>
public sealed partial class WindowsFeaturesViewModel : ViewModelBase
{
    private readonly IWindowsFeatureService _features;
    private readonly IRestorePointService _restorePoints;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<WindowsFeaturesViewModel> _logger;
    private IReadOnlyList<FeatureRow> _all = [];

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _removeForAllUsers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoaded))]
    private DateTimeOffset? _lastRefreshed;

    [ObservableProperty]
    private bool _rebootRequired;

    [ObservableProperty]
    private string? _rebootMessage;

    [ObservableProperty]
    private bool _isProtectionOff;

    [ObservableProperty]
    private bool _showIntro = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    [ObservableProperty]
    private int _optionalFeatureCount;

    [ObservableProperty]
    private int _enabledFeatureCount;

    [ObservableProperty]
    private int _capabilityCount;

    [ObservableProperty]
    private int _installedCapabilityCount;

    [ObservableProperty]
    private int _inboxAppCount;

    [ObservableProperty]
    private int _removableAppCount;

    [ObservableProperty]
    private int _hiddenAdvancedCount;

    public WindowsFeaturesViewModel(ISettingsService settings, IWindowsFeatureService features, IRestorePointService restorePoints,
        IDialogService dialogs, INotificationService notifications, ILogger<WindowsFeaturesViewModel> logger)
        : base(settings)
    {
        _features = features;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                ApplyFilters();
            }
        };
    }

    public ObservableCollection<FeatureRow> OptionalFeatures { get; } = [];
    public ObservableCollection<FeatureRow> Capabilities { get; } = [];
    public ObservableCollection<FeatureRow> InboxApps { get; } = [];

    public bool HasLoaded => LastRefreshed is not null;
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public string OptionalFeaturesHeader => $"Optional features ({OptionalFeatures.Count})";
    public string CapabilitiesHeader => $"Capabilities ({Capabilities.Count})";
    public string InboxAppsHeader => $"Built-in apps ({InboxApps.Count})";
    public bool HasHiddenAdvanced => HiddenAdvancedCount > 0;
    public string HiddenAdvancedText => HiddenAdvancedCount == 1
        ? "1 advanced item is hidden. Turn on Advanced mode (bottom of the sidebar) to see it."
        : $"{HiddenAdvancedCount} advanced items are hidden. Turn on Advanced mode (bottom of the sidebar) to see them.";
    public bool RestorePointsBeforeChanges => Settings.Current.RestorePoints.CreateBeforeChanges;
    public string RestorePointPolicyText => RestorePointsBeforeChanges ? "On" : "Off";
    public bool IsOptionalFeaturesEmpty => HasLoaded && OptionalFeatures.Count == 0;
    public bool IsCapabilitiesEmpty => HasLoaded && Capabilities.Count == 0;
    public bool IsInboxAppsEmpty => HasLoaded && InboxApps.Count == 0;

    public override Task InitializeAsync(CancellationToken cancellationToken) => RunBusyAsync(LoadAsync, "Reading Windows features…");

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(LoadAsync, "Reading Windows features…");

    [RelayCommand]
    private Task RefreshListAsync() => RunBusyAsync(LoadAsync, "Reading Windows features…");

    [RelayCommand]
    private Task TurnOnAsync(FeatureRow? row)
    {
        if (row is null || !row.CanTurnOn)
        {
            return Task.CompletedTask;
        }

        var verb = row.TurnOnText;
        var message = BuildConfirmation(row, verb);
        return ChangeAsync(row, verb, message, false, ct => _features.EnableAsync(row.Feature, ct));
    }

    [RelayCommand]
    private Task TurnOffAsync(FeatureRow? row)
    {
        if (row is null || !row.CanTurnOff)
        {
            return Task.CompletedTask;
        }

        var verb = row.TurnOffText;
        var message = BuildConfirmation(row, verb);
        return ChangeAsync(row, verb, message, row.Risk == Core.Optimization.RiskLevel.Advanced, ct => _features.DisableAsync(row.Feature, ct));
    }

    [RelayCommand]
    private Task RemoveAppAsync(FeatureRow? row)
    {
        if (row is null || !row.CanRemove)
        {
            return Task.CompletedTask;
        }

        bool allUsers = RemoveForAllUsers && IsAdvancedMode;
        var message = BuildConfirmation(row, "Remove", allUsers);
        return ChangeAsync(row, "Remove", message, true, ct => _features.RemoveInboxAppAsync(row.Feature, allUsers, ct));
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = null;

    [RelayCommand]
    private void DismissReboot()
    {
        RebootRequired = false;
        RebootMessage = null;
    }

    [RelayCommand]
    private async Task RestartNowAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Restart Windows now?",
            "Save your work first. Windows will restart in 30 seconds so the feature change can finish.", "Restart in 30 s", true);
        if (!ok)
        {
            return;
        }

        var result = ShellLauncher.Open("shutdown.exe", "/r /t 30 /c \"PC Checker: restarting to finish a Windows feature change.\"");
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    [RelayCommand]
    private void OpenOptionalFeaturesSettings() => Open("ms-settings:optionalfeatures");

    [RelayCommand]
    private void OpenAppsSettings() => Open("ms-settings:appsfeatures");

    [RelayCommand]
    private void OpenClassicFeaturesDialog() => Open("optionalfeatures.exe");

    [RelayCommand]
    private void OpenMicrosoftStore() => Open("ms-windows-store://home");

    partial void OnSearchTextChanged(string? value) => ApplyFilters();

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Windows Features page error.");

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var progress = new Progress<ProgressUpdate>(u => BusyText = u.Stage);
        var list = await _features.ListAsync(progress, cancellationToken);
        _all = list.Select(f => new FeatureRow(f)).ToList();
        LastRefreshed = DateTimeOffset.Now;
        ApplyFilters();

        try
        {
            bool enabled = await _restorePoints.IsEnabledAsync(cancellationToken);
            IsProtectionOff = RestorePointsBeforeChanges && !enabled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine whether System Protection is on.");
            IsProtectionOff = false;
        }

        OnPropertyChanged(nameof(RestorePointsBeforeChanges));
        OnPropertyChanged(nameof(RestorePointPolicyText));
    }

    private void ApplyFilters()
    {
        var search = SearchText;
        bool advanced = IsAdvancedMode;
        int hidden = 0;

        Fill(OptionalFeatures, FeatureKind.OptionalFeature);
        Fill(Capabilities, FeatureKind.Capability);
        Fill(InboxApps, FeatureKind.InboxApp);

        OptionalFeatureCount = _all.Count(r => r.Kind == FeatureKind.OptionalFeature);
        EnabledFeatureCount = _all.Count(r => r.Kind == FeatureKind.OptionalFeature && r.IsOn);
        CapabilityCount = _all.Count(r => r.Kind == FeatureKind.Capability);
        InstalledCapabilityCount = _all.Count(r => r.Kind == FeatureKind.Capability && r.IsOn);
        InboxAppCount = _all.Count(r => r.Kind == FeatureKind.InboxApp && !r.IsRemoved);
        RemovableAppCount = _all.Count(r => r.CanRemove);
        HiddenAdvancedCount = hidden;
        OnPropertyChanged(nameof(OptionalFeaturesHeader));
        OnPropertyChanged(nameof(CapabilitiesHeader));
        OnPropertyChanged(nameof(InboxAppsHeader));
        OnPropertyChanged(nameof(HasHiddenAdvanced));
        OnPropertyChanged(nameof(HiddenAdvancedText));
        OnPropertyChanged(nameof(IsOptionalFeaturesEmpty));
        OnPropertyChanged(nameof(IsCapabilitiesEmpty));
        OnPropertyChanged(nameof(IsInboxAppsEmpty));

        void Fill(ObservableCollection<FeatureRow> target, FeatureKind kind)
        {
            target.Clear();
            foreach (var row in _all)
            {
                if (row.Kind != kind)
                {
                    continue;
                }

                if (row.AdvancedOnly && !advanced)
                {
                    hidden++;
                    continue;
                }

                if (row.Matches(search))
                {
                    target.Add(row);
                }
            }
        }
    }

    private string BuildConfirmation(FeatureRow row, string verb, bool allUsers = false)
    {
        var lines = new List<string>();
        lines.Add(row.Kind switch
        {
            FeatureKind.InboxApp => $"{row.DisplayName} will be uninstalled{(allUsers ? " for every user account on this PC, and new accounts will not get it either" : " for your user account")}. You can reinstall it from the Microsoft Store at any time.",
            FeatureKind.Capability when verb == "Install" => $"Windows will download and install {row.DisplayName}. This needs an internet connection and can take a few minutes.",
            FeatureKind.Capability => $"{row.DisplayName} will be removed from Windows. You can install it again from this page later.",
            _ when verb == "Enable" => $"Windows will turn on {row.DisplayName}. It may download files and may ask for a restart.",
            _ => $"Windows will turn off {row.DisplayName}. It may ask for a restart. You can turn it back on from this page later.",
        });

        if (!string.IsNullOrWhiteSpace(row.Recommendation) && row.Kind != FeatureKind.InboxApp)
        {
            lines.Add("Advice: " + row.Recommendation);
        }
        else if (row.Kind == FeatureKind.InboxApp && !string.IsNullOrWhiteSpace(row.Description))
        {
            lines.Add("Advice: " + row.Description);
        }

        if (row.Risk == Core.Optimization.RiskLevel.Advanced)
        {
            lines.Add("This item is marked \"not recommended\". Only continue if you are sure.");
        }

        lines.Add(RestorePointsBeforeChanges
            ? "A restore point will be created first so you can undo the change."
            : "No restore point will be created (turned off in Settings → Restore points).");
        return string.Join("\n\n", lines);
    }

    private async Task ChangeAsync(FeatureRow row, string verb, string message, bool danger, Func<CancellationToken, Task<OperationResult>> operation)
    {
        bool confirmed = await _dialogs.ConfirmAsync($"{verb} {row.DisplayName}?", message, verb, danger);
        if (!confirmed)
        {
            return;
        }

        StatusMessage = null;
        await RunBusyAsync(async ct =>
        {
            var result = await operation(ct);
            if (result.Success)
            {
                StatusMessage = result.Message;
                _notifications.Success($"{row.DisplayName}", result.Message);
                if (result.RebootRequired)
                {
                    RebootRequired = true;
                    RebootMessage = $"{row.DisplayName}: Windows needs a restart to finish this change.";
                }
            }
            else
            {
                ErrorMessage = result.Message;
                _logger.LogWarning("{Verb} {Feature} failed: {Message}", verb, row.Name, result.Message);
            }

            await LoadAsync(ct);
        }, $"{verb} {row.DisplayName}…");
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
