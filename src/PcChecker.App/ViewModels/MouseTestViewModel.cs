using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.DeviceTests;
using PcChecker.Core.Input;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The mouse test window: every button and the wheel light up, the time between clicks is measured (and a switch
/// that double-clicks by itself is spotted), and the polling rate is measured with the same Raw Input meter as the
/// Keyboard &amp; Mouse page.
/// </summary>
public sealed partial class MouseTestViewModel : ObservableObject
{
    private const int PollingSeconds = 5;
    private static readonly TimeSpan WheelFlash = TimeSpan.FromMilliseconds(160);

    private readonly IMousePollingMeter _meter;
    private readonly ILogger<MouseTestViewModel> _logger;
    private readonly ClickTimingAnalyzer _clicks = new();
    private readonly Dictionary<MouseTestInput, MouseInputItem> _byInput = [];
    private CancellationTokenSource? _pollingCts;

    [ObservableProperty] private string _clickText = Loc.T("Click inside the box. Double-click to see the time between your clicks.");
    [ObservableProperty] private string? _chatterText;
    [ObservableProperty] private int _doubleClickLimitMs = 500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotMeasuring))]
    [NotifyCanExecuteChangedFor(nameof(MeasurePollingRateCommand), nameof(StopPollingRateCommand))]
    private bool _isMeasuring;

    [ObservableProperty] private int _liveHz;
    [ObservableProperty] private string _pollingStatus = Loc.T("Press Measure, then keep the mouse moving in circles for 5 seconds.");
    [ObservableProperty] private string? _pollingResult;
    [ObservableProperty] private string? _smoothnessText;

    public MouseTestViewModel(IMousePollingMeter meter, ILogger<MouseTestViewModel> logger)
    {
        _meter = meter;
        _logger = logger;
        Add(MouseTestInput.Left, Loc.T("Left"));
        Add(MouseTestInput.Right, Loc.T("Right"));
        Add(MouseTestInput.Middle, Loc.T("Middle (wheel click)"));
        Add(MouseTestInput.Back, Loc.T("Back (side)"));
        Add(MouseTestInput.Forward, Loc.T("Forward (side)"));
        Add(MouseTestInput.WheelUp, Loc.T("Wheel up"));
        Add(MouseTestInput.WheelDown, Loc.T("Wheel down"));
        Add(MouseTestInput.TiltLeft, Loc.T("Wheel tilt left"));
        Add(MouseTestInput.TiltRight, Loc.T("Wheel tilt right"));
    }

    public ObservableCollection<MouseInputItem> Inputs { get; } = [];

    public bool IsNotMeasuring => !IsMeasuring;

    public bool HasChatter => ChatterText is not null;

    partial void OnChatterTextChanged(string? value) => OnPropertyChanged(nameof(HasChatter));

    public void Press(MouseTestInput input, double timestampMs)
    {
        if (!_byInput.TryGetValue(input, out var item))
        {
            return;
        }

        item.Press();
        var timing = _clicks.Register(input, timestampMs, DoubleClickLimitMs);
        if (input == MouseTestInput.Left && timing.IntervalMs is { } gap)
        {
            ClickText = timing.IsDoubleClick
                ? Loc.F("{0:0} ms between clicks: Windows sees a double-click (limit {1} ms).", gap, DoubleClickLimitMs)
                : Loc.F("{0:0} ms between clicks: too slow for a double-click (limit {1} ms).", gap, DoubleClickLimitMs);
        }

        if (timing.IsChatter && timing.IntervalMs is { } bounce)
        {
            ChatterText = Loc.F("The {0} button registered two clicks only {1:0} ms apart. If you pressed once, its switch is wearing out and double-clicks by itself.", item.Name.ToLowerInvariant(), bounce);
        }
    }

    public void Release(MouseTestInput input)
    {
        if (_byInput.TryGetValue(input, out var item))
        {
            item.IsActive = false;
        }
    }

    /// <summary>A wheel step or tilt: lights briefly, since the wheel has no "up" event.</summary>
    public async void Flash(MouseTestInput input)
    {
        if (!_byInput.TryGetValue(input, out var item))
        {
            return;
        }

        item.Press();
        int stamp = item.Count;
        await Task.Delay(WheelFlash);
        if (item.Count == stamp)
        {
            item.IsActive = false;
        }
    }

    [RelayCommand]
    private void Reset()
    {
        _clicks.Reset();
        foreach (var item in Inputs)
        {
            item.Reset();
        }

        ChatterText = null;
        ClickText = Loc.T("Click inside the box. Double-click to see the time between your clicks.");
    }

    [RelayCommand(CanExecute = nameof(IsNotMeasuring))]
    private async Task MeasurePollingRateAsync()
    {
        _pollingCts?.Dispose();
        _pollingCts = new CancellationTokenSource();
        IsMeasuring = true;
        LiveHz = 0;
        PollingResult = null;
        SmoothnessText = null;
        PollingStatus = Loc.T("Keep the mouse moving in circles…");
        var live = new List<int>();
        try
        {
            var progress = new Progress<int>(hz =>
            {
                LiveHz = hz;
                live.Add(hz);
            });
            int hz = await _meter.MeasureAsync(PollingSeconds, progress, _pollingCts.Token);
            var reading = PollingRateMath.Interpret(hz);
            PollingResult = hz > 0 ? Loc.F("{0} Hz (about {1})", hz, reading.Label) : null;
            PollingStatus = hz > 0 ? Loc.T(reading.Verdict) : Loc.T("No mouse reports were received. Keep the mouse moving the whole time and try again.");
            SmoothnessText = MouseTestMath.Smoothness(live) switch
            {
                SmoothnessVerdict.Steady => Loc.T("Movement looked smooth: the reports came at an even rate."),
                SmoothnessVerdict.Uneven => Loc.T("The report rate jumped around. Keep moving steadily; if it stays uneven, try another USB port (not a hub), move a wireless receiver closer, or close busy programs."),
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            PollingStatus = Loc.T("Measurement stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Polling rate measurement failed in the mouse test.");
            PollingStatus = Loc.T("The measurement could not run.") + " " + ex.Message;
        }
        finally
        {
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

    public void Stop() => StopPollingRate();

    private void Add(MouseTestInput input, string name)
    {
        var item = new MouseInputItem(input, name);
        Inputs.Add(item);
        _byInput[input] = item;
    }
}

/// <summary>One mouse button or wheel direction on the test: lit while pressed, ticked once used.</summary>
public sealed partial class MouseInputItem : ObservableObject
{
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _wasUsed;
    [ObservableProperty] private int _count;

    public MouseInputItem(MouseTestInput input, string name)
    {
        Input = input;
        Name = name;
    }

    public MouseTestInput Input { get; }

    public string Name { get; }

    public string CountText => Count == 0 ? "—" : Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    public void Press()
    {
        IsActive = true;
        WasUsed = true;
        Count++;
    }

    public void Reset()
    {
        IsActive = false;
        WasUsed = false;
        Count = 0;
    }
}
