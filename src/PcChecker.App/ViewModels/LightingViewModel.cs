using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Lighting;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>View model for the Lighting page: OpenRGB connection, quick controls, profiles and one card per device.</summary>
public sealed partial class LightingViewModel : ViewModelBase
{
    private readonly ILightingService _lighting;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ILogger<LightingViewModel> _logger;
    private CancellationTokenSource? _reloadDebounce;
    private bool _installProbed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(StatusIconName))]
    private bool _isConnected;

    [ObservableProperty]
    private string _status = LightingStatusText.Connecting;

    [ObservableProperty]
    private bool _isOpenRgbInstalled;

    [ObservableProperty]
    private string? _openRgbPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncColorBrush))]
    private Color _syncColor = Color.FromRgb(0x4C, 0xC2, 0xFF);

    [ObservableProperty]
    private bool _isSyncPickerOpen;

    [ObservableProperty]
    private int _allBrightness = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteProfile))]
    [NotifyPropertyChangedFor(nameof(SelectedProfileSummary))]
    private LightingProfile? _selectedProfile;

    [ObservableProperty]
    private string? _newProfileName;

    [ObservableProperty]
    private string? _lastMessage;

    [ObservableProperty]
    private string? _activeProfileName;

    [ObservableProperty]
    private bool _showWhyOpenRgb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasInitialized;

    public LightingViewModel(ISettingsService settings, ILightingService lighting, INotificationService notifications, IDialogService dialogs, ILogger<LightingViewModel> logger)
        : base(settings)
    {
        _lighting = lighting;
        _notifications = notifications;
        _dialogs = dialogs;
        _logger = logger;
        _lighting.DevicesChanged += OnDevicesChanged;
        _activeProfileName = settings.Current.Lighting.ActiveProfileName;
        RebuildProfiles();
        ReloadWhenReplaced(s => s.Lighting, RebuildProfiles);
        // Status is pulled in InitializeAsync: the first read probes the OpenRGB install (registry, PATH), which must
        // not run on the UI thread.
    }

    public ObservableCollection<LightingDeviceViewModel> Devices { get; } = [];
    public ObservableCollection<LightingDeviceViewModel> Components { get; } = [];
    public ObservableCollection<LightingDeviceViewModel> Peripherals { get; } = [];
    public ObservableCollection<LightingProfile> Profiles { get; } = [];

    public IReadOnlyList<VendorSoftwareLink> VendorSoftware { get; } =
        LightingStatusText.VendorSoftware.Select(v => new VendorSoftwareLink(v.Vendor, v.Product, v.Url)).ToList();

    public string WhyOpenRgbText => LightingStatusText.WhyOpenRgb;
    public string OpenRgbDownloadUrl => LightingStatusText.DownloadUrl;
    public string ServerAddress => $"{Settings.Current.Lighting.OpenRgbHost}:{Settings.Current.Lighting.OpenRgbPort}";
    public bool HasDevices => Devices.Count > 0;
    public bool HasComponents => Components.Count > 0;
    public bool HasPeripherals => Peripherals.Count > 0;
    public bool ShowEmptyState => HasInitialized && (!IsConnected || !HasDevices);
    public string ConnectButtonText => IsConnected ? "Reconnect" : "Connect";
    public string StatusIconName => IsConnected ? "PlugConnected24" : "PlugDisconnected24";
    public Brush SyncColorBrush => new SolidColorBrush(SyncColor);
    public bool CanDeleteProfile => SelectedProfile is { IsBuiltIn: false };
    public string EmptyStateMessage => IsOpenRgbInstalled
        ? "Start OpenRGB with its SDK server enabled (or press \"Start OpenRGB server\"), then connect to see and control your RGB devices."
        : "Install OpenRGB from openrgb.org, start it with the SDK server enabled, then connect. The vendor apps listed in the status card are an alternative.";

    public string SelectedProfileSummary => SelectedProfile switch
    {
        null => "Pick a profile to apply it to every device.",
        { AllColor: { } c, Devices.Count: 0 } => c == RgbColor.Black ? "Turns every device off." : $"Sets every device to {c.ToHex()}.",
        { Devices.Count: > 0 } p => $"Per-device settings for {p.Devices.Count} entr{(p.Devices.Count == 1 ? "y" : "ies")}" + (p.AllColor is { } all ? $", others {all.ToHex()}." : "."),
        _ => "Applies the saved modes and colours.",
    };

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ProbeInstallAsync();
        PullStatus();
        if (Settings.Current.Lighting.AutoConnect)
        {
            await RunBusyAsync(async ct =>
            {
                var result = await _lighting.ConnectAsync(ct);
                PullStatus();
                if (result.Success)
                {
                    await LoadDevicesAsync(ct);
                }
                else
                {
                    _logger.LogInformation("Lighting: {Status}", result.Message);
                }
            }, "Connecting to OpenRGB…");
        }

        HasInitialized = true;
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(async ct =>
    {
        PullStatus();
        await LoadDevicesAsync(ct);
    }, "Refreshing devices…");

    [RelayCommand]
    private Task RefreshDevicesAsync() => RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private Task ConnectAsync() => RunBusyAsync(async ct =>
    {
        var result = await _lighting.ConnectAsync(ct);
        PullStatus();
        if (result.Success)
        {
            await LoadDevicesAsync(ct);
            _notifications.Success("Lighting", Status);
        }
        else
        {
            ErrorMessage = result.Message;
        }
    }, "Connecting to OpenRGB…");

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _lighting.DisconnectAsync();
        PullStatus();
        ClearDevices();
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    [RelayCommand]
    private Task LaunchServerAsync() => RunBusyAsync(async ct =>
    {
        var result = await _lighting.LaunchOpenRgbServerAsync(ct);
        PullStatus();
        if (result.Success)
        {
            await LoadDevicesAsync(ct);
            _notifications.Success("Lighting", Status);
        }
        else
        {
            ErrorMessage = result.Message;
        }
    }, "Starting the OpenRGB server (this can take a few seconds)…");

    [RelayCommand]
    private void OpenDownloadPage() => Report(ShellLauncher.Open(LightingStatusText.DownloadUrl));

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            Report(ShellLauncher.Open(url));
        }
    }

    [RelayCommand]
    private void ToggleWhyOpenRgb() => ShowWhyOpenRgb = !ShowWhyOpenRgb;

    [RelayCommand]
    private void ToggleSyncPicker() => IsSyncPickerOpen = !IsSyncPickerOpen;

    [RelayCommand]
    private Task ApplySyncColorAsync() => RunBusyAsync(async ct =>
    {
        var rgb = new RgbColor(SyncColor.R, SyncColor.G, SyncColor.B);
        var result = await _lighting.SetAllAsync(LightingApplyPlanner.EffectiveColor(rgb, AllBrightness), ct);
        Report(result);
        if (result.Success)
        {
            ActiveProfileName = null;
            foreach (var device in Devices)
            {
                device.Adopt(SyncColor, AllBrightness);
            }

            await LoadDevicesAsync(ct);
        }
    }, "Applying colour to every device…");

    [RelayCommand]
    private Task ApplyBrightnessAsync() => RunBusyAsync(async ct =>
    {
        var failures = 0;
        foreach (var device in Devices.ToList())
        {
            if (!device.SupportsColor)
            {
                continue;
            }

            // Dim each device's base colour (not the colour currently on the LEDs) so repeated changes do not compound.
            var rgb = new RgbColor(device.Color.R, device.Color.G, device.Color.B);
            var r = await _lighting.SetDeviceColorAsync(device.Index, LightingApplyPlanner.EffectiveColor(rgb, AllBrightness), ct);
            if (r.Success)
            {
                device.Adopt(device.Color, AllBrightness);
            }
            else
            {
                failures++;
            }
        }

        Report(failures == 0
            ? OperationResult.Ok($"Brightness set to {AllBrightness} % on {Devices.Count(d => d.SupportsColor)} device(s).")
            : OperationResult.Fail($"{failures} device(s) did not accept the brightness change."));
        await LoadDevicesAsync(ct);
    }, "Applying brightness…");

    [RelayCommand]
    private Task TurnOffAllAsync() => RunBusyAsync(async ct =>
    {
        var result = await _lighting.TurnOffAllAsync(ct);
        Report(result);
        if (result.Success)
        {
            ActiveProfileName = "Night (off)";
            await LoadDevicesAsync(ct);
        }
    }, "Turning lighting off…");

    [RelayCommand]
    private Task ApplyProfileAsync() => ApplyProfileCoreAsync(SelectedProfile);

    [RelayCommand]
    private Task ApplyRainbowAsync() => ApplyProfileCoreAsync(_lighting.BuiltInProfiles.FirstOrDefault(p => p.Name.StartsWith("Rainbow", StringComparison.OrdinalIgnoreCase)));

    private Task ApplyProfileCoreAsync(LightingProfile? profile)
    {
        if (profile is null)
        {
            ErrorMessage = "Choose a profile first.";
            return Task.CompletedTask;
        }

        return RunBusyAsync(async ct =>
        {
            var result = await _lighting.ApplyProfileAsync(profile, ct);
            Report(result);
            if (result.Success)
            {
                ActiveProfileName = profile.Name;
                Settings.Current.Lighting.ActiveProfileName = profile.Name;
                await SaveSettingsAsync();
                await LoadDevicesAsync(ct);
            }
        }, $"Applying \"{profile.Name}\"…");
    }

    /// <summary>Saves the current mode/colour/speed/brightness of every device as a custom profile.</summary>
    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        var name = NewProfileName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ErrorMessage = "Give the profile a name first.";
            return;
        }

        if (_lighting.BuiltInProfiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = $"\"{name}\" is a built-in profile; choose another name.";
            return;
        }

        if (Devices.Count == 0)
        {
            ErrorMessage = "Connect to OpenRGB first so there is something to save.";
            return;
        }

        var existing = Settings.Current.Lighting.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !await _dialogs.ConfirmAsync("Replace profile?", $"A profile called \"{name}\" already exists. Replace it with the current lighting?", "Replace"))
        {
            return;
        }

        var profile = BuildProfileFromDevices(name, Devices);
        if (existing is not null)
        {
            Settings.Current.Lighting.Profiles.Remove(existing);
        }

        Settings.Current.Lighting.Profiles.Add(profile);
        Settings.Current.Lighting.ActiveProfileName = name;
        await SaveSettingsAsync();
        RebuildProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == name);
        ActiveProfileName = name;
        NewProfileName = null;
        _notifications.Success("Profile saved", $"\"{name}\" now holds the current lighting of {profile.Devices.Count} device(s).");
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { IsBuiltIn: false } profile)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Delete profile?", $"Delete the lighting profile \"{profile.Name}\"? Your devices keep their current colours.", "Delete", isDanger: true))
        {
            return;
        }

        Settings.Current.Lighting.Profiles.RemoveAll(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (Settings.Current.Lighting.ActiveProfileName == profile.Name)
        {
            Settings.Current.Lighting.ActiveProfileName = null;
            ActiveProfileName = null;
        }

        await SaveSettingsAsync();
        RebuildProfiles();
        SelectedProfile = Profiles.FirstOrDefault();
        _notifications.Info("Profile deleted", $"\"{profile.Name}\" was removed.");
    }

    /// <summary>Snapshot of what every device card currently shows, keyed by device name. Pure, so tests can cover it.</summary>
    public static LightingProfile BuildProfileFromDevices(string name, IEnumerable<LightingDeviceViewModel> devices)
    {
        var profile = new LightingProfile { Name = name, IsBuiltIn = false };
        foreach (var d in devices)
        {
            profile.Devices[d.Name] = new LightingAssignment
            {
                ModeName = d.SelectedMode?.Name,
                Color = d.SupportsColor ? new RgbColor(d.Color.R, d.Color.G, d.Color.B) : null,
                Brightness = d.SupportsBrightness ? d.Brightness : null,
                Speed = d.SupportsSpeed && d.SelectedMode is { } m ? LightingApplyPlanner.SpeedFromPercent(m, d.Speed) : null,
            };
        }

        return profile;
    }

    // ---- internals -------------------------------------------------------------------------------------------

    /// <summary>Warms the service's install lookup off the UI thread; every later status read is then a cached value.</summary>
    private async Task ProbeInstallAsync()
    {
        if (_installProbed)
        {
            return;
        }

        try
        {
            await Task.Run(() => _lighting.IsOpenRgbInstalled);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OpenRGB install probe failed.");
        }

        _installProbed = true;
    }

    private void PullStatus()
    {
        if (!_installProbed)
        {
            // Status text depends on the install probe; keep "Connecting…" until InitializeAsync has run it.
            return;
        }

        IsConnected = _lighting.IsConnected;
        Status = _lighting.Status;
        IsOpenRgbInstalled = _lighting.IsOpenRgbInstalled;
        OpenRgbPath = _lighting.OpenRgbPath;
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(ServerAddress));
    }

    private async Task LoadDevicesAsync(CancellationToken ct)
    {
        var devices = await _lighting.GetDevicesAsync(ct);
        await DispatcherHelper.RunOnUiAsync(() => ApplyDeviceList(devices));
    }

    private void ApplyDeviceList(IReadOnlyList<RgbDevice> devices)
    {
        var byIndex = Devices.ToDictionary(d => d.Index);
        var kept = new HashSet<int>();
        foreach (var device in devices)
        {
            kept.Add(device.Index);
            if (byIndex.TryGetValue(device.Index, out var vm) && vm.Name == device.Name)
            {
                vm.Sync(device);
            }
            else
            {
                if (vm is not null)
                {
                    Devices.Remove(vm);
                }

                Devices.Add(new LightingDeviceViewModel(device, _lighting, Report));
            }
        }

        foreach (var stale in Devices.Where(d => !kept.Contains(d.Index)).ToList())
        {
            Devices.Remove(stale);
        }

        Components.Clear();
        Peripherals.Clear();
        foreach (var d in Devices.OrderBy(d => d.Index))
        {
            (d.IsPeripheral ? Peripherals : Components).Add(d);
        }

        PullStatus();
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasComponents));
        OnPropertyChanged(nameof(HasPeripherals));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void ClearDevices()
    {
        Devices.Clear();
        Components.Clear();
        Peripherals.Clear();
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasComponents));
        OnPropertyChanged(nameof(HasPeripherals));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        // The server (or our own change) updated the device list: refresh the cards after a short pause so a burst
        // of slider changes results in one reload. Raised from service worker threads, so the debounce token is
        // swapped atomically and a token another event already disposed is ignored.
        try
        {
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _reloadDebounce, cts);
            if (previous is not null)
            {
                try
                {
                    previous.Cancel();
                    previous.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _ = ReloadLaterAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lighting device change handler failed.");
        }
    }

    private async Task ReloadLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            if (IsBusy)
            {
                return;
            }

            DispatcherHelper.PostToUi(PullStatus);
            if (_lighting.IsConnected)
            {
                await LoadDevicesAsync(token);
            }
            else
            {
                DispatcherHelper.PostToUi(ClearDevices);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lighting device reload failed.");
        }
    }

    private void Report(OperationResult result)
    {
        DispatcherHelper.PostToUi(() =>
        {
            LastMessage = result.Message;
            if (!result.Success)
            {
                ErrorMessage = result.Message;
                _logger.LogWarning("Lighting: {Message}", result.Message);
            }
        });
    }

    private void RebuildProfiles()
    {
        var selected = SelectedProfile?.Name;
        Profiles.Clear();
        foreach (var p in _lighting.BuiltInProfiles)
        {
            Profiles.Add(p);
        }

        foreach (var p in Settings.Current.Lighting.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Profiles.Add(p);
        }

        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == (selected ?? Settings.Current.Lighting.ActiveProfileName)) ?? Profiles.FirstOrDefault();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save lighting settings.");
            ErrorMessage = "Settings could not be saved: " + ex.Message;
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Lighting page operation failed.");
}

/// <summary>A vendor RGB suite the user can fall back to.</summary>
public sealed record VendorSoftwareLink(string Vendor, string Product, string Url)
{
    public string Label => $"{Vendor} {Product}";
}

/// <summary>Media.Color, RgbColor or "#RRGGBB" → SolidColorBrush (colour swatches on the Lighting page).</summary>
public sealed class LightingColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        Color color = value switch
        {
            Color c => c,
            RgbColor rgb => Color.FromRgb(rgb.R, rgb.G, rgb.B),
            string s when RgbColorMath.TryParseHex(s, out var parsed) => Color.FromRgb(parsed.R, parsed.G, parsed.B),
            _ => Color.FromArgb(0x40, 0x80, 0x80, 0x80),
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Icon name ("PlugConnected24") → <see cref="Wpf.Ui.Controls.SymbolRegular"/> for the Lighting status card.</summary>
public sealed class LightingSymbolNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(s, out var symbol) ? symbol : Wpf.Ui.Controls.SymbolRegular.Info24;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
