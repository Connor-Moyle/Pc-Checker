using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Fans page: what can be controlled on this PC, one card per fan (live RPM, manual slider or curve editor),
/// built-in and saved profiles, the safety alerts strip and the advanced raw-control list.
/// </summary>
public sealed partial class FansViewModel : ViewModelBase
{
    private readonly IFanControlService _fans;
    private readonly IFanControlAdvanced? _advanced;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<FansViewModel> _logger;
    private static readonly TimeSpan SettingsSaveDelay = TimeSpan.FromMilliseconds(750);
    private bool _controlConsentGiven;
    private bool _loadingSettings;
    private DateTimeOffset _lastSourceRefresh = DateTimeOffset.MinValue;
    private CancellationTokenSource? _pendingSave;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    private bool _isAvailable;

    [ObservableProperty]
    private string? _unavailableReason;

    [ObservableProperty]
    private string? _capabilityText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedProfile))]
    private FanProfileItem? _selectedProfile;

    /// <summary>The profile the fan service is running right now (null when every fan follows its own mode).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProfileText))]
    private string? _activeProfileName;

    /// <summary>The profile remembered in settings (what "apply at startup" would use); shown only when it is not the active one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProfileText))]
    private string? _lastAppliedProfileName;

    [ObservableProperty]
    private bool _revertToAutoOnExit;

    [ObservableProperty]
    private bool _applyOnStartup;

    [ObservableProperty]
    private double _minimumPercent;

    [ObservableProperty]
    private string? _newProfileName;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlerts))]
    private int _alertCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoftwareControlledText))]
    private int _softwareControlledCount;

    public FansViewModel(IFanControlService fans, ISettingsService settings, IDialogService dialogs, INotificationService notifications, ILogger<FansViewModel> logger)
        : base(settings)
    {
        _fans = fans;
        _advanced = fans as IFanControlAdvanced;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        LoadSettings();
        _fans.FansUpdated += OnFansUpdated;
        if (_advanced is not null)
        {
            _advanced.AlertRaised += OnAlertRaised;
        }
    }

    public ObservableCollection<FanItemViewModel> Fans { get; } = [];
    public ObservableCollection<FanProfileItem> Profiles { get; } = [];
    public ObservableCollection<FanControlAlert> Alerts { get; } = [];
    public ObservableCollection<FanTemperatureSource> TemperatureSources { get; } = [];
    public ObservableCollection<RawFanControl> RawControls { get; } = [];

    public bool IsUnavailable => !IsAvailable;
    public bool HasAlerts => AlertCount > 0;
    public bool HasFans => Fans.Count > 0;
    public bool CanDeleteSelectedProfile => SelectedProfile is { IsBuiltIn: false };
    public bool IsDemoMode => AppOptions.Mode == AppMode.Demo;
    public string ActiveProfileText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ActiveProfileName))
            {
                return $"Active profile: {ActiveProfileName}";
            }

            return string.IsNullOrWhiteSpace(LastAppliedProfileName)
                ? "No profile active — fans follow their own mode."
                : $"No profile active — fans follow their own mode. Last applied: {LastAppliedProfileName} (not re-applied this session).";
        }
    }
    public string SoftwareControlledText => SoftwareControlledCount switch
    {
        0 => "All fans are under automatic (firmware) control.",
        1 => "1 fan is controlled by PC Checker.",
        _ => $"{SoftwareControlledCount} fans are controlled by PC Checker.",
    };

    public IReadOnlyList<string> Presets => FanCurveMath.Presets;

    public override Task InitializeAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(LoadAsync, "Detecting fans…");

    [RelayCommand]
    private Task DetectAsync() => RefreshAsync(CancellationToken.None);

    private async Task LoadAsync(CancellationToken ct)
    {
        var fans = await _fans.GetFansAsync(ct);
        IsAvailable = _fans.IsAvailable;
        UnavailableReason = _fans.UnavailableReason;
        CapabilityText = _advanced is null ? null : string.Join("\n", _advanced.CapabilityNotes);

        MergeFans(fans);
        RefreshTemperatureSources(force: true);
        RefreshProfiles();
        RefreshRawControls();
        ActiveProfileName = _fans.ActiveProfile?.Name;
        LastAppliedProfileName = Settings.Current.Fans.ActiveProfileName;
        if (_advanced is not null)
        {
            Alerts.Clear();
            foreach (var a in _advanced.Alerts)
            {
                Alerts.Add(a);
            }

            AlertCount = Alerts.Count;
        }

        StatusText = IsAvailable
            ? $"{Fans.Count(f => f.Controllable)} controllable fan(s) and {Fans.Count(f => !f.Controllable)} read-only fan(s) found."
            : null;
    }

    private void MergeFans(IReadOnlyList<FanDevice> devices)
    {
        var byId = Fans.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var device in devices)
        {
            keep.Add(device.Id);
            if (byId.TryGetValue(device.Id, out var existing))
            {
                existing.Update(device);
                int current = Fans.IndexOf(existing);
                if (current != index)
                {
                    Fans.Move(current, index);
                }
            }
            else
            {
                var item = new FanItemViewModel(this, device);
                SeedCurveDefaults(item);
                Fans.Insert(index, item);
            }

            index++;
        }

        for (int i = Fans.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Fans[i].Id))
            {
                Fans.RemoveAt(i);
            }
        }

        SoftwareControlledCount = Fans.Count(f => f.IsSoftwareControlled);
        OnPropertyChanged(nameof(HasFans));
    }

    /// <summary>New cards start with the Balanced curve on the matching built-in temperature (CPU or GPU).</summary>
    private void SeedCurveDefaults(FanItemViewModel item)
    {
        var balanced = _fans.BuiltInProfiles.FirstOrDefault(p => p.Name == FanCurveMath.PresetBalanced);
        if (balanced is not null && balanced.Fans.TryGetValue(item.Id, out var assignment) && assignment.Curve is not null)
        {
            item.LoadCurve(assignment.Curve);
        }
    }

    private void RefreshTemperatureSources(bool force)
    {
        if (_advanced is null)
        {
            return;
        }

        if (!force && DateTimeOffset.Now - _lastSourceRefresh < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastSourceRefresh = DateTimeOffset.Now;
        IReadOnlyList<FanTemperatureSource> sources;
        try
        {
            sources = _advanced.GetTemperatureSources();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Temperature sources unavailable.");
            return;
        }

        if (force || sources.Count != TemperatureSources.Count || !sources.Select(s => s.Id).SequenceEqual(TemperatureSources.Select(s => s.Id)))
        {
            TemperatureSources.Clear();
            foreach (var s in sources)
            {
                TemperatureSources.Add(s);
            }
        }

        foreach (var fan in Fans)
        {
            if (fan.TemperatureSourceId is null && sources.Count > 0)
            {
                fan.TemperatureSourceId = (fan.Hardware == Core.Sensors.HardwareKind.Gpu
                    ? sources.FirstOrDefault(s => s.Hardware == Core.Sensors.HardwareKind.Gpu)
                    : sources.FirstOrDefault(s => s.Hardware == Core.Sensors.HardwareKind.Cpu))?.Id ?? sources[0].Id;
            }

            fan.CurrentTemperature = sources.FirstOrDefault(s => s.Id.Equals(fan.TemperatureSourceId, StringComparison.OrdinalIgnoreCase))?.Value;
        }
    }

    /// <summary>Called by a card when its temperature source changes.</summary>
    public void RefreshTemperature(FanItemViewModel fan)
    {
        var source = TemperatureSources.FirstOrDefault(s => s.Id.Equals(fan.TemperatureSourceId, StringComparison.OrdinalIgnoreCase));
        fan.CurrentTemperature = source?.Value;
    }

    private void RefreshRawControls()
    {
        if (_advanced is null)
        {
            return;
        }

        try
        {
            RawControls.Clear();
            foreach (var raw in _advanced.GetRawControls())
            {
                RawControls.Add(raw);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Raw control list unavailable.");
        }
    }

    private void OnFansUpdated(object? sender, IReadOnlyList<FanDevice> devices) => DispatcherHelper.PostToUi(() =>
    {
        if (IsBusy)
        {
            return;
        }

        MergeFans(devices);
        RefreshTemperatureSources(force: false);
    });

    private void OnAlertRaised(object? sender, FanControlAlert alert) => DispatcherHelper.PostToUi(() =>
    {
        Alerts.Add(alert);
        while (Alerts.Count > 20)
        {
            Alerts.RemoveAt(0);
        }

        AlertCount = Alerts.Count;
        if (alert.Severity >= Severity.High)
        {
            _notifications.Warning("Fans forced to full speed", alert.Message);
        }
        else
        {
            _notifications.Info("Fan control", alert.Message);
        }
    });

    [RelayCommand]
    private void ClearAlerts()
    {
        Alerts.Clear();
        AlertCount = 0;
    }

    /// <summary>One-time explanation before the app takes over any fan in this session.</summary>
    private async Task<bool> EnsureControlConsentAsync()
    {
        if (_controlConsentGiven)
        {
            return true;
        }

        bool ok = await _dialogs.ConfirmAsync("Let PC Checker control your fans?",
            "PC Checker will set fan speeds through the motherboard sensor chip or graphics driver instead of the firmware.\n\n"
            + "• Fans never go below the minimum speed you set, and are forced to 100 % if a part reaches its critical temperature.\n"
            + "• Everything returns to automatic when you click \"Revert all to automatic\" or close the app (if that option is on).\n"
            + "• If the app crashes, the next start puts the fans back to automatic.",
            "Take control");
        _controlConsentGiven = ok;
        return ok;
    }

    internal async Task ApplyManualAsync(FanItemViewModel fan)
    {
        if (!await EnsureControlConsentAsync())
        {
            return;
        }

        double percent = Math.Round(fan.ManualPercent);
        bool allowBelow = false;
        if (percent < MinimumPercent)
        {
            allowBelow = await _dialogs.ConfirmAsync("Run below the minimum fan speed?",
                $"You asked for {percent:0} %, below the {MinimumPercent:0} % minimum set on this page. Very low speeds can stall some fans and let parts run hot. The safety net still forces 100 % at critical temperatures.",
                "Allow this once", isDanger: true);
            if (!allowBelow)
            {
                return;
            }
        }

        await RunBusyAsync(async ct =>
        {
            var result = _advanced is not null
                ? await _advanced.SetManualAsync(fan.Id, percent, allowBelow, ct)
                : await _fans.SetManualAsync(fan.Id, percent, ct);
            Report(result);
            if (result.Success)
            {
                fan.Mode = FanMode.Manual;
                SoftwareControlledCount = Fans.Count(f => f.IsSoftwareControlled);
            }
        }, $"Setting {fan.Name} to {percent:0} %…");
    }

    internal async Task ApplyCurveAsync(FanItemViewModel fan)
    {
        var curve = fan.BuildCurve();
        var problem = FanCurveMath.Validate(curve);
        if (problem is not null)
        {
            fan.CurveProblem = problem;
            return;
        }

        fan.CurveProblem = null;
        if (!await EnsureControlConsentAsync())
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            OperationResult result;
            if (_advanced is not null)
            {
                result = await _advanced.SetCurveAsync(fan.Id, curve, ct);
            }
            else
            {
                var profile = new FanProfile { Name = $"{fan.Name} curve" };
                profile.Fans[fan.Id] = new FanAssignment { Mode = FanMode.Curve, Curve = curve };
                result = await _fans.ApplyProfileAsync(profile, ct);
            }

            Report(result);
            if (result.Success)
            {
                fan.Mode = FanMode.Curve;
                SoftwareControlledCount = Fans.Count(f => f.IsSoftwareControlled);
            }
        }, $"Applying curve to {fan.Name}…");
    }

    internal async Task SetAutoAsync(FanItemViewModel fan)
    {
        if (IsBusy)
        {
            // RunBusyAsync would silently skip the work; keep the selector honest instead.
            fan.SyncSelectedMode();
            StatusText = "Another fan operation is still running. Wait for it to finish, then try again.";
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _fans.SetAutoAsync(fan.Id, ct);
            Report(result);
            if (result.Success)
            {
                fan.Mode = FanMode.Auto;
                SoftwareControlledCount = Fans.Count(f => f.IsSoftwareControlled);
            }
        }, $"Returning {fan.Name} to automatic…");

        if (fan.Mode != FanMode.Auto)
        {
            fan.SyncSelectedMode();
        }
    }

    /// <summary>Called from the card's "Automatic" radio: runs <see cref="SetAutoAsync"/> and never lets the selector drift from reality.</summary>
    internal void SetAutoFromSelector(FanItemViewModel fan)
    {
        _ = RunSelectorAsync(fan);

        async Task RunSelectorAsync(FanItemViewModel item)
        {
            try
            {
                await SetAutoAsync(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Returning {Fan} to automatic from the mode selector failed.", item.Name);
                item.SyncSelectedMode();
            }
        }
    }

    [RelayCommand]
    private void SimulateOverheat()
    {
        if (_fans is DemoFanControlService demo)
        {
            demo.SimulateOverheat();
        }
    }

    private void Report(OperationResult result)
    {
        if (result.Success)
        {
            StatusText = result.Message;
            ErrorMessage = null;
        }
        else
        {
            ErrorMessage = result.Message;
            _logger.LogWarning("Fan operation failed: {Message}", result.Message);
        }
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var s = Settings.Current.Fans;
            RevertToAutoOnExit = s.RevertToAutoOnExit;
            ApplyOnStartup = s.ApplyOnStartup;
            MinimumPercent = s.MinimumPercent;
            LastAppliedProfileName = s.ActiveProfileName;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    partial void OnRevertToAutoOnExitChanged(bool value) => Persist(s => s.RevertToAutoOnExit = value);
    partial void OnApplyOnStartupChanged(bool value) => Persist(s => s.ApplyOnStartup = value);
    partial void OnMinimumPercentChanged(double value) => Persist(s => s.MinimumPercent = Math.Clamp(Math.Round(value), 0, 100));

    /// <summary>Applies a page-level setting and saves shortly afterwards, so spinning a NumberBox writes the file once, not per step.</summary>
    private void Persist(Action<FanSettings> apply)
    {
        if (_loadingSettings)
        {
            return;
        }

        apply(Settings.Current.Fans);
        _pendingSave?.Cancel();
        _pendingSave?.Dispose();
        var cts = new CancellationTokenSource();
        _pendingSave = cts;
        _ = SaveSettingsLaterAsync(cts.Token);
    }

    private async Task SaveSettingsLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SettingsSaveDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save fan settings.");
            ErrorMessage = "Settings could not be saved: " + ex.Message;
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Fans page operation failed.");
}

/// <summary>A profile row in the picker: name plus a short description.</summary>
public sealed record FanProfileItem(FanProfile Profile, bool IsBuiltIn)
{
    public string Name => Profile.Name;

    public string Description => (IsBuiltIn ? "Built-in" : "Saved") + $" • {Profile.Fans.Count} fan{(Profile.Fans.Count == 1 ? string.Empty : "s")}";

    public string Tooltip => IsBuiltIn ? FanCurveMath.DescribePreset(Profile.Name) : $"Saved profile with {Profile.Fans.Count} fan assignment(s).";
}
