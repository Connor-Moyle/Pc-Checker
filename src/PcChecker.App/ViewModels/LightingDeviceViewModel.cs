using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Lighting;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// One RGB device card: mode, colour, speed, brightness and zones. Slider and picker changes are applied after a
/// short debounce so dragging does not flood the OpenRGB server.
/// </summary>
public sealed partial class LightingDeviceViewModel : ObservableObject
{
    private readonly ILightingService _service;
    private readonly Action<OperationResult> _report;
    private CancellationTokenSource? _debounce;
    private bool _syncing;

    [ObservableProperty]
    private RgbDevice _device;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsColor))]
    [NotifyPropertyChangedFor(nameof(SupportsSpeed))]
    [NotifyPropertyChangedFor(nameof(SupportsBrightness))]
    [NotifyPropertyChangedFor(nameof(ShowBrightnessNote))]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    private RgbMode? _selectedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBrush))]
    [NotifyPropertyChangedFor(nameof(ColorHex))]
    private Color _color;

    [ObservableProperty]
    private int _speed;

    [ObservableProperty]
    private int _brightness;

    [ObservableProperty]
    private bool _isPickerOpen;

    [ObservableProperty]
    private string? _lastResult;

    [ObservableProperty]
    private bool _isApplying;

    public LightingDeviceViewModel(RgbDevice device, ILightingService service, Action<OperationResult> report, int brightness = 100, int speed = 50)
    {
        _device = device;
        _service = service;
        _report = report;
        _syncing = true;
        _selectedMode = device.Modes.FirstOrDefault(m => m.Index == device.ActiveModeIndex) ?? device.Modes.FirstOrDefault();
        _color = ToMedia(device.Color ?? new RgbColor(0x4C, 0xC2, 0xFF));
        _brightness = brightness;
        _speed = speed;
        Zones = new ObservableCollection<LightingZoneViewModel>(device.Zones.Select(z => new LightingZoneViewModel(this, z, _color)));
        _syncing = false;
    }

    public int Index => Device.Index;
    public string Name => Device.Name;
    public string Vendor => Device.Vendor;
    public string TypeName => Device.Type;
    public string? Serial => Device.Serial;
    public int LedCount => Device.LedCount;
    public IReadOnlyList<RgbMode> Modes => Device.Modes;
    public ObservableCollection<LightingZoneViewModel> Zones { get; }
    /// <summary>Zones show when there are several, or when one can be resized (an addressable header).</summary>
    public bool HasZones => Zones.Count > 1 || Zones.Any(z => z.IsResizable);

    /// <summary>True when a resizable zone still has 0 LEDs (nothing lights there until its size is set).</summary>
    public bool NeedsZoneSize => Device.Zones.Any(z => z.NeedsSize);
    public bool IsPeripheral => OpenRgbDeviceTypes.IsPeripheral(Device.Type);

    public string Summary
    {
        get
        {
            string leds = LedCount == 1 ? "1 LED" : $"{LedCount} LEDs";
            string zones = Device.Zones.Count == 1 ? "1 zone" : $"{Device.Zones.Count} zones";
            return $"{Vendor} · {leds} · {zones}";
        }
    }

    public SymbolRegular Icon => IconFor(Device.Type);

    /// <summary>The icon for a device type (shared with the combined device list).</summary>
    public static SymbolRegular IconFor(string? type) => type switch
    {
        OpenRgbDeviceTypes.Motherboard => SymbolRegular.DeveloperBoard24,
        OpenRgbDeviceTypes.Ram => SymbolRegular.Memory16,
        OpenRgbDeviceTypes.Gpu => SymbolRegular.DeveloperBoardLightning20,
        OpenRgbDeviceTypes.Cooler => SymbolRegular.Temperature24,
        OpenRgbDeviceTypes.CaseFan => SymbolRegular.ArrowSyncCircle24,
        OpenRgbDeviceTypes.LedStrip or OpenRgbDeviceTypes.Light => SymbolRegular.LightbulbFilament24,
        OpenRgbDeviceTypes.Keyboard => SymbolRegular.Keyboard24,
        OpenRgbDeviceTypes.Mouse or OpenRgbDeviceTypes.MousePad => SymbolRegular.CursorHover24,
        OpenRgbDeviceTypes.Headset or OpenRgbDeviceTypes.HeadsetStand => SymbolRegular.Headset24,
        OpenRgbDeviceTypes.Gamepad => SymbolRegular.XboxController24,
        OpenRgbDeviceTypes.Speaker => SymbolRegular.Speaker224,
        RgbKnownDevices.LightingController => SymbolRegular.ArrowSyncCircle24,
        RgbKnownDevices.Microphone => SymbolRegular.Mic24,
        RgbKnownDevices.Monitor => SymbolRegular.Desktop24,
        RgbKnownDevices.Laptop => SymbolRegular.Laptop24,
        _ => SymbolRegular.DeveloperBoard24,
    };

    public bool SupportsColor => SelectedMode?.HasColor ?? false;
    public bool SupportsSpeed => SelectedMode?.HasSpeed ?? false;

    /// <summary>
    /// Brightness is applied by dimming the colour that is sent, so it works for every mode that takes a colour and
    /// for none that does not (OpenRGB.NET 3.1.1 cannot send a mode's own brightness value).
    /// </summary>
    public bool SupportsBrightness => LightingApplyPlanner.CanDim(SelectedMode);

    /// <summary>True for effects that advertise brightness we cannot set: the card states that instead of showing a dead slider.</summary>
    public bool ShowBrightnessNote => SelectedMode is { HasColor: false, HasBrightness: true };
    public string BrightnessNote => LightingApplyPlanner.BrightnessUnsupportedNote;

    public string ModeHint => SelectedMode switch
    {
        null => "This device reports no modes.",
        { HasColor: false, HasSpeed: false } => "This effect runs on its own: no colour or speed to set.",
        { HasColor: false } => "This effect picks its own colours; only the speed can be changed.",
        _ => "Pick a colour below. Changes apply as you adjust them.",
    };

    public Brush ColorBrush => new SolidColorBrush(Color);
    public string ColorHex => new RgbColor(Color.R, Color.G, Color.B).ToHex();

    /// <summary>Replaces the underlying device after a refresh without re-sending anything to the server.</summary>
    public void Sync(RgbDevice device)
    {
        _syncing = true;
        try
        {
            Device = device;
            SelectedMode = device.Modes.FirstOrDefault(m => m.Index == device.ActiveModeIndex) ?? SelectedMode;
            if (device.Color is { } c && LightingApplyPlanner.IsSteadyMode(SelectedMode))
            {
                // The server reports the colour actually on the LEDs, which is our base colour dimmed by the
                // brightness. Keep the base colour unless something else (OpenRGB's own window, "Sync all") changed it,
                // otherwise repeated brightness changes would dim an already dimmed colour. Animated effects report a
                // mid-animation colour, so only steady modes are trusted here.
                var expected = LightingApplyPlanner.EffectiveColor(new RgbColor(Color.R, Color.G, Color.B), Brightness);
                if (c != expected)
                {
                    Color = ToMedia(c);
                }
            }

            SyncZones(device);
            OnPropertyChanged(nameof(NeedsZoneSize));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Modes));
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Rebuilds the zone list when the device's zones changed (OpenRGB resized or re-detected them).</summary>
    private void SyncZones(RgbDevice device)
    {
        bool same = Zones.Count == device.Zones.Count
            && Zones.Zip(device.Zones).All(pair => pair.First.Zone == pair.Second);
        if (same)
        {
            return;
        }

        var previous = Zones.ToDictionary(z => z.Index, z => z.Color);
        Zones.Clear();
        foreach (var zone in device.Zones)
        {
            Zones.Add(new LightingZoneViewModel(this, zone, previous.TryGetValue(zone.Index, out var kept) ? kept : Color));
        }

        OnPropertyChanged(nameof(HasZones));
    }

    /// <summary>Adopts a colour and brightness that were already sent by a page-level action, without re-sending.</summary>
    public void Adopt(Color color, int brightness)
    {
        _syncing = true;
        try
        {
            Color = color;
            Brightness = brightness;
            foreach (var zone in Zones)
            {
                zone.SyncColor(color);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnSelectedModeChanged(RgbMode? value) => Schedule(ApplyKind.Mode);
    partial void OnColorChanged(Color value) => Schedule(ApplyKind.Color);
    partial void OnSpeedChanged(int value) => Schedule(ApplyKind.Mode);
    partial void OnBrightnessChanged(int value) => Schedule(SupportsColor ? ApplyKind.Color : ApplyKind.Mode);

    private ApplyKind _pending = ApplyKind.None;

    private void Schedule(ApplyKind kind)
    {
        if (_syncing)
        {
            return;
        }

        _pending = kind == ApplyKind.Mode || _pending == ApplyKind.Mode ? ApplyKind.Mode : ApplyKind.Color;
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _debounce, cts);
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

        _ = DebouncedApplyAsync(cts.Token);
    }

    private async Task DebouncedApplyAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(LightingApplyPlanner.DefaultDebounceMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var kind = _pending;
        _pending = ApplyKind.None;
        await ApplyCoreAsync(kind, token);
    }

    /// <summary>Explicit "Apply" button: sends mode, colour, speed and brightness right away.</summary>
    [RelayCommand]
    private Task ApplyAsync() => ApplyCoreAsync(ApplyKind.Mode, CancellationToken.None);

    [RelayCommand]
    private void TogglePicker() => IsPickerOpen = !IsPickerOpen;

    private async Task ApplyCoreAsync(ApplyKind kind, CancellationToken token)
    {
        if (kind == ApplyKind.None)
        {
            return;
        }

        IsApplying = true;
        try
        {
            OperationResult result;
            var rgb = new RgbColor(Color.R, Color.G, Color.B);
            if (kind == ApplyKind.Color && SelectedMode is { HasColor: true } && SelectedMode.Index == Device.ActiveModeIndex)
            {
                result = await _service.SetDeviceColorAsync(Index, LightingApplyPlanner.EffectiveColor(rgb, Brightness), token);
            }
            else if (SelectedMode is { } mode)
            {
                int? speed = mode.HasSpeed ? LightingApplyPlanner.SpeedFromPercent(mode, Speed) : null;
                result = await _service.SetModeAsync(Index, mode.Index, mode.HasColor ? rgb : null, speed, Brightness, token);
            }
            else
            {
                result = await _service.SetDeviceColorAsync(Index, LightingApplyPlanner.EffectiveColor(rgb, Brightness), token);
            }

            if (result.Success)
            {
                foreach (var zone in Zones)
                {
                    zone.SyncColor(Color);
                }
            }

            LastResult = result.Message;
            _report(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastResult = ex.Message;
            _report(OperationResult.FromException(ex));
        }
        finally
        {
            IsApplying = false;
        }
    }

    internal async Task ResizeZoneAsync(LightingZoneViewModel zone, int ledCount)
    {
        IsApplying = true;
        try
        {
            var result = await _service.ResizeZoneAsync(Index, zone.Index, ledCount, CancellationToken.None);
            LastResult = result.Message;
            _report(result);
        }
        catch (Exception ex)
        {
            LastResult = ex.Message;
            _report(OperationResult.FromException(ex));
        }
        finally
        {
            IsApplying = false;
        }
    }

    internal async Task ApplyZoneAsync(LightingZoneViewModel zone, CancellationToken token)
    {
        try
        {
            var rgb = new RgbColor(zone.Color.R, zone.Color.G, zone.Color.B);
            var result = await _service.SetZoneColorAsync(Index, zone.Index, LightingApplyPlanner.EffectiveColor(rgb, Brightness), token);
            LastResult = result.Message;
            _report(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastResult = ex.Message;
            _report(OperationResult.FromException(ex));
        }
    }

    internal static Color ToMedia(RgbColor c) => System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);

    private enum ApplyKind
    {
        None,
        Color,
        Mode,
    }
}

/// <summary>A zone (header, strip, key block) within a device with its own colour.</summary>
public sealed partial class LightingZoneViewModel : ObservableObject
{
    private readonly LightingDeviceViewModel _owner;
    private CancellationTokenSource? _debounce;
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBrush))]
    private Color _color;

    [ObservableProperty]
    private bool _isPickerOpen;

    /// <summary>The LED count typed for a resizable zone.</summary>
    [ObservableProperty]
    private double _newSize;

    public LightingZoneViewModel(LightingDeviceViewModel owner, RgbZone zone, Color initial)
    {
        _owner = owner;
        Zone = zone;
        _color = initial;
        _newSize = zone.LedCount;
    }

    public RgbZone Zone { get; }
    public int Index => Zone.Index;
    public string Name => Zone.Name;
    public bool IsResizable => Zone.IsResizable;
    public bool NeedsSize => Zone.NeedsSize;
    public double MinSize => Zone.LedsMin ?? 0;
    public double MaxSize => Zone.LedsMax ?? Zone.LedCount;

    public string Summary => Zone switch
    {
        { NeedsSize: true } => "No LEDs set: enter how many are connected",
        { IsResizable: true, LedCount: 1 } => $"1 LED (up to {Zone.LedsMax})",
        { IsResizable: true } => $"{Zone.LedCount} LEDs (up to {Zone.LedsMax})",
        { LedCount: 1 } => "1 LED",
        _ => $"{Zone.LedCount} LEDs",
    };

    /// <summary>Sends the typed LED count for this addressable zone to OpenRGB.</summary>
    [RelayCommand]
    private Task SetSizeAsync() => _owner.ResizeZoneAsync(this, (int)Math.Round(NewSize));
    public Brush ColorBrush => new SolidColorBrush(Color);

    public void SyncColor(Color color)
    {
        _syncing = true;
        try
        {
            Color = color;
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand]
    private void TogglePicker() => IsPickerOpen = !IsPickerOpen;

    partial void OnColorChanged(Color value)
    {
        if (_syncing)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _debounce, cts);
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

        _ = RunAsync(cts.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(LightingApplyPlanner.DefaultDebounceMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _owner.ApplyZoneAsync(this, token);
    }
}
