using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.DeviceTests;

namespace PcChecker.App.ViewModels;

/// <summary>The controller test: live sticks, triggers and buttons, the drift and roundness checks, and vibration.</summary>
public sealed partial class DeviceTestsViewModel
{
    private const int TrailLength = 90;
    private const int RoundnessTrailLength = 1200;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan DriftDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RoundnessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RumbleDuration = TimeSpan.FromSeconds(1);
    private const double RumbleStrength = 0.7;

    private readonly List<Point> _leftTrail = [];
    private readonly List<Point> _rightTrail = [];
    private readonly List<StickPoint> _leftSamples = [];
    private readonly List<StickPoint> _rightSamples = [];
    private DispatcherTimer? _pollTimer;
    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _rumbleCts;
    private bool _collectingDrift;
    private bool _collectingRoundness;
    private bool _reloadQueued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasControllers), nameof(HasNoControllers))]
    private ObservableCollection<ControllerChoice> _controllers = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDriftCommand), nameof(CheckRoundnessCommand), nameof(RumbleCommand))]
    private ControllerChoice? _selectedController;

    [ObservableProperty] private bool _isControllerTestOpen;
    [ObservableProperty] private Point _leftStick;
    [ObservableProperty] private Point _rightStick;
    [ObservableProperty] private IReadOnlyList<Point> _leftTrailPoints = [];
    [ObservableProperty] private IReadOnlyList<Point> _rightTrailPoints = [];
    [ObservableProperty] private bool _leftStickPressed;
    [ObservableProperty] private bool _rightStickPressed;
    [ObservableProperty] private string _leftStickText = string.Empty;
    [ObservableProperty] private string _rightStickText = string.Empty;
    [ObservableProperty] private double _leftTriggerPercent;
    [ObservableProperty] private double _rightTriggerPercent;
    [ObservableProperty] private string _controllerStatus = string.Empty;
    [ObservableProperty] private string _batteryText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotChecking))]
    [NotifyCanExecuteChangedFor(nameof(CheckDriftCommand), nameof(CheckRoundnessCommand), nameof(StopCheckCommand))]
    private bool _isChecking;

    [ObservableProperty] private string _checkStatus = Loc.T("Let go of both sticks and press Check drift, or spin them for the roundness check.");
    [ObservableProperty] private string? _leftDriftText;
    [ObservableProperty] private string? _rightDriftText;
    [ObservableProperty] private string? _leftRoundnessText;
    [ObservableProperty] private string? _rightRoundnessText;
    [ObservableProperty] private bool _leftDriftWarning;
    [ObservableProperty] private bool _rightDriftWarning;
    [ObservableProperty] private string _rumbleStatus = string.Empty;

    public ObservableCollection<ButtonLight> ButtonLights { get; }

    public bool HasControllers => Controllers.Count > 0;

    public bool HasNoControllers => Controllers.Count == 0;

    public bool IsNotChecking => !IsChecking;

    public bool CanRumble => SelectedController?.Info.CanRumble == true;

    public bool HasTriggerRumble => SelectedController?.Info.HasTriggerRumble == true;

    /// <summary>The dead zone drawn on the stick plots and used by the drift check.</summary>
    public double Deadzone => StickMath.DefaultDeadzone;

    partial void OnSelectedControllerChanged(ControllerChoice? value)
    {
        StopRumble();
        CancelCheck();
        ResetControllerReadings();
        RebuildButtonLights(value?.Info);
        BatteryText = value is null ? string.Empty : value.BatteryText;
        ControllerStatus = value is null ? string.Empty : value.SourceText;
        OnPropertyChanged(nameof(CanRumble));
        OnPropertyChanged(nameof(HasTriggerRumble));
    }

    partial void OnIsControllerTestOpenChanged(bool value)
    {
        if (!value)
        {
            StopRumble();
            CancelCheck();
        }

        UpdateControllerPolling();
    }

    [RelayCommand]
    private void OpenControllerTest()
    {
        IsControllerTestOpen = true;
        SelectedController ??= Controllers.FirstOrDefault();
    }

    [RelayCommand]
    private void CloseControllerTest() => IsControllerTestOpen = false;

    private void ShowControllers(IReadOnlyList<GamepadInfo> gamepads)
    {
        string? previous = SelectedController?.Info.Id;
        Controllers = new ObservableCollection<ControllerChoice>(gamepads.Select(g => new ControllerChoice(g)));
        ControllerCount = Controllers.Count;
        ControllerSummary = Controllers.Count switch
        {
            0 => Loc.T("No controller found. Plug one in or pair it over Bluetooth."),
            1 => Controllers[0].Summary,
            _ => Loc.F("{0} controllers connected", Controllers.Count),
        };
        SelectedController = Controllers.FirstOrDefault(c => c.Info.Id == previous) ?? Controllers.FirstOrDefault();
    }

    private void OnGamepadsChanged(object? sender, EventArgs e)
    {
        DispatcherHelper.PostToUi(async () =>
        {
            if (_reloadQueued)
            {
                return;
            }

            _reloadQueued = true;
            try
            {
                await Task.Delay(300);
                var list = await _gamepads.GetGamepadsAsync(CancellationToken.None);
                ShowControllers(list);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Controller list could not be refreshed.");
            }
            finally
            {
                _reloadQueued = false;
            }
        });
    }

    private void UpdateControllerPolling()
    {
        bool run = _pageVisible && IsControllerTestOpen;
        if (run && _pollTimer is null)
        {
            _pollTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = PollInterval };
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();
        }
        else if (!run && _pollTimer is not null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= OnPollTick;
            _pollTimer = null;
        }
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (SelectedController is not { } controller)
        {
            return;
        }

        GamepadState? state;
        try
        {
            state = _gamepads.ReadState(controller.Info.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Controller read failed.");
            state = null;
        }

        if (state is null)
        {
            ControllerStatus = Loc.T("Not responding. Check that the controller is on and connected.");
            return;
        }

        ControllerStatus = controller.SourceText;
        ShowState(state);
    }

    private void ShowState(GamepadState state)
    {
        LeftStick = new Point(state.Left.X, state.Left.Y);
        RightStick = new Point(state.Right.X, state.Right.Y);
        int keep = _collectingRoundness ? RoundnessTrailLength : TrailLength;
        LeftTrailPoints = Push(_leftTrail, LeftStick, keep);
        RightTrailPoints = Push(_rightTrail, RightStick, keep);
        LeftStickText = Loc.F("X {0:+0.00;-0.00;0.00}  Y {1:+0.00;-0.00;0.00}", state.Left.X, state.Left.Y);
        RightStickText = Loc.F("X {0:+0.00;-0.00;0.00}  Y {1:+0.00;-0.00;0.00}", state.Right.X, state.Right.Y);
        LeftTriggerPercent = Math.Round(state.LeftTrigger * 100);
        RightTriggerPercent = Math.Round(state.RightTrigger * 100);
        LeftStickPressed = state.Buttons.HasFlag(GamepadButtons.LeftThumbstick);
        RightStickPressed = state.Buttons.HasFlag(GamepadButtons.RightThumbstick);

        foreach (var light in ButtonLights)
        {
            light.Update(state);
        }

        if (_collectingDrift || _collectingRoundness)
        {
            _leftSamples.Add(state.Left);
            _rightSamples.Add(state.Right);
        }
    }

    private static IReadOnlyList<Point> Push(List<Point> trail, Point point, int keep)
    {
        trail.Add(point);
        if (trail.Count > keep)
        {
            trail.RemoveRange(0, trail.Count - keep);
        }

        return trail.ToArray();
    }

    private void ResetControllerReadings()
    {
        _leftTrail.Clear();
        _rightTrail.Clear();
        LeftTrailPoints = [];
        RightTrailPoints = [];
        LeftStick = default;
        RightStick = default;
        LeftTriggerPercent = 0;
        RightTriggerPercent = 0;
        LeftDriftText = RightDriftText = LeftRoundnessText = RightRoundnessText = null;
        LeftDriftWarning = RightDriftWarning = false;
    }

    private void RebuildButtonLights(GamepadInfo? info)
    {
        ButtonLights.Clear();
        if (info is null)
        {
            return;
        }

        foreach (var button in GamepadMapping.StandardButtons)
        {
            ButtonLights.Add(ButtonLight.ForButton(button));
        }

        for (int i = 0; i < info.RawButtonCount; i++)
        {
            ButtonLights.Add(ButtonLight.ForRawButton(i));
        }
    }

    // ----- Drift and roundness -----

    private bool CanCheck() => SelectedController is not null && !IsChecking;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckDriftAsync()
    {
        if (!BeginCheck(out var ct))
        {
            return;
        }

        LeftDriftText = RightDriftText = null;
        try
        {
            CheckStatus = Loc.T("Hands off the sticks… checking for 2 seconds.");
            _collectingDrift = true;
            await Task.Delay(DriftDuration, ct);
            _collectingDrift = false;
            var left = StickMath.AnalyzeDrift(_leftSamples);
            var right = StickMath.AnalyzeDrift(_rightSamples);
            LeftDriftText = DescribeDrift(left);
            RightDriftText = DescribeDrift(right);
            LeftDriftWarning = left.Verdict == DriftVerdict.Drift;
            RightDriftWarning = right.Verdict == DriftVerdict.Drift;
            CheckStatus = left.Verdict == DriftVerdict.Moved || right.Verdict == DriftVerdict.Moved
                ? Loc.T("A stick moved during the check. Put the controller down and try again.")
                : Loc.T("Drift check finished.");
        }
        catch (OperationCanceledException)
        {
            CheckStatus = Loc.T("Check stopped.");
        }
        finally
        {
            EndCheck();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckRoundnessAsync()
    {
        if (!BeginCheck(out var ct))
        {
            return;
        }

        LeftRoundnessText = RightRoundnessText = null;
        _leftTrail.Clear();
        _rightTrail.Clear();
        try
        {
            CheckStatus = Loc.T("Push each stick all the way out and turn it slowly round the edge two or three times.");
            _collectingRoundness = true;
            var started = DateTime.UtcNow;
            CircularityResult left;
            CircularityResult right;
            while (true)
            {
                await Task.Delay(250, ct);
                left = StickMath.AnalyzeCircularity(_leftSamples);
                right = StickMath.AnalyzeCircularity(_rightSamples);
                LeftRoundnessText = DescribeRoundness(left);
                RightRoundnessText = DescribeRoundness(right);
                bool done = left.Verdict != CircularityVerdict.KeepSpinning && right.Verdict != CircularityVerdict.KeepSpinning;
                if (done || DateTime.UtcNow - started > RoundnessTimeout)
                {
                    break;
                }
            }

            CheckStatus = left.Verdict == CircularityVerdict.KeepSpinning || right.Verdict == CircularityVerdict.KeepSpinning
                ? Loc.T("Time is up. A stick that never reached the edge all the way round shows how far it got.")
                : Loc.T("Roundness check finished. Most controllers read between 5 and 15 %.");
        }
        catch (OperationCanceledException)
        {
            CheckStatus = Loc.T("Check stopped.");
        }
        finally
        {
            EndCheck();
        }
    }

    [RelayCommand(CanExecute = nameof(IsChecking))]
    private void StopCheck() => CancelCheck();

    private bool BeginCheck(out CancellationToken ct)
    {
        ct = default;
        if (IsChecking || SelectedController is null)
        {
            return false;
        }

        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource();
        ct = _checkCts.Token;
        _leftSamples.Clear();
        _rightSamples.Clear();
        IsChecking = true;
        return true;
    }

    private void EndCheck()
    {
        _collectingDrift = false;
        _collectingRoundness = false;
        IsChecking = false;
    }

    private void CancelCheck()
    {
        try
        {
            _checkCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string DescribeDrift(DriftResult result) => result.Verdict switch
    {
        DriftVerdict.Centred => Loc.F("Centred ({0:0.0} % off the middle). No drift.", result.OffsetPercent),
        DriftVerdict.SlightDrift => Loc.F("Slight drift: rests {0:0.0} % off the middle. Games' dead zones usually hide this.", result.OffsetPercent),
        DriftVerdict.Drift => Loc.F("Drift: rests {0:0.0} % off the middle, more than a typical dead zone. Games may move on their own; cleaning or repairing the stick helps.", result.OffsetPercent),
        DriftVerdict.Moved => Loc.T("Moved during the check, so drift could not be judged."),
        _ => Loc.T("Not enough readings. Is the controller still connected?"),
    };

    private static string DescribeRoundness(CircularityResult result) => result.Verdict switch
    {
        CircularityVerdict.Good => Loc.F("Round: average error {0:0.0} %. Good.", result.AverageErrorPercent),
        CircularityVerdict.Fair => Loc.F("Average error {0:0.0} %. Normal for many controllers.", result.AverageErrorPercent),
        CircularityVerdict.Poor => Loc.F("Average error {0:0.0} %. The stick may be worn or not reach its full range.", result.AverageErrorPercent),
        _ => Loc.F("Keep turning: {0:0} % of the edge covered.", result.CoveragePercent),
    };

    // ----- Vibration -----

    private bool CanRumbleNow() => SelectedController?.Info.CanRumble == true;

    /// <summary>Runs one motor for a second: "left", "right", "left-trigger" or "right-trigger".</summary>
    [RelayCommand(CanExecute = nameof(CanRumbleNow))]
    private async Task RumbleAsync(string? motor)
    {
        if (SelectedController is not { } controller)
        {
            return;
        }

        var request = motor switch
        {
            "left" => new RumbleRequest(RumbleStrength, 0, 0, 0),
            "right" => new RumbleRequest(0, RumbleStrength, 0, 0),
            "left-trigger" => new RumbleRequest(0, 0, RumbleStrength, 0),
            "right-trigger" => new RumbleRequest(0, 0, 0, RumbleStrength),
            _ => new RumbleRequest(RumbleStrength, RumbleStrength, 0, 0),
        };

        _rumbleCts?.Cancel();
        _rumbleCts?.Dispose();
        _rumbleCts = new CancellationTokenSource();
        var ct = _rumbleCts.Token;
        var result = _gamepads.SetRumble(controller.Info.Id, request);
        if (!result.Success)
        {
            RumbleStatus = Loc.T(result.Message);
            return;
        }

        RumbleStatus = Loc.T("Vibrating for one second. You should feel it in the matching side or trigger.");
        try
        {
            await Task.Delay(RumbleDuration, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _gamepads.SetRumble(controller.Info.Id, RumbleRequest.Off);
        RumbleStatus = Loc.T("Vibration finished.");
    }

    [RelayCommand]
    private void StopRumble()
    {
        try
        {
            _rumbleCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _gamepads.StopAllRumble();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping vibration failed.");
        }
    }
}

/// <summary>A controller in the picker, with its one-line summary.</summary>
public sealed class ControllerChoice
{
    public ControllerChoice(GamepadInfo info)
    {
        Info = info;
        SourceText = info.Source switch
        {
            GamepadSource.Gamepad => Loc.T("Connected (Xbox-style controller)"),
            GamepadSource.RawController => Loc.T("Connected (generic controller: stick and trigger axes may be mapped differently)"),
            _ => Loc.T("Connected through XInput"),
        };
        BatteryText = info.Battery switch
        {
            GamepadBatteryState.Wired => Loc.T("Wired"),
            GamepadBatteryState.Charging when info.BatteryPercent is { } p => Loc.F("Battery {0} %, charging", p),
            GamepadBatteryState.Charging => Loc.T("Charging"),
            _ when info.BatteryPercent is { } p => Loc.F("Battery {0} %", p),
            _ => info.IsWireless ? Loc.T("Battery level not reported") : string.Empty,
        };
        Summary = string.IsNullOrEmpty(BatteryText) ? info.Name : info.Name + " · " + BatteryText;
    }

    public GamepadInfo Info { get; }

    public string Name => Info.Name;

    public string SourceText { get; }

    public string BatteryText { get; }

    public string Summary { get; }

    public override string ToString() => Info.Name;
}

/// <summary>One button on the controller test: lit while held, marked once it has been pressed.</summary>
public sealed partial class ButtonLight : ObservableObject
{
    private readonly GamepadButtons _button;
    private readonly int _rawIndex = -1;

    [ObservableProperty] private bool _isPressed;
    [ObservableProperty] private bool _wasPressed;

    private ButtonLight(string label, GamepadButtons button, int rawIndex)
    {
        Label = label;
        _button = button;
        _rawIndex = rawIndex;
    }

    public string Label { get; }

    public static ButtonLight ForButton(GamepadButtons button) => new(button switch
    {
        GamepadButtons.LeftShoulder => "LB",
        GamepadButtons.RightShoulder => "RB",
        GamepadButtons.View => Loc.T("View"),
        GamepadButtons.Menu => Loc.T("Menu"),
        GamepadButtons.LeftThumbstick => Loc.T("Left stick"),
        GamepadButtons.RightThumbstick => Loc.T("Right stick"),
        GamepadButtons.DPadUp => Loc.T("D-pad up"),
        GamepadButtons.DPadDown => Loc.T("D-pad down"),
        GamepadButtons.DPadLeft => Loc.T("D-pad left"),
        GamepadButtons.DPadRight => Loc.T("D-pad right"),
        _ => button.ToString(),
    }, button, -1);

    public static ButtonLight ForRawButton(int index) => new(Loc.F("Button {0}", index + 1), GamepadButtons.None, index);

    public void Update(GamepadState state)
    {
        bool pressed = _rawIndex >= 0
            ? _rawIndex < state.RawButtons.Count && state.RawButtons[_rawIndex]
            : (state.Buttons & _button) != 0;
        if (pressed != IsPressed)
        {
            IsPressed = pressed;
        }

        if (pressed && !WasPressed)
        {
            WasPressed = true;
        }
    }
}
