using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Input;

namespace PcChecker.App.ViewModels;

/// <summary>Polling rate meter, double-click tester, profiles, detected devices and launchers.</summary>
public sealed partial class InputViewModel
{
    private const int PollingSeconds = 5;
    private const string CustomProfilePrefix = "input.profile.custom.";

    private readonly Stopwatch _clickClock = new();
    private CancellationTokenSource? _pollingCts;

    // Polling rate
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotMeasuring))]
    [NotifyCanExecuteChangedFor(nameof(MeasurePollingRateCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopPollingRateCommand))]
    private bool _isMeasuring;

    [ObservableProperty] private int _liveHz;
    [ObservableProperty] private int _measuredHz;
    [ObservableProperty] private int _secondsLeft;
    [ObservableProperty] private PollingRateReading? _pollingResult;
    [ObservableProperty] private string _pollingStatus = "Press Measure, then keep the mouse moving in circles for 5 seconds.";

    // Double-click tester
    [ObservableProperty] private string _clickTestText = "Double-click here to test the speed above.";
    [ObservableProperty] private bool _clickTestPassed;
    [ObservableProperty] private int _clickTestCount;

    // Profiles
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    private string _newProfileName = string.Empty;

    public bool IsNotMeasuring => !IsMeasuring;
    public bool HasPollingResult => PollingResult is not null;
    public bool CanSaveProfile => !string.IsNullOrWhiteSpace(NewProfileName);

    partial void OnPollingResultChanged(PollingRateReading? value) => OnPropertyChanged(nameof(HasPollingResult));

    // ----- Polling rate -----

    [RelayCommand(CanExecute = nameof(IsNotMeasuring))]
    private async Task MeasurePollingRateAsync()
    {
        if (IsMeasuring)
        {
            return;
        }

        _pollingCts?.Dispose();
        _pollingCts = new CancellationTokenSource();
        var ct = _pollingCts.Token;
        IsMeasuring = true;
        LiveHz = 0;
        MeasuredHz = 0;
        PollingResult = null;
        SecondsLeft = PollingSeconds;
        PollingStatus = "Keep the mouse moving in circles…";
        var countdown = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var countdownTask = Task.Run(async () =>
        {
            try
            {
                while (SecondsLeft > 0 && await countdown.WaitForNextTickAsync(ct))
                {
                    DispatcherHelper.PostToUi(() => SecondsLeft = Math.Max(0, SecondsLeft - 1));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);

        try
        {
            var progress = new Progress<int>(hz => LiveHz = hz);
            int hz = await _pollingMeter.MeasureAsync(PollingSeconds, progress, ct);
            MeasuredHz = hz;
            PollingResult = PollingRateMath.Interpret(hz);
            PollingStatus = hz > 0
                ? $"Measured {hz} Hz — about {PollingResult.Label}."
                : "No mouse reports were received. Keep the mouse moving the whole time and try again.";
            _logger.LogInformation("Polling rate test finished: {Hz} Hz.", hz);
        }
        catch (OperationCanceledException)
        {
            PollingStatus = "Measurement stopped.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polling rate measurement failed.");
            PollingStatus = "The measurement could not run: " + ex.Message;
        }
        finally
        {
            SecondsLeft = 0;
            countdown.Dispose();
            try
            {
                await countdownTask;
            }
            catch (OperationCanceledException)
            {
            }

            IsMeasuring = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsMeasuring))]
    private void StopPollingRate()
    {
        try
        {
            _pollingCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ----- Double-click tester -----

    /// <summary>Called on every click of the test target; two clicks inside the double-click window count as one double-click.</summary>
    [RelayCommand]
    private void ClickTest()
    {
        if (!_clickClock.IsRunning)
        {
            _clickClock.Start();
            ClickTestText = "First click registered — click again quickly.";
            ClickTestPassed = false;
            return;
        }

        long elapsed = _clickClock.ElapsedMilliseconds;
        _clickClock.Restart();
        if (elapsed <= DoubleClickMs)
        {
            ClickTestCount++;
            ClickTestPassed = true;
            ClickTestText = $"Double-click detected ({elapsed} ms between clicks, limit {DoubleClickMs} ms). Total: {ClickTestCount}.";
            _clickClock.Reset();
        }
        else
        {
            ClickTestPassed = false;
            ClickTestText = $"Too slow: {elapsed} ms between clicks, limit {DoubleClickMs} ms. Raise the slider or click faster.";
        }
    }

    // ----- Profiles -----

    private void RebuildProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _input.BuiltInProfiles)
        {
            Profiles.Add(new InputProfileItem(profile));
        }

        foreach (var profile in Settings.Current.Input.Profiles)
        {
            Profiles.Add(new InputProfileItem(profile));
        }

        MarkActiveProfile();
    }

    private void MarkActiveProfile()
    {
        foreach (var item in Profiles)
        {
            item.IsActive = InputMath.SameAs(item.Profile.Mouse, _loadedMouse) && InputMath.SameAs(item.Profile.Keyboard, _loadedKeyboard);
        }
    }

    [RelayCommand]
    private async Task ApplyProfileAsync(InputProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        var mouseChanges = InputMath.Describe(_loadedMouse, item.Profile.Mouse);
        var keyboardChanges = InputMath.Describe(_loadedKeyboard, item.Profile.Keyboard);
        var all = mouseChanges.Concat(keyboardChanges).ToList();
        string detail = all.Count == 0
            ? "Your current settings already match this profile; applying it re-writes the same values."
            : "These changes will be applied to Windows right away:\n\n• " + string.Join("\n• ", all);
        bool ok = await _dialogs.ConfirmAsync($"Apply the \"{item.Name}\" profile?", detail + "\n\nYou can undo this with the \"Windows default\" profile.", "Apply");
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _input.ApplyProfileAsync(item.Profile, ct);
            Report(result, "Profile");
            await LoadSettingsAsync(ct);
        }, $"Applying \"{item.Name}\"…");
    }

    /// <summary>Loads a profile's values into the editors without applying them, so they can be tweaked first.</summary>
    [RelayCommand]
    private void LoadProfileIntoEditors(InputProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        ShowMouse(item.Profile.Mouse);
        ShowKeyboard(item.Profile.Keyboard);
        StatusMessage = $"\"{item.Name}\" loaded into the editors. Press Apply on each card to use it.";
    }

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        string name = NewProfileName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var existing = Settings.Current.Input.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            bool replace = await _dialogs.ConfirmAsync("Replace profile?", $"A profile called \"{name}\" already exists. Replace it with the current editor values?", "Replace");
            if (!replace)
            {
                return;
            }

            Settings.Current.Input.Profiles.Remove(existing);
        }

        var profile = new InputProfile(
            existing?.Id ?? CustomProfilePrefix + Guid.NewGuid().ToString("N")[..8],
            name,
            $"Custom profile saved {DateTime.Now:d}: {InputProfileItem.Summarize(InputMath.Normalize(BuildMouse()), InputMath.Normalize(BuildKeyboard()))}",
            InputMath.Normalize(BuildMouse()),
            InputMath.Normalize(BuildKeyboard()),
            false);
        Settings.Current.Input.Profiles.Add(profile);
        await SaveSettingsAsync();
        NewProfileName = string.Empty;
        RebuildProfiles();
        _notifications.Success("Profile saved", $"\"{name}\" is now in your profiles list.");
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(InputProfileItem? item)
    {
        if (item is null || item.IsBuiltIn)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync("Delete profile?", $"Remove the \"{item.Name}\" profile? Your current Windows settings are not changed.", "Delete", isDanger: true);
        if (!ok)
        {
            return;
        }

        Settings.Current.Input.Profiles.RemoveAll(p => p.Id == item.Profile.Id);
        await SaveSettingsAsync();
        RebuildProfiles();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save input profiles.");
            ErrorMessage = "Profiles could not be saved: " + ex.Message;
        }
    }

    // ----- Devices -----

    private async Task LoadDevicesAsync(bool scanIfMissing, CancellationToken ct)
    {
        var inventory = _hardware.Last;
        if (inventory is null && scanIfMissing)
        {
            BusyText = "Detecting keyboards and mice…";
            var progress = new Progress<ProgressUpdate>(p => BusyText = "Detecting keyboards and mice… " + p.Stage);
            try
            {
                inventory = await _hardware.ScanAsync(progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hardware scan for input devices failed.");
                StatusMessage = "Connected keyboards and mice could not be listed: " + ex.Message;
            }
        }

        var devices = inventory?.InputDevices ?? [];
        DispatcherHelper.RunOnUi(() =>
        {
            Devices.Clear();
            foreach (var device in devices.OrderBy(d => d.Kind).ThenBy(d => d.Name))
            {
                Devices.Add(new InputDeviceItem(device));
            }

            VendorLinks.Clear();
            foreach (var link in _input.VendorSoftwareLinks(devices))
            {
                VendorLinks.Add(link);
            }

            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(HasVendorLinks));
        });
    }

    [RelayCommand]
    private Task DetectDevicesAsync() => RunBusyAsync(async ct =>
    {
        await _hardware.ScanAsync(new Progress<ProgressUpdate>(p => BusyText = "Detecting keyboards and mice… " + p.Stage), ct);
        await LoadDevicesAsync(scanIfMissing: false, ct);
    }, "Detecting keyboards and mice…");

    // ----- Launchers -----

    [RelayCommand]
    private void OpenMouseSettings() => ReportLaunch(_input.OpenMouseSettings());

    [RelayCommand]
    private void OpenKeyboardSettings() => ReportLaunch(_input.OpenKeyboardSettings());

    [RelayCommand]
    private void OpenMouseControlPanel() => ReportLaunch(_input.OpenMouseControlPanel());

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            ReportLaunch(ShellLauncher.Open(url));
        }
    }

    [RelayCommand]
    private void GoToLighting()
    {
        try
        {
            _navigation.Navigate(typeof(LightingPage));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not navigate to the Lighting page.");
        }
    }

    private void ReportLaunch(OperationResult result)
    {
        if (!result.Success)
        {
            _notifications.Warning("Could not open", result.Message);
        }
    }
}

