using System.Diagnostics;
using PcChecker.Core.Common;

namespace PcChecker.Core.DeviceTests;

/// <summary>
/// Demo controller: an Xbox Wireless Controller at 72 % battery whose left stick rests slightly off centre (light
/// drift). In a 14-second loop both sticks rest for 6 seconds, then spin round their edge for 8, while the triggers
/// pulse and the buttons light up one after another. Pure .NET.
/// </summary>
public sealed class DemoGamepadTestService : IGamepadTestService
{
    public const string DemoId = "demo:xbox";
    public const double CycleSeconds = 14;
    public const double RestSeconds = 6;

    /// <summary>Where the demo's left stick rests: about 7 % off centre, inside a normal dead zone.</summary>
    public static readonly StickPoint LeftRest = new(0.06, -0.04);

    private static readonly GamepadButtons[] ButtonCycle =
    [
        GamepadButtons.A, GamepadButtons.B, GamepadButtons.X, GamepadButtons.Y, GamepadButtons.LeftShoulder,
        GamepadButtons.RightShoulder, GamepadButtons.DPadUp, GamepadButtons.DPadRight, GamepadButtons.DPadDown,
        GamepadButtons.DPadLeft, GamepadButtons.Menu, GamepadButtons.View,
    ];

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = DemoData.CreateRandom(360);

    public event EventHandler? GamepadsChanged
    {
        add { }
        remove { }
    }

    public async Task<IReadOnlyList<GamepadInfo>> GetGamepadsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        return [new GamepadInfo(DemoId, "Xbox Wireless Controller", GamepadSource.Gamepad, true, true, true, 72, GamepadBatteryState.Discharging, 0)];
    }

    public GamepadState? ReadState(string id)
    {
        if (id != DemoId)
        {
            return null;
        }

        double t = _clock.Elapsed.TotalSeconds;
        return StateAt(t, Noise);
    }

    public OperationResult SetRumble(string id, RumbleRequest rumble) =>
        id == DemoId ? OperationResult.Ok(rumble.IsOff ? "Vibration stopped (demo)." : "Vibrating (demo).") : OperationResult.Fail("The controller is not connected.");

    public void StopAllRumble()
    {
    }

    /// <summary>The simulated reading <paramref name="seconds"/> into the loop; <paramref name="noise"/> adds small jitter.</summary>
    public static GamepadState StateAt(double seconds, Func<double>? noise = null)
    {
        noise ??= () => 0;
        double phase = seconds % CycleSeconds;
        StickPoint left;
        StickPoint right;
        if (phase < RestSeconds)
        {
            left = new StickPoint(LeftRest.X + noise(), LeftRest.Y + noise());
            right = new StickPoint(0.01 + noise(), noise());
        }
        else
        {
            // One turn every two seconds; the left stick's edge bulges a little at the diagonals like a worn gate.
            double angle = (phase - RestSeconds) * Math.PI;
            double leftRadius = 0.96 + 0.06 * Math.Abs(Math.Sin(2 * angle));
            left = new StickPoint(Math.Clamp(leftRadius * Math.Cos(angle), -1, 1), Math.Clamp(leftRadius * Math.Sin(angle), -1, 1));
            right = new StickPoint(0.99 * Math.Cos(-angle), 0.99 * Math.Sin(-angle));
        }

        double leftTrigger = Math.Max(0, Math.Sin(seconds * 1.6));
        double rightTrigger = Math.Max(0, Math.Sin(seconds * 1.6 + Math.PI));
        int step = (int)(seconds * 2.5);
        var buttons = step % 2 == 0 ? ButtonCycle[step / 2 % ButtonCycle.Length] : GamepadButtons.None;
        return new GamepadState(left, right, leftTrigger, rightTrigger, buttons, []);
    }

    private double Noise() => (_random.NextDouble() - 0.5) * 0.016;
}
