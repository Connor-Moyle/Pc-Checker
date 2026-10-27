using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Gaming;
using PcChecker.Core.Power;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Overlay page, part 2: the FPS counter fields and the "Game profiles" section (list, add from running programs
/// or an .exe, edit, remove, enable/disable, auto-switch options and the active profile status line).
/// </summary>
public sealed partial class OverlayViewModel
{
    private const string KeepLayoutText = "Keep the current layout";
    private const string KeepFansText = "Don't change the fans";
    private const string KeepPowerText = "Don't change the power plan";

    private GameProfileSwitcher? _switcher;
    private IGameProfileService? _gameProfiles;
    private IForegroundAppTracker? _tracker;
    private IFrameRateMonitor? _frames;
    private PcChecker.Core.Fans.IFanControlService? _fans;
    private DispatcherTimer? _frameStatusTimer;
    private string? _editingOriginalName;
    private bool _loadingProfiles;

    // ----- FPS counter status -----

    [ObservableProperty]
    private string _frameRateStatus = string.Empty;

    [ObservableProperty]
    private string _trackedGameText = "Waiting for a game";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFrameCounterUnavailable))]
    private string? _frameCounterProblem;

    // ----- Game profiles -----

    [ObservableProperty]
    private bool _autoSwitch = true;

    [ObservableProperty]
    private bool _onlyWhenFullscreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveProfile))]
    private string? _activeProfileText;

    [ObservableProperty]
    private string? _lastSwitchMessage;

    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    private string _editorTitle = "New game profile";

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editExeName = string.Empty;

    [ObservableProperty]
    private string? _editExePath;

    [ObservableProperty]
    private bool _editEnabled = true;

    [ObservableProperty]
    private ProfileChoice? _editOverlayPreset;

    [ObservableProperty]
    private bool _editShowOverlay = true;

    [ObservableProperty]
    private ProfileChoice? _editFanProfile;

    [ObservableProperty]
    private PowerPlanChoice? _editPowerPlan;

    [ObservableProperty]
    private bool _editRevertOnExit = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditorError))]
    private string? _editorError;

    [ObservableProperty]
    private bool _isPickerOpen;

    [ObservableProperty]
    private bool _isLoadingApps;

    public ObservableCollection<GameProfileRow> GameProfiles { get; } = [];

    public ObservableCollection<RunningAppRow> RunningApps { get; } = [];

    public ObservableCollection<ProfileChoice> OverlayPresetChoices { get; } = [];

    public ObservableCollection<ProfileChoice> FanProfileChoices { get; } = [];

    public ObservableCollection<PowerPlanChoice> PowerPlanChoices { get; } = [];

    public bool HasGameProfiles => GameProfiles.Count > 0;

    public bool HasActiveProfile => !string.IsNullOrWhiteSpace(ActiveProfileText);

    public bool HasEditorError => !string.IsNullOrWhiteSpace(EditorError);

    public bool IsFrameCounterUnavailable => !string.IsNullOrWhiteSpace(FrameCounterProblem);

    /// <summary>Shown under the fan drop-down when this PC has no controllable fans.</summary>
    public string? FanControlNote { get; private set; }

    public bool HasFanControlNote => !string.IsNullOrWhiteSpace(FanControlNote);

    /// <summary>Wires the gaming services (called from the constructor).</summary>
    private void InitGaming(GameProfileSwitcher switcher, IGameProfileService gameProfiles, IForegroundAppTracker tracker,
        IFrameRateMonitor frames, PcChecker.Core.Fans.IFanControlService fans)
    {
        _switcher = switcher;
        _gameProfiles = gameProfiles;
        _tracker = tracker;
        _frames = frames;
        _fans = fans;
        _switcher.StateChanged += (_, _) => DispatcherHelper.PostToUi(OnSwitcherStateChanged);
        GameProfiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGameProfiles));
        _frameStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _frameStatusTimer.Tick += (_, _) => UpdateFrameStatus();
    }

    private async Task LoadGamingAsync(CancellationToken cancellationToken)
    {
        LoadGameProfileOptions();
        RefreshGameProfiles();
        UpdateActiveProfile();
        UpdateFrameStatus();
        try
        {
            var plans = _gameProfiles is null ? [] : await _gameProfiles.GetPowerPlansAsync(cancellationToken);
            PowerPlanChoices.Clear();
            PowerPlanChoices.Add(new PowerPlanChoice(null, KeepPowerText));
            foreach (var plan in plans)
            {
                PowerPlanChoices.Add(new PowerPlanChoice(plan.Id, plan.IsActive ? $"{plan.Name} (active now)" : plan.Name));
            }

            RefreshGameProfiles();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Power plans could not be loaded for game profiles.");
        }
    }

    // ----- Profile list -----

    private void RefreshGameProfiles()
    {
        foreach (var row in GameProfiles)
        {
            row.EnabledToggled -= OnProfileEnabledToggled;
        }

        GameProfiles.Clear();
        var gaming = Settings.Current.Gaming;
        _loadingProfiles = true;
        try
        {
            AutoSwitch = gaming.AutoSwitch;
            OnlyWhenFullscreen = gaming.OnlyWhenFullscreen;
        }
        finally
        {
            _loadingProfiles = false;
        }

        var active = _switcher?.Active;
        foreach (var profile in gaming.Profiles)
        {
            bool isActive = active is not null && string.Equals(active.ProfileName, profile.Name, StringComparison.OrdinalIgnoreCase);
            var row = new GameProfileRow(profile, GameProfileRules.Describe(profile, PowerPlanName), isActive);
            row.EnabledToggled += OnProfileEnabledToggled;
            GameProfiles.Add(row);
        }
    }

    private string? PowerPlanName(Guid id) =>
        PowerPlanChoices.FirstOrDefault(p => p.Id == id)?.Text.Replace(" (active now)", string.Empty, StringComparison.Ordinal)
        ?? PowerPlanParser.DescribeKnown(id)
        ?? "a custom plan";

    private void LoadGameProfileOptions()
    {
        OverlayPresetChoices.Clear();
        OverlayPresetChoices.Add(new ProfileChoice(null, KeepLayoutText));
        foreach (var name in OverlayPresetCatalog.BuiltInNames.Concat(Settings.Current.Overlay.Presets.Select(p => p.Name)))
        {
            OverlayPresetChoices.Add(new ProfileChoice(name, name));
        }

        FanProfileChoices.Clear();
        FanProfileChoices.Add(new ProfileChoice(null, KeepFansText));
        var fanNames = new List<string>();
        try
        {
            if (_fans is not null)
            {
                fanNames.AddRange(_fans.BuiltInProfiles.Select(p => p.Name));
                FanControlNote = _fans.IsAvailable
                    ? null
                    : "Fan control is not available on this PC" + (_fans.UnavailableReason is { Length: > 0 } r ? $" ({r.TrimEnd('.')})" : string.Empty)
                      + ", so a fan profile is skipped when the game starts.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fan profiles could not be listed.");
        }

        fanNames.AddRange(Settings.Current.Fans.Profiles.Select(p => p.Name));
        foreach (var name in fanNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            FanProfileChoices.Add(new ProfileChoice(name, name));
        }

        OnPropertyChanged(nameof(FanControlNote));
        OnPropertyChanged(nameof(HasFanControlNote));
    }

    private void OnProfileEnabledToggled(object? sender, EventArgs e)
    {
        if (sender is not GameProfileRow row)
        {
            return;
        }

        var profile = Settings.Current.Gaming.Profiles.FirstOrDefault(p => string.Equals(p.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        if (profile is null || profile.Enabled == row.Enabled)
        {
            return;
        }

        profile.Enabled = row.Enabled;
        ScheduleSave();
    }

    partial void OnAutoSwitchChanged(bool value)
    {
        if (_loadingProfiles)
        {
            return;
        }

        Settings.Current.Gaming.AutoSwitch = value;
        ScheduleSave();
    }

    partial void OnOnlyWhenFullscreenChanged(bool value)
    {
        if (_loadingProfiles)
        {
            return;
        }

        Settings.Current.Gaming.OnlyWhenFullscreen = value;
        ScheduleSave();
    }

    private void OnSwitcherStateChanged()
    {
        UpdateActiveProfile();
        RefreshGameProfiles();
        if (IsInitialized)
        {
            // The switcher may have loaded a preset or shown/hidden the overlay.
            LoadFromSettings();
        }
    }

    private void UpdateActiveProfile()
    {
        var active = _switcher?.Active;
        ActiveProfileText = active is null ? null : $"Active: {active.ProfileName} profile (since {active.Since.LocalDateTime:HH:mm})";
        LastSwitchMessage = _switcher?.LastMessage;
    }

    // ----- Add, edit, remove -----

    [RelayCommand]
    private async Task AddRunningGameAsync()
    {
        IsPickerOpen = true;
        await RefreshRunningAppsAsync();
    }

    [RelayCommand]
    private async Task RefreshRunningAppsAsync()
    {
        if (_tracker is null)
        {
            return;
        }

        IsLoadingApps = true;
        try
        {
            var apps = await _tracker.GetRunningAppsAsync(CancellationToken.None);
            RunningApps.Clear();
            foreach (var app in apps)
            {
                RunningApps.Add(new RunningAppRow(app));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Running programs could not be listed.");
            _notifications.Warning("Game profiles", "Running programs could not be listed: " + ex.Message);
        }
        finally
        {
            IsLoadingApps = false;
        }
    }

    [RelayCommand]
    private void ClosePicker() => IsPickerOpen = false;

    [RelayCommand]
    private void PickRunningApp(RunningAppRow? row)
    {
        if (row is null)
        {
            return;
        }

        IsPickerOpen = false;
        OpenEditor(null, new GameProfile
        {
            Name = GameProfileMatcher.SuggestName(row.App.ExePath ?? row.ExeName, row.App.WindowTitle),
            ExeName = row.ExeName,
            ExePath = row.App.ExePath,
        });
    }

    [RelayCommand]
    private void BrowseForGame()
    {
        try
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the game's program file",
                Filter = "Programs (*.exe)|*.exe",
                CheckFileExists = true,
            };
            if (picker.ShowDialog() != true)
            {
                return;
            }

            IsPickerOpen = false;
            OpenEditor(null, new GameProfile
            {
                Name = GameProfileMatcher.SuggestName(picker.FileName, null),
                ExeName = GameProfileRules.CanonicalExeName(picker.FileName),
                ExePath = picker.FileName,
            });
        }
        catch (Exception ex)
        {
            _notifications.Warning("Could not open the file picker", ex.Message);
        }
    }

    [RelayCommand]
    private void EditGameProfile(GameProfileRow? row)
    {
        var profile = row is null ? null : Settings.Current.Gaming.Profiles.FirstOrDefault(p => string.Equals(p.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            OpenEditor(profile.Name, profile.Clone());
        }
    }

    [RelayCommand]
    private async Task RemoveGameProfileAsync(GameProfileRow? row)
    {
        if (row is null)
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync("Remove game profile",
            $"Remove the profile for {row.ExeName}? If the game is running now, its settings are put back first.", "Remove", isDanger: true);
        if (!confirmed)
        {
            return;
        }

        GameProfileRules.Remove(Settings.Current.Gaming, row.Name);
        RefreshGameProfiles();
        ScheduleSave();
    }

    [RelayCommand]
    private void SaveGameProfile()
    {
        var profile = new GameProfile
        {
            Name = EditName,
            ExeName = EditExeName,
            ExePath = EditExePath,
            Enabled = EditEnabled,
            OverlayPresetName = EditOverlayPreset?.Value,
            ShowOverlay = EditShowOverlay,
            FanProfileName = EditFanProfile?.Value,
            PowerPlanId = EditPowerPlan?.Id,
            RevertOnExit = EditRevertOnExit,
        };

        var error = GameProfileRules.Validate(profile, Settings.Current.Gaming.Profiles, _editingOriginalName);
        if (error is not null)
        {
            EditorError = error;
            return;
        }

        var stored = GameProfileRules.Upsert(Settings.Current.Gaming, profile, _editingOriginalName);
        var shadowed = GameProfileRules.SameExe(Settings.Current.Gaming, stored);
        IsEditorOpen = false;
        RefreshGameProfiles();
        ScheduleSave();
        _notifications.Success("Game profiles", shadowed.Count > 0
            ? $"\"{stored.Name}\" saved. Another profile also uses {stored.ExeName}; the first enabled one in the list is used."
            : $"\"{stored.Name}\" saved. It switches on by itself when {stored.ExeName} is in front.");
    }

    [RelayCommand]
    private void CancelGameProfileEdit()
    {
        IsEditorOpen = false;
        EditorError = null;
    }

    private void OpenEditor(string? originalName, GameProfile profile)
    {
        LoadGameProfileOptions();
        _editingOriginalName = originalName;
        EditorTitle = originalName is null ? "New game profile" : $"Edit \"{originalName}\"";
        EditName = profile.Name;
        EditExeName = profile.ExeName;
        EditExePath = profile.ExePath;
        EditEnabled = profile.Enabled;
        EditShowOverlay = profile.ShowOverlay;
        EditRevertOnExit = profile.RevertOnExit;
        EditOverlayPreset = OverlayPresetChoices.FirstOrDefault(c => string.Equals(c.Value, profile.OverlayPresetName, StringComparison.OrdinalIgnoreCase))
            ?? OverlayPresetChoices[0];
        EditFanProfile = FanProfileChoices.FirstOrDefault(c => string.Equals(c.Value, profile.FanProfileName, StringComparison.OrdinalIgnoreCase))
            ?? FanProfileChoices[0];
        if (PowerPlanChoices.Count == 0)
        {
            PowerPlanChoices.Add(new PowerPlanChoice(null, KeepPowerText));
        }

        EditPowerPlan = PowerPlanChoices.FirstOrDefault(c => c.Id == profile.PowerPlanId) ?? PowerPlanChoices[0];
        if (profile.PowerPlanId is { } missing && EditPowerPlan.Id is null)
        {
            var choice = new PowerPlanChoice(missing, PowerPlanParser.DescribeKnown(missing) ?? "Plan no longer on this PC");
            PowerPlanChoices.Add(choice);
            EditPowerPlan = choice;
        }

        EditorError = null;
        IsEditorOpen = true;
    }
}