/// <summary>A profile row: the profile plus whether the live settings currently match it.</summary>
public sealed partial class InputProfileItem : ObservableObject
{
    [ObservableProperty] private bool _isActive;

    public InputProfileItem(InputProfile profile)
    {
        Profile = profile;
        Summary = Summarize(profile.Mouse, profile.Keyboard);
    }

    public InputProfile Profile { get; }
    public string Name => Profile.Name;
    public string Description => Profile.Description;
    public bool IsBuiltIn => Profile.IsBuiltIn;
    public bool IsCustom => !Profile.IsBuiltIn;
    public string Summary { get; }

    public static string Summarize(MouseSettings mouse, KeyboardSettings keyboard) =>
        $"Speed {InputMath.SpeedToSliderText(mouse.Speed)} · Precision {(mouse.EnhancePointerPrecision ? "on" : "off")} · Double-click {mouse.DoubleClickMs} ms"
        + $" · Trails {InputMath.TrailsText(mouse.PointerTrails).ToLowerInvariant()} · Repeat delay {InputMath.RepeatDelayToMs(keyboard.RepeatDelay)} ms"
        + (keyboard.StickyKeys ? " · Sticky Keys" : string.Empty);
}

/// <summary>A detected keyboard or mouse with the few fields worth showing.</summary>
public sealed class InputDeviceItem
{
    public InputDeviceItem(InputDeviceInfo device)
    {
        Device = device;
        Name = device.Name;
        Kind = device.Kind;
        Manufacturer = string.IsNullOrWhiteSpace(device.Manufacturer) ? "Unknown maker" : device.Manufacturer;
        Interface = DescribeInterface(device);
        Detail = string.Join(" · ", new[]
        {
            device.NumberOfButtons is { } b ? $"{b} buttons" : null,
            device.NumberOfFunctionKeys is { } f ? $"{f} function keys" : null,
            string.IsNullOrWhiteSpace(device.Layout) ? null : $"Layout {device.Layout}",
            string.IsNullOrWhiteSpace(device.Status) ? null : $"Status {device.Status}",
        }.Where(s => s is not null));
        IsKeyboard = device.Kind.Contains("keyboard", StringComparison.OrdinalIgnoreCase);
    }

    public InputDeviceInfo Device { get; }
    public string Name { get; }
    public string Kind { get; }
    public string Manufacturer { get; }
    public string Interface { get; }
    public string Detail { get; }
    public bool IsKeyboard { get; }

    private static string DescribeInterface(InputDeviceInfo device)
    {
        var id = (device.PnpDeviceId ?? device.DeviceId ?? string.Empty).ToUpperInvariant();
        if (id.StartsWith("BTHENUM", StringComparison.Ordinal) || id.Contains("BLUETOOTH", StringComparison.Ordinal))
        {
            return "Bluetooth";
        }

        if (id.StartsWith("HID", StringComparison.Ordinal) || id.StartsWith("USB", StringComparison.Ordinal))
        {
            return "USB / HID";
        }

        if (id.StartsWith("ACPI", StringComparison.Ordinal))
        {
            return "Built-in (laptop)";
        }

        return string.IsNullOrWhiteSpace(device.Description) ? "Unknown connection" : device.Description;
    }
}
