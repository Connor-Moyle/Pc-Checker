using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.Core.Fans;
using PcChecker.Core.Sensors;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// One fan card on the Fans page: live RPM/percent plus the editing state for the mode the user is choosing
/// (manual slider or curve editor). Applying goes back through <see cref="FansViewModel"/>.
/// </summary>
public sealed partial class FanItemViewModel : ObservableObject
{
    private readonly FansViewModel _owner;
    private bool _syncingMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RpmText))]
    private double? _rpm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    [NotifyPropertyChangedFor(nameof(PercentValue))]
    private double? _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText))]
    [NotifyPropertyChangedFor(nameof(IsSoftwareControlled))]
    private FanMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualSelected))]
    [NotifyPropertyChangedFor(nameof(IsCurveSelected))]
    [NotifyPropertyChangedFor(nameof(IsAutoSelected))]
    private FanMode _selectedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualPercentText))]
    private double _manualPercent = 50;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurvePreviewText))]
    private string? _temperatureSourceId;

    [ObservableProperty]
    private double _hysteresisC = 2;

    [ObservableProperty]
    private double _rampStepPercent = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurvePreviewText))]
    [NotifyPropertyChangedFor(nameof(CurvePreviewPercent))]
    private double? _currentTemperature;

    [ObservableProperty]
    private string? _curveProblem;

    public FanItemViewModel(FansViewModel owner, FanDevice device)
    {
        _owner = owner;
        Id = device.Id;
        Name = device.Name;
        HardwareName = device.HardwareName;
        Hardware = device.Hardware;
        Controllable = device.Controllable;
        Capability = device.Capability;
        MinPercent = device.MinPercent;
        MaxPercent = device.MaxPercent;
        _rpm = device.Rpm;
        _percent = device.Percent;
        _mode = device.Mode;
        _selectedMode = device.Mode;
        _manualPercent = device.Percent ?? 50;
        CurvePoints = new ObservableCollection<FanCurvePoint>(FanCurveMath.Default(string.Empty, FanCurveMath.PresetBalanced).Points);
        CurvePoints.CollectionChanged += OnCurvePointsChanged;
    }

    public string Id { get; }
    public string Name { get; }
    public string HardwareName { get; }
    public HardwareKind Hardware { get; }
    public bool Controllable { get; }

    /// <summary>Controllable, control without RPM reading, or read-only.</summary>
    public FanCapability Capability { get; }

    /// <summary>One line on the card for a read-only fan or a control without RPM reading; null otherwise.</summary>
    public string? CapabilityNote => FanListRules.Describe(Capability, Hardware);
    public double MinPercent { get; }
    public double MaxPercent { get; }

    /// <summary>Editable curve points; the editor control mutates this collection directly.</summary>
    public ObservableCollection<FanCurvePoint> CurvePoints { get; }

    public IReadOnlyList<FanMode> Modes { get; } = Enum.GetValues<FanMode>();

    public string RpmText => Rpm is { } r ? r.ToString("0", System.Globalization.CultureInfo.CurrentCulture) : "—";
    public string PercentText => Percent is { } p ? p.ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " %" : Controllable ? "—" : "read-only";
    public double PercentValue => Percent ?? 0;
    public string ManualPercentText => ManualPercent.ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " %";
    public bool IsSoftwareControlled => Mode != FanMode.Auto;
    public SymbolRegular Icon => Hardware switch
    {
        HardwareKind.Gpu => SymbolRegular.DeveloperBoard24,
        HardwareKind.Cooler => SymbolRegular.Drop24,
        HardwareKind.Psu => SymbolRegular.Flash24,
        _ => SymbolRegular.ArrowSyncCircle24,
    };

    public string ModeText => Mode switch
    {
        FanMode.Manual => "Manual",
        FanMode.Curve => "Curve",
        _ => Controllable ? "Automatic" : "Read-only",
    };

    public string ModeHint => Mode switch
    {
        FanMode.Manual => "PC Checker holds this fan at a fixed speed.",
        FanMode.Curve => "PC Checker adjusts this fan every second from the temperature you chose.",
        _ => Controllable ? "The motherboard or graphics driver decides the speed." : "This fan's speed can be read but not changed on this PC.",
    };

    public bool IsAutoSelected
    {
        get => SelectedMode == FanMode.Auto;
        set => SetMode(value, FanMode.Auto);
    }

    public bool IsManualSelected
    {
        get => SelectedMode == FanMode.Manual;
        set => SetMode(value, FanMode.Manual);
    }

    public bool IsCurveSelected
    {
        get => SelectedMode == FanMode.Curve;
        set => SetMode(value, FanMode.Curve);
    }

    /// <summary>What the edited curve would output at the current temperature.</summary>
    public double? CurvePreviewPercent =>
        CurrentTemperature is { } t && CurvePoints.Count > 0 ? FanCurveMath.Interpolate(CurvePoints.ToList(), t) : null;

    public string CurvePreviewText => CurrentTemperature is { } t
        ? $"Now: {t:0} °C → {CurvePreviewPercent:0} %"
        : string.IsNullOrEmpty(TemperatureSourceId) ? "Choose a temperature to follow." : "Waiting for a temperature reading…";

    /// <summary>The curve as currently edited.</summary>
    public FanCurve BuildCurve()
    {
        return new FanCurve
        {
            TemperatureSensorId = TemperatureSourceId ?? string.Empty,
            Points = FanCurveMath.Normalize(CurvePoints),
            HysteresisC = (int)Math.Round(HysteresisC),
            RampStepPercent = (int)Math.Round(RampStepPercent),
        };
    }

    /// <summary>Loads a curve into the editor (from a profile or a preset) without applying it.</summary>
    public void LoadCurve(FanCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (!string.IsNullOrWhiteSpace(curve.TemperatureSensorId))
        {
            TemperatureSourceId = curve.TemperatureSensorId;
        }

        HysteresisC = curve.HysteresisC;
        RampStepPercent = curve.RampStepPercent;
        ReplacePoints(FanCurveMath.Normalize(curve.Points));
    }

    /// <summary>Pushes fresh readings from the service without disturbing what the user is editing.</summary>
    public void Update(FanDevice device)
    {
        Rpm = device.Rpm;
        Percent = device.Percent;
        if (Mode != device.Mode)
        {
            Mode = device.Mode;
            SyncSelectedMode();
        }
    }

    /// <summary>Puts the mode selector back on the mode the fan is really in (used when a selector change could not be applied).</summary>
    public void SyncSelectedMode()
    {
        _syncingMode = true;
        try
        {
            SelectedMode = Mode;
        }
        finally
        {
            _syncingMode = false;
        }
    }

    /// <summary>Reflects an applied profile assignment in the editing controls.</summary>
    public void LoadAssignment(FanAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        _syncingMode = true;
        try
        {
            SelectedMode = assignment.Mode;
        }
        finally
        {
            _syncingMode = false;
        }

        if (assignment.ManualPercent is { } manual)
        {
            ManualPercent = manual;
        }

        if (assignment.Curve is not null)
        {
            LoadCurve(assignment.Curve);
        }
    }

    /// <summary>What a saved profile should remember for this fan, based on the mode the user selected.</summary>
    public FanAssignment ToAssignment() => SelectedMode switch
    {
        FanMode.Manual => new FanAssignment { Mode = FanMode.Manual, ManualPercent = Math.Round(ManualPercent) },
        FanMode.Curve => new FanAssignment { Mode = FanMode.Curve, Curve = BuildCurve() },
        _ => new FanAssignment { Mode = FanMode.Auto },
    };

    [RelayCommand]
    private Task ApplyManualAsync() => _owner.ApplyManualAsync(this);

    [RelayCommand]
    private Task ApplyCurveAsync() => _owner.ApplyCurveAsync(this);

    [RelayCommand]
    private void UsePreset(string? preset)
    {
        var curve = FanCurveMath.Default(TemperatureSourceId ?? string.Empty, preset ?? FanCurveMath.PresetBalanced);
        ReplacePoints(curve.Points);
    }

    [RelayCommand]
    private void NudgeManual(string? delta)
    {
        if (double.TryParse(delta, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            ManualPercent = Math.Clamp(Math.Round(ManualPercent + d), 0, 100);
        }
    }

    private void SetMode(bool isChecked, FanMode mode)
    {
        if (!isChecked)
        {
            return;
        }

        SelectedMode = mode;
        if (!_syncingMode && mode == FanMode.Auto && Mode != FanMode.Auto)
        {
            // Picking "Automatic" applies right away; the owner resyncs the selector if it cannot run now or fails.
            _owner.SetAutoFromSelector(this);
        }
    }

    partial void OnSelectedModeChanged(FanMode value)
    {
        OnPropertyChanged(nameof(IsAutoSelected));
        OnPropertyChanged(nameof(IsManualSelected));
        OnPropertyChanged(nameof(IsCurveSelected));
    }

    partial void OnTemperatureSourceIdChanged(string? value) => _owner.RefreshTemperature(this);

    private void ReplacePoints(IEnumerable<FanCurvePoint> points)
    {
        CurvePoints.CollectionChanged -= OnCurvePointsChanged;
        try
        {
            CurvePoints.Clear();
            foreach (var p in points)
            {
                CurvePoints.Add(p);
            }
        }
        finally
        {
            CurvePoints.CollectionChanged += OnCurvePointsChanged;
        }

        OnCurvePointsChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void OnCurvePointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurvePreviewPercent));
        OnPropertyChanged(nameof(CurvePreviewText));
        CurveProblem = CurvePoints.Count < 2 ? "A curve needs at least two points." : null;
    }
}
