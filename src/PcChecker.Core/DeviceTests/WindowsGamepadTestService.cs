using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using Windows.Gaming.Input;
using WgiVibration = Windows.Gaming.Input.GamepadVibration;

namespace PcChecker.Core.DeviceTests;

/// <summary>
/// Controllers through Windows.Gaming.Input: <see cref="Gamepad"/> for Xbox-style controllers (sticks, triggers,
/// buttons, body and trigger vibration, battery) and <see cref="RawGameController"/> for every other controller
/// (generic axes, buttons and hat). When Windows.Gaming.Input lists no gamepad, XInput slots 1 to 4 are read instead.
/// Every call is guarded: a missing API or a controller that disappears mid-read gives "not connected", never a crash.
/// </summary>
public sealed class WindowsGamepadTestService : IGamepadTestService, IDisposable
{
    private const string GamepadPrefix = "wgi:";
    private const string RawPrefix = "raw:";
    private const string XInputPrefix = "xinput:";

    private readonly ILogger<WindowsGamepadTestService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Gamepad> _gamepads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RawGameController> _raws = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool[] Buttons, GameControllerSwitchPosition[] Switches, double[] Axes)> _rawBuffers = new(StringComparer.Ordinal);
    private bool _subscribed;
    private bool _xinputMissing;

    public WindowsGamepadTestService(ILogger<WindowsGamepadTestService> logger)
    {
        _logger = logger;
    }

    public event EventHandler? GamepadsChanged;

    public Task<IReadOnlyList<GamepadInfo>> GetGamepadsAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<IReadOnlyList<GamepadInfo>>([]);
        }

        return Task.Run<IReadOnlyList<GamepadInfo>>(async () =>
        {
            if (EnsureSubscribed())
            {
                // Windows.Gaming.Input fills its lists shortly after first use.
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }

            var list = new List<GamepadInfo>();
            lock (_gate)
            {
                _gamepads.Clear();
                _raws.Clear();
                _rawBuffers.Clear();
                ReadGamepads(list);
                ReadRawControllers(list);
            }

            if (!list.Any(g => g.Source == GamepadSource.Gamepad))
            {
                ReadXInput(list);
            }

            _logger.LogDebug("Found {Count} controllers for the controller test.", list.Count);
            return list;
        }, cancellationToken);
    }

    public GamepadState? ReadState(string id)
    {
        try
        {
            if (id.StartsWith(GamepadPrefix, StringComparison.Ordinal))
            {
                Gamepad? gamepad;
                lock (_gate)
                {
                    _gamepads.TryGetValue(id, out gamepad);
                }

                return gamepad is null ? null : ToState(gamepad.GetCurrentReading());
            }

            if (id.StartsWith(RawPrefix, StringComparison.Ordinal))
            {
                return ReadRaw(id);
            }

            if (id.StartsWith(XInputPrefix, StringComparison.Ordinal) && int.TryParse(id[XInputPrefix.Length..], out int slot))
            {
                return ReadXInputState(slot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Controller {Id} could not be read.", id);
        }

        return null;
    }

    public OperationResult SetRumble(string id, RumbleRequest rumble)
    {
        try
        {
            if (id.StartsWith(GamepadPrefix, StringComparison.Ordinal))
            {
                Gamepad? gamepad;
                lock (_gate)
                {
                    _gamepads.TryGetValue(id, out gamepad);
                }

                if (gamepad is null)
                {
                    return OperationResult.Fail("The controller is not connected any more.");
                }

                gamepad.Vibration = new WgiVibration
                {
                    LeftMotor = Clamp(rumble.LeftMotor),
                    RightMotor = Clamp(rumble.RightMotor),
                    LeftTrigger = Clamp(rumble.LeftTrigger),
                    RightTrigger = Clamp(rumble.RightTrigger),
                };
                return OperationResult.Ok();
            }

            if (id.StartsWith(XInputPrefix, StringComparison.Ordinal) && int.TryParse(id[XInputPrefix.Length..], out int slot))
            {
                var vibration = new XInputNative.Vibration
                {
                    LeftMotorSpeed = (ushort)Math.Round(Clamp(rumble.LeftMotor) * ushort.MaxValue),
                    RightMotorSpeed = (ushort)Math.Round(Clamp(rumble.RightMotor) * ushort.MaxValue),
                };
                return XInputNative.SetState(slot, ref vibration) == XInputNative.ErrorSuccess
                    ? OperationResult.Ok()
                    : OperationResult.Fail("The controller is not connected any more.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Vibration for {Id} failed.", id);
            return OperationResult.FromException(ex, "The controller did not accept the vibration request.");
        }

        return OperationResult.Fail("This controller's vibration cannot be controlled from Windows.");
    }

    public void StopAllRumble()
    {
        List<Gamepad> gamepads;
        lock (_gate)
        {
            gamepads = _gamepads.Values.ToList();
        }

        foreach (var gamepad in gamepads)
        {
            try
            {
                gamepad.Vibration = default;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stopping vibration failed.");
            }
        }

        if (_xinputMissing || !PlatformGuard.IsWindows)
        {
            return;
        }

        for (int slot = 0; slot < XInputNative.MaxControllers; slot++)
        {
            try
            {
                var off = default(XInputNative.Vibration);
                XInputNative.SetState(slot, ref off);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _xinputMissing = true;
                return;
            }
        }
    }

    public void Dispose()
    {
        StopAllRumble();
        if (!_subscribed)
        {
            return;
        }

        try
        {
            Gamepad.GamepadAdded -= OnGamepadChanged;
            Gamepad.GamepadRemoved -= OnGamepadChanged;
            RawGameController.RawGameControllerAdded -= OnRawChanged;
            RawGameController.RawGameControllerRemoved -= OnRawChanged;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Controller events could not be unsubscribed.");
        }
    }

    /// <summary>Subscribes to plug and unplug events once. Returns true on the first call.</summary>
    private bool EnsureSubscribed()
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                return false;
            }

            _subscribed = true;
        }

        try
        {
            Gamepad.GamepadAdded += OnGamepadChanged;
            Gamepad.GamepadRemoved += OnGamepadChanged;
            RawGameController.RawGameControllerAdded += OnRawChanged;
            RawGameController.RawGameControllerRemoved += OnRawChanged;
            _ = Gamepad.Gamepads.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows.Gaming.Input is not available; only XInput controllers can be tested.");
        }

        return true;
    }

    private void OnGamepadChanged(object? sender, Gamepad e) => GamepadsChanged?.Invoke(this, EventArgs.Empty);

    private void OnRawChanged(object? sender, RawGameController e) => GamepadsChanged?.Invoke(this, EventArgs.Empty);

    private void ReadGamepads(List<GamepadInfo> list)
    {
        IReadOnlyList<Gamepad> gamepads;
        try
        {
            gamepads = Gamepad.Gamepads;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gamepads could not be listed.");
            return;
        }

        int index = 0;
        foreach (var gamepad in gamepads)
        {
            index++;
            try
            {
                var raw = RawGameController.FromGameController(gamepad);
                string id = GamepadPrefix + (raw?.NonRoamableId ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                string name = string.IsNullOrWhiteSpace(raw?.DisplayName) ? "Xbox controller" : raw!.DisplayName;
                var (percent, state) = Battery(gamepad);
                _gamepads[id] = gamepad;
                list.Add(new GamepadInfo(id, name, GamepadSource.Gamepad, raw?.IsWireless ?? false, true, true, percent, state, 0));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A gamepad could not be described.");
            }
        }
    }

    private void ReadRawControllers(List<GamepadInfo> list)
    {
        IReadOnlyList<RawGameController> raws;
        try
        {
            raws = RawGameController.RawGameControllers;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Raw game controllers could not be listed.");
            return;
        }

        foreach (var raw in raws)
        {
            try
            {
                if (Gamepad.FromGameController(raw) is not null)
                {
                    continue;
                }

                string id = RawPrefix + raw.NonRoamableId;
                _raws[id] = raw;
                _rawBuffers[id] = (new bool[raw.ButtonCount], new GameControllerSwitchPosition[raw.SwitchCount], new double[raw.AxisCount]);
                var (percent, state) = Battery(raw);
                string name = string.IsNullOrWhiteSpace(raw.DisplayName) ? "Game controller" : raw.DisplayName;
                list.Add(new GamepadInfo(id, name, GamepadSource.RawController, raw.IsWireless, false, false, percent, state, raw.ButtonCount));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A game controller could not be described.");
            }
        }
    }

    private void ReadXInput(List<GamepadInfo> list)
    {
        if (_xinputMissing)
        {
            return;
        }

        for (int slot = 0; slot < XInputNative.MaxControllers; slot++)
        {
            try
            {
                if (XInputNative.GetState(slot, out _) != XInputNative.ErrorSuccess)
                {
                    continue;
                }

                int? percent = null;
                var state = GamepadBatteryState.Unknown;
                if (XInputNative.GetBatteryInformation(slot, XInputNative.BatteryDevTypeGamepad, out var battery) == XInputNative.ErrorSuccess)
                {
                    (percent, state) = GamepadMapping.FromXInputBattery(battery.BatteryType, battery.BatteryLevel);
                }

                bool wireless = state is GamepadBatteryState.Discharging;
                list.Add(new GamepadInfo(XInputPrefix + slot, $"Xbox controller (player {slot + 1})", GamepadSource.XInput, wireless, true, false, percent, state, 0));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _xinputMissing = true;
                _logger.LogDebug(ex, "XInput is not available.");
                return;
            }
        }
    }

    private GamepadState? ReadRaw(string id)
    {
        RawGameController? raw;
        (bool[] Buttons, GameControllerSwitchPosition[] Switches, double[] Axes) buffers;
        lock (_gate)
        {
            if (!_raws.TryGetValue(id, out raw) || !_rawBuffers.TryGetValue(id, out buffers))
            {
                return null;
            }
        }

        raw.GetCurrentReading(buffers.Buttons, buffers.Switches, buffers.Axes);
        var axes = buffers.Axes;
        double Axis(int i, bool invert) => i < axes.Length ? StickMath.FromRawAxis(axes[i], invert) : 0;
        double Trigger(int i) => i < axes.Length ? Math.Clamp(axes[i], 0, 1) : 0;

        var buttons = buffers.Switches.Length > 0 ? GamepadMapping.FromSwitchPosition((int)buffers.Switches[0]) : GamepadButtons.None;
        return new GamepadState(
            new StickPoint(Axis(0, false), Axis(1, true)),
            new StickPoint(Axis(2, false), Axis(3, true)),
            Trigger(4),
            Trigger(5),
            buttons,
            buffers.Buttons.ToArray());
    }

    private static GamepadState? ReadXInputState(int slot)
    {
        if (XInputNative.GetState(slot, out var state) != XInputNative.ErrorSuccess)
        {
            return null;
        }

        var pad = state.Gamepad;
        return new GamepadState(
            new StickPoint(StickMath.FromXInputAxis(pad.ThumbLX), StickMath.FromXInputAxis(pad.ThumbLY)),
            new StickPoint(StickMath.FromXInputAxis(pad.ThumbRX), StickMath.FromXInputAxis(pad.ThumbRY)),
            StickMath.FromXInputTrigger(pad.LeftTrigger),
            StickMath.FromXInputTrigger(pad.RightTrigger),
            GamepadMapping.FromXInputButtons(pad.Buttons),
            []);
    }

    private static GamepadState ToState(GamepadReading reading) => new(
        new StickPoint(reading.LeftThumbstickX, reading.LeftThumbstickY),
        new StickPoint(reading.RightThumbstickX, reading.RightThumbstickY),
        reading.LeftTrigger,
        reading.RightTrigger,
        GamepadMapping.FromWgiButtons((uint)reading.Buttons),
        []);

    private (int? Percent, GamepadBatteryState State) Battery(IGameControllerBatteryInfo controller)
    {
        try
        {
            var report = controller.TryGetBatteryReport();
            if (report is null)
            {
                return (null, GamepadBatteryState.Unknown);
            }

            var state = GamepadMapping.FromBatteryStatus((int)report.Status);
            return (state == GamepadBatteryState.Wired ? null : GamepadMapping.BatteryPercent(report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours), state);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Controller battery could not be read.");
            return (null, GamepadBatteryState.Unknown);
        }
    }

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
