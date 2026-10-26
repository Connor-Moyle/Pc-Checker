using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.Core.Displays;

namespace PcChecker.App.ViewModels;

/// <summary>A resolution choice in the card's drop-down, grouped by aspect ratio.</summary>
public sealed record ResolutionOption(int Width, int Height, string Aspect)
{
    public string Text => $"{Width} × {Height}";
}

/// <summary>An orientation choice.</summary>
public sealed record OrientationOption(int Degrees, string Text);

/// <summary>
/// One display card on the Displays page: current facts plus the pending resolution / refresh choice, orientation,
/// HDR toggle and brightness/contrast sliders. Changes are forwarded to <see cref="DisplaysViewModel"/>.
/// </summary>
public sealed partial class DisplayCardViewModel : ObservableObject
{
    private static readonly OrientationOption[] AllOrientations =
    [
        new(0, "Landscape"), new(90, "Portrait"), new(180, "Landscape (flipped)"), new(270, "Portrait (flipped)"),
    ];

    private readonly DisplaysViewModel _owner;
    private readonly List<ResolutionOption> _resolutions = [];
    private bool _loading;
    private CancellationTokenSource? _brightnessDebounce;
    private CancellationTokenSource? _contrastDebounce;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingMode))]
    [NotifyCanExecuteChangedFor(nameof(ApplyModeCommand))]
    private ResolutionOption? _selectedResolution;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingMode))]
    [NotifyCanExecuteChangedFor(nameof(ApplyModeCommand))]
    private int _selectedRefreshHz;

    [ObservableProperty]
    private OrientationOption _selectedOrientation = AllOrientations[0];

    [ObservableProperty]
    private bool _hdrEnabled;

    [ObservableProperty]
    private int _brightness;

    [ObservableProperty]
    private int _contrast;

    public DisplayCardViewModel(DisplaysViewModel owner, DisplayInfo info, int number)
    {
        _owner = owner;
        Info = info;
        Number = number;
        ResolutionsView = new ListCollectionView(_resolutions);
        ResolutionsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ResolutionOption.Aspect)));
        Load(info);
    }

    public DisplayInfo Info { get; private set; }

    public int Number { get; }

    public string DeviceName => Info.DeviceName;

    public string Name => Info.FriendlyName;

    public string ShortDeviceName => DisplayProfiles.ShortDeviceName(Info.DeviceName);

    public bool IsPrimary => Info.IsPrimary;

    public bool IsNotPrimary => !Info.IsPrimary;

    /// <summary>"ASUS · DisplayPort · 27-inch · NVIDIA GeForce RTX 4070"</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Info.Manufacturer))
            {
                parts.Add(Info.Manufacturer);
            }

            if (!string.IsNullOrWhiteSpace(Info.ConnectionType))
            {
                parts.Add(Info.ConnectionType);
            }

            if (DisplayText.DiagonalText(Info.DiagonalInches) is { } size)
            {
                parts.Add(size);
            }

            if (!string.IsNullOrWhiteSpace(Info.AdapterName))
            {
                parts.Add(Info.AdapterName);
            }

            return parts.Count == 0 ? ShortDeviceName : string.Join(" · ", parts);
        }
    }

    public string CurrentModeText => DisplayText.ModeText(Info.Current);

    public string ScaleText => $"{Info.ScalePercent:0}% scale";

    public string PositionText => $"Position {Info.PositionX}, {Info.PositionY}";

    public string SerialText => string.IsNullOrWhiteSpace(Info.Serial) ? "Serial not reported" : "Serial " + Info.Serial;

    public bool HdrSupported => Info.HdrSupported;

    public string HdrStatusText => Info.HdrSupported
        ? (Info.HdrEnabled ? "HDR is on" : "HDR is available but off")
        : "This display does not report HDR support";

    public bool SupportsBrightness => Info.SupportsDdc || (Info.IsInternal && Info.Brightness is not null);

    public bool SupportsContrast => Info.SupportsDdc && Info.Contrast is not null;

    public string BrightnessUnavailableReason => Info.IsInternal
        ? "Windows did not expose a brightness control for this built-in panel."
        : "This monitor did not answer DDC/CI commands. Many monitors need DDC/CI switched on in their on-screen menu; until then use the monitor's own buttons.";

    public string PanelKindText => Info.IsInternal ? "Built-in panel" : "External monitor";

    public ICollectionView ResolutionsView { get; }

    public ObservableCollection<int> RefreshRates { get; } = [];

    public IReadOnlyList<OrientationOption> Orientations => AllOrientations;

    /// <summary>The mode the user picked in the drop-downs; equals the current one when nothing is pending.</summary>
    public DisplayMode PendingMode => SelectedResolution is null
        ? Info.Current
        : new DisplayMode(SelectedResolution.Width, SelectedResolution.Height, SelectedRefreshHz > 0 ? SelectedRefreshHz : Info.Current.RefreshHz, Info.Current.BitsPerPixel > 0 ? Info.Current.BitsPerPixel : 32);

    public bool HasPendingMode => !_loading && (PendingMode.Width != Info.Current.Width || PendingMode.Height != Info.Current.Height || PendingMode.RefreshHz != Info.Current.RefreshHz);

    /// <summary>Replaces the facts after a refresh and resets the pickers to the live values.</summary>
    public void Load(DisplayInfo info)
    {
        _loading = true;
        try
        {
            Info = info;
            _resolutions.Clear();
            foreach (var (w, h) in DisplayModeMath.Resolutions(info.Modes))
            {
                _resolutions.Add(new ResolutionOption(w, h, DisplayModeMath.AspectRatioText(w, h)));
            }

            ResolutionsView.Refresh();
            SelectedResolution = _resolutions.FirstOrDefault(r => r.Width == info.Current.Width && r.Height == info.Current.Height) ?? _resolutions.FirstOrDefault();
            RebuildRefreshRates(info.Current.RefreshHz);
            SelectedOrientation = AllOrientations.First(o => o.Degrees == DisplayText.Normalize(info.OrientationDegrees));
            HdrEnabled = info.HdrEnabled;
            Brightness = info.Brightness ?? 0;
            Contrast = info.Contrast ?? 0;
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(string.Empty);
        ApplyModeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedResolutionChanged(ResolutionOption? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        int preferred = value.Width == Info.Current.Width && value.Height == Info.Current.Height ? Info.Current.RefreshHz : SelectedRefreshHz;
        RebuildRefreshRates(preferred);
    }

    partial void OnSelectedOrientationChanged(OrientationOption value)
    {
        if (_loading || value.Degrees == DisplayText.Normalize(Info.OrientationDegrees))
        {
            return;
        }

        _ = _owner.ChangeOrientationAsync(this, value.Degrees);
    }

    partial void OnHdrEnabledChanged(bool value)
    {
        if (_loading || value == Info.HdrEnabled)
        {
            return;
        }

        _ = _owner.ChangeHdrAsync(this, value);
    }

    partial void OnBrightnessChanged(int value)
    {
        if (_loading || !SupportsBrightness)
        {
            return;
        }

        Debounce(ref _brightnessDebounce, ct => _owner.ChangeBrightnessAsync(this, value, ct));
    }

    partial void OnContrastChanged(int value)
    {
        if (_loading || !SupportsContrast)
        {
            return;
        }

        Debounce(ref _contrastDebounce, ct => _owner.ChangeContrastAsync(this, value, ct));
    }

    /// <summary>Puts the pickers back to the live mode (used when a change is cancelled or reverted).</summary>
    public void ResetPickers()
    {
        _loading = true;
        try
        {
            SelectedResolution = _resolutions.FirstOrDefault(r => r.Width == Info.Current.Width && r.Height == Info.Current.Height);
            RebuildRefreshRates(Info.Current.RefreshHz);
            SelectedOrientation = AllOrientations.First(o => o.Degrees == DisplayText.Normalize(Info.OrientationDegrees));
            HdrEnabled = Info.HdrEnabled;
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(HasPendingMode));
        ApplyModeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasPendingMode))]
    private Task ApplyModeAsync() => _owner.ApplyModeAsync(this);

    [RelayCommand]
    private Task SetPrimaryAsync() => _owner.SetPrimaryAsync(this);

    [RelayCommand]
    private Task IdentifyAsync() => _owner.IdentifyAsync(this);

    private void RebuildRefreshRates(int preferred)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            RefreshRates.Clear();
            if (SelectedResolution is not null)
            {
                foreach (int hz in DisplayModeMath.RefreshRatesFor(Info.Modes, SelectedResolution.Width, SelectedResolution.Height))
                {
                    RefreshRates.Add(hz);
                }
            }

            SelectedRefreshHz = RefreshRates.Contains(preferred) ? preferred : RefreshRates.FirstOrDefault();
        }
        finally
        {
            _loading = wasLoading;
        }

        OnPropertyChanged(nameof(HasPendingMode));
        ApplyModeCommand.NotifyCanExecuteChanged();
    }

    private static void Debounce(ref CancellationTokenSource? source, Func<CancellationToken, Task> work)
    {
        source?.Cancel();
        source?.Dispose();
        var cts = new CancellationTokenSource();
        source = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, cts.Token);
                await work(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // A newer slider value superseded this one.
            }
        });
    }
}
