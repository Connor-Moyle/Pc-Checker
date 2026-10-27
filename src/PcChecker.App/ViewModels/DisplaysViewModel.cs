using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Dialogs;
using PcChecker.App.Views.Windows;
using PcChecker.Core.Common;
using PcChecker.Core.Displays;
using PcChecker.Core.Settings;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>A saved display profile as listed on the page.</summary>
public sealed partial class DisplayProfileItem : ObservableObject
{
    public DisplayProfileItem(DisplayProfile profile)
    {
        Profile = profile;
    }

    public DisplayProfile Profile { get; }

    public string Name => Profile.Name;

    public string Description => DisplayProfiles.Describe(Profile);

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string? _missingText;
}

/// <summary>
/// Displays page: arrangement preview, one card per monitor (mode, orientation, HDR, brightness/contrast, main display),
/// saved layouts (profiles) and shortcuts into the Windows and graphics-vendor tools.
/// </summary>
public sealed partial class DisplaysViewModel : ViewModelBase
{
    /// <summary>Seconds the "Keep these settings?" dialog waits before reverting a mode change.</summary>
    public const int KeepSettingsSeconds = 15;

    public const double ArrangementWidth = 560;
    public const double ArrangementHeight = 230;

    private readonly IDisplayService _displays;
    private readonly IDialogService _dialogs;
    private readonly IContentDialogService _contentDialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<DisplaysViewModel> _logger;
    private IReadOnlyList<DisplayInfo> _current = [];

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warningText;

    [ObservableProperty]
    private bool _hasDisplays;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private DisplayProfileItem? _selectedProfile;

    [ObservableProperty]
    private string? _lastActionMessage;

    public DisplaysViewModel(ISettingsService settings, IDisplayService displays, IDialogService dialogs, IContentDialogService contentDialogs,
        INotificationService notifications, ILogger<DisplaysViewModel> logger)
        : base(settings)
    {
        _displays = displays;
        _dialogs = dialogs;
        _contentDialogs = contentDialogs;
        _notifications = notifications;
        _logger = logger;
        PropertyChanged += OnOwnPropertyChanged;
        LoadProfiles();
        ReloadWhenReplaced(s => s.Displays, LoadProfiles);
    }

    public ObservableCollection<DisplayCardViewModel> Displays { get; } = [];

    public ObservableCollection<ArrangementRect> Arrangement { get; } = [];

    public ObservableCollection<DisplayProfileItem> Profiles { get; } = [];

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public bool HasProfiles => Profiles.Count > 0;

    public double ArrangementCanvasWidth => ArrangementWidth;

    public double ArrangementCanvasHeight => ArrangementHeight;

    public override Task InitializeAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(ReloadAsync, "Reading your displays…");

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Displays page operation failed.");

    [RelayCommand]
    private Task RefreshNowAsync() => RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private Task IdentifyAllAsync() => IdentifyAsync(null);

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        string name = NewProfileName.Trim();
        var existing = Settings.Current.Displays.Profiles;
        var error = DisplayProfiles.ValidateName(name, existing);
        if (error is not null && existing.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            bool overwrite = await _dialogs.ConfirmAsync("Replace profile?", $"A profile called \"{name}\" already exists. Replace it with the current layout?", "Replace");
            if (!overwrite)
            {
                return;
            }

            existing.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            error = null;
        }

        if (error is not null)
        {
            ErrorMessage = error;
            return;
        }

        if (_current.Count == 0)
        {
            ErrorMessage = "There is no display layout to save yet. Refresh first.";
            return;
        }

        existing.Add(DisplayProfiles.Capture(name, _current));
        await SaveSettingsAsync();
        NewProfileName = string.Empty;
        LoadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _notifications.Success("Profile saved", $"\"{name}\" remembers {_current.Count} display(s).");
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedProfile))]
    private async Task ApplyProfileAsync()
    {
        var item = SelectedProfile;
        if (item is null)
        {
            return;
        }

        var (_, missing) = DisplayProfiles.Match(item.Profile, _current);
        string missingNote = missing.Count == 0
            ? string.Empty
            : $"\n\n{missing.Count} display(s) in this profile are not connected right now and will be skipped.";
        bool ok = await _dialogs.ConfirmAsync("Apply display profile?",
            $"\"{item.Name}\" will set:\n{DisplayProfiles.Describe(item.Profile)}.{missingNote}\n\nIf a screen goes blank, wait: you can press Esc or just refresh after {KeepSettingsSeconds} seconds and the previous layout is restored.", "Apply");
        if (!ok)
        {
            return;
        }

        var before = _current;
        OperationResult? result = null;
        await RunBusyAsync(async ct =>
        {
            var trimmed = new DisplayProfile { Name = item.Profile.Name, PrimaryDevice = item.Profile.PrimaryDevice };
            foreach (var (device, mode) in item.Profile.Modes.Where(kv => !missing.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)))
            {
                trimmed.Modes[device] = mode;
            }

            if (trimmed.PrimaryDevice is not null && missing.Contains(trimmed.PrimaryDevice, StringComparer.OrdinalIgnoreCase))
            {
                trimmed.PrimaryDevice = null;
            }

            result = await _displays.ApplyProfileAsync(trimmed, ct);
            await ReloadAsync(ct);
        }, $"Applying \"{item.Name}\"…");

        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            _notifications.Error("Profile not applied", result.Message);
            return;
        }

        bool keep = await ShowKeepDialogAsync("Keep this layout?", $"Profile \"{item.Name}\" is active.");
        if (!keep)
        {
            await RevertLayoutAsync(before);
        }
        else
        {
            _notifications.Success("Profile applied", result.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedProfile))]
    private async Task DeleteProfileAsync()
    {
        var item = SelectedProfile;
        if (item is null)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync("Delete profile?", $"Remove the saved layout \"{item.Name}\"? Your displays keep their current settings.", "Delete", isDanger: true);
        if (!ok)
        {
            return;
        }

        Settings.Current.Displays.Profiles.RemoveAll(p => string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        await SaveSettingsAsync();
        LoadProfiles();
        _notifications.Info("Profile deleted", $"\"{item.Name}\" was removed.");
    }

    private bool CanUseSelectedProfile() => SelectedProfile is not null && !IsBusy;

    [RelayCommand]
    private void OpenWindowsDisplaySettings() => Report(_displays.OpenWindowsDisplaySettings());

    [RelayCommand]
    private void OpenNightLight() => Report(_displays.OpenNightLightSettings());

    [RelayCommand]
    private void OpenGpuControlPanel() => Report(_displays.OpenGpuControlPanel());

    [RelayCommand]
    private void OpenColorCalibration() => Report(_displays.OpenColorCalibration());

    [RelayCommand]
    private void OpenAdvancedGraphics() => Report(ShellLauncher.Open("ms-settings:display-advancedgraphics"));

    // ----- called by the cards ---------------------------------------------------------------------------------

    internal async Task ApplyModeAsync(DisplayCardViewModel card)
    {
        var target = card.PendingMode;
        var previous = card.Info.Current;
        OperationResult? result = null;
        await RunBusyAsync(async ct =>
        {
            result = await _displays.SetModeAsync(card.DeviceName, target, ct);
            if (result.Success)
            {
                await ReloadAsync(ct);
            }
        }, $"Switching {card.Name} to {target}…");

        if (result is null)
        {
            return;
        }

        if (!result.Success)
        {
            card.ResetPickers();
            _notifications.Error("Display mode not changed", result.Message);
            return;
        }

        bool keep = await ShowKeepDialogAsync($"Keep these settings for {card.Name}?", DisplayText.ModeText(target));
        if (keep)
        {
            _notifications.Success("Display updated", result.Message);
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var revert = await _displays.SetModeAsync(card.DeviceName, previous, ct);
            await ReloadAsync(ct);
            if (revert.Success)
            {
                _notifications.Info("Reverted", $"{card.Name} is back to {previous}.");
            }
            else
            {
                _notifications.Error("Could not revert", revert.Message);
            }
        }, "Restoring the previous settings…");
    }

    internal async Task SetPrimaryAsync(DisplayCardViewModel card)
    {
        bool ok = await _dialogs.ConfirmAsync("Make this the main display?",
            $"{card.Name} will become the main display: the taskbar, the Start menu and new windows appear there. The other displays are repositioned around it.", "Make main");
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _displays.SetPrimaryAsync(card.DeviceName, ct);
            await ReloadAsync(ct);
            Notify("Main display", result);
        }, "Changing the main display…");
    }

    internal async Task ChangeOrientationAsync(DisplayCardViewModel card, int degrees)
    {
        bool ok = await _dialogs.ConfirmAsync("Rotate this display?",
            $"{card.Name} will switch to {DisplayText.OrientationText(degrees)}. If it looks wrong you get {KeepSettingsSeconds} seconds to revert.", "Rotate");
        if (!ok)
        {
            card.ResetPickers();
            return;
        }

        int previous = card.Info.OrientationDegrees;
        OperationResult? result = null;
        await RunBusyAsync(async ct =>
        {
            result = await _displays.SetOrientationAsync(card.DeviceName, degrees, ct);
            if (result.Success)
            {
                await ReloadAsync(ct);
            }
        }, "Rotating…");

        if (result is null || !result.Success)
        {
            card.ResetPickers();
            if (result is not null)
            {
                _notifications.Error("Display not rotated", result.Message);
            }

            return;
        }

        bool keep = await ShowKeepDialogAsync("Keep this orientation?", $"{card.Name}: {DisplayText.OrientationText(degrees)}");
        if (!keep)
        {
            await RunBusyAsync(async ct =>
            {
                var revert = await _displays.SetOrientationAsync(card.DeviceName, previous, ct);
                await ReloadAsync(ct);
                Notify("Reverted", revert);
            }, "Restoring the previous orientation…");
        }
    }

    internal async Task ChangeHdrAsync(DisplayCardViewModel card, bool enabled)
    {
        string message = enabled
            ? $"Turn on HDR for {card.Name}? Games and videos with HDR content look richer; some non-HDR apps may look washed out until you adjust the SDR brightness slider in Windows."
            : $"Turn off HDR for {card.Name}? Content goes back to standard colour range.";
        bool ok = await _dialogs.ConfirmAsync(enabled ? "Turn HDR on?" : "Turn HDR off?", message, enabled ? "Turn on" : "Turn off");
        if (!ok)
        {
            card.ResetPickers();
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _displays.SetHdrAsync(card.DeviceName, enabled, ct);
            await ReloadAsync(ct);
            if (!result.Success)
            {
                card.ResetPickers();
            }

            Notify("HDR", result);
        }, enabled ? "Turning HDR on…" : "Turning HDR off…");
    }

    internal async Task ChangeBrightnessAsync(DisplayCardViewModel card, int percent, CancellationToken cancellationToken)
    {
        var result = await _displays.SetBrightnessAsync(card.DeviceName, percent, cancellationToken);
        if (!result.Success)
        {
            _notifications.Warning("Brightness", result.Message);
        }
        else
        {
            DispatcherHelper.PostToUi(() => LastActionMessage = result.Message);
        }
    }

    internal async Task ChangeContrastAsync(DisplayCardViewModel card, int percent, CancellationToken cancellationToken)
    {
        var result = await _displays.SetContrastAsync(card.DeviceName, percent, cancellationToken);
        if (!result.Success)
        {
            _notifications.Warning("Contrast", result.Message);
        }
        else
        {
            DispatcherHelper.PostToUi(() => LastActionMessage = result.Message);
        }
    }

    internal Task IdentifyAsync(DisplayCardViewModel? only)
    {
        var targets = only is null ? _current : _current.Where(d => d.DeviceName == only.DeviceName).ToList();
        if (targets.Count == 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            IdentifyWindow.ShowFor(targets.Select(d => (d, ArrangementLayout.NumberOf(_current, d.DeviceName))), TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identify windows could not be shown.");
            _notifications.Warning("Identify", "The identify overlay could not be shown: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    // ----- internals -----------------------------------------------------------------------------------------

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var list = await _displays.GetDisplaysAsync(cancellationToken);
        _current = list;
        var warnings = (_displays as WindowsDisplayService)?.LastWarnings ?? [];

        await DispatcherHelper.RunOnUiAsync(() =>
        {
            // Reuse cards for devices that are still attached so the sliders do not jump while being dragged.
            var existing = Displays.ToDictionary(c => c.DeviceName, StringComparer.OrdinalIgnoreCase);
            Displays.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];
                if (existing.TryGetValue(info.DeviceName, out var card) && card.Number == i + 1)
                {
                    card.Load(info);
                    Displays.Add(card);
                }
                else
                {
                    Displays.Add(new DisplayCardViewModel(this, info, i + 1));
                }
            }

            Arrangement.Clear();
            foreach (var rect in ArrangementLayout.Compute(ArrangementLayout.FromDisplays(list), ArrangementWidth, ArrangementHeight))
            {
                Arrangement.Add(rect);
            }

            HasDisplays = list.Count > 0;
            Summary = BuildSummary(list);
            WarningText = warnings.Count == 0 ? null : string.Join(" ", warnings);
            UpdateProfileStates();
        });
    }

    private static string BuildSummary(IReadOnlyList<DisplayInfo> list)
    {
        if (list.Count == 0)
        {
            return "No displays were reported.";
        }

        var primary = list.FirstOrDefault(d => d.IsPrimary) ?? list[0];
        string count = list.Count == 1 ? "1 display" : $"{list.Count} displays";
        int hdr = list.Count(d => d.HdrEnabled);
        string hdrText = hdr == 0 ? string.Empty : $" · HDR on for {hdr}";
        return $"{count} · main: {primary.FriendlyName} at {primary.Current}{hdrText}";
    }

    private void LoadProfiles()
    {
        var selected = SelectedProfile?.Name;
        Profiles.Clear();
        foreach (var p in Settings.Current.Displays.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Profiles.Add(new DisplayProfileItem(p));
        }

        UpdateProfileStates();
        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Name, selected, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasProfiles));
    }

    private void UpdateProfileStates()
    {
        foreach (var item in Profiles)
        {
            item.IsActive = DisplayProfiles.IsActive(item.Profile, _current);
            var (_, missing) = DisplayProfiles.Match(item.Profile, _current);
            item.MissingText = missing.Count == 0 ? null : $"{missing.Count} display(s) not connected";
        }
    }

    private async Task RevertLayoutAsync(IReadOnlyList<DisplayInfo> before)
    {
        await RunBusyAsync(async ct =>
        {
            var revert = DisplayProfiles.Capture("previous layout", before);
            var result = await _displays.ApplyProfileAsync(revert, ct);
            await ReloadAsync(ct);
            Notify("Reverted", result);
        }, "Restoring the previous layout…");
    }

    private async Task<bool> ShowKeepDialogAsync(string title, string detail)
    {
        try
        {
            var dialog = new KeepSettingsDialog(title, detail, KeepSettingsSeconds);
            var result = await _contentDialogs.ShowAsync(dialog, CancellationToken.None);
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // Without a dialog host (very early start-up) fall back to a plain confirmation; keeping is the safe default.
            _logger.LogDebug(ex, "Keep-settings dialog unavailable; falling back to a plain confirmation.");
            return await _dialogs.ConfirmAsync(title, detail + "\n\nKeep these settings?", "Keep");
        }
    }

    private void Notify(string title, OperationResult result)
    {
        LastActionMessage = result.Message;
        if (result.Success)
        {
            _notifications.Success(title, result.Message);
        }
        else
        {
            _notifications.Error(title, result.Message);
        }
    }

    private void Report(OperationResult result)
    {
        if (!result.Success)
        {
            _notifications.Warning("Could not open", result.Message);
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save display profiles.");
            ErrorMessage = "Profiles could not be saved: " + ex.Message;
        }
    }

    private void OnOwnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy))
        {
            ApplyProfileCommand.NotifyCanExecuteChanged();
            DeleteProfileCommand.NotifyCanExecuteChanged();
        }
    }
}
