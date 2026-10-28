namespace PcChecker.Core.DeviceTests;

/// <summary>Maps the button, hat and battery values of Windows.Gaming.Input and XInput onto the app's own types.</summary>
public static class GamepadMapping
{
    // XInput XINPUT_GAMEPAD_* button bits.
    private static readonly (ushort Bit, GamepadButtons Button)[] XInputBits =
    [
        (0x0001, GamepadButtons.DPadUp), (0x0002, GamepadButtons.DPadDown), (0x0004, GamepadButtons.DPadLeft),
        (0x0008, GamepadButtons.DPadRight), (0x0010, GamepadButtons.Menu), (0x0020, GamepadButtons.View),
        (0x0040, GamepadButtons.LeftThumbstick), (0x0080, GamepadButtons.RightThumbstick),
        (0x0100, GamepadButtons.LeftShoulder), (0x0200, GamepadButtons.RightShoulder),
        (0x1000, GamepadButtons.A), (0x2000, GamepadButtons.B), (0x4000, GamepadButtons.X), (0x8000, GamepadButtons.Y),
    ];

    // Windows.Gaming.Input.GamepadButtons values.
    private static readonly (uint Bit, GamepadButtons Button)[] WgiBits =
    [
        (1, GamepadButtons.Menu), (2, GamepadButtons.View), (4, GamepadButtons.A), (8, GamepadButtons.B),
        (16, GamepadButtons.X), (32, GamepadButtons.Y), (64, GamepadButtons.DPadUp), (128, GamepadButtons.DPadDown),
        (256, GamepadButtons.DPadLeft), (512, GamepadButtons.DPadRight), (1024, GamepadButtons.LeftShoulder),
        (2048, GamepadButtons.RightShoulder), (4096, GamepadButtons.LeftThumbstick), (8192, GamepadButtons.RightThumbstick),
        (16384, GamepadButtons.Paddle1), (32768, GamepadButtons.Paddle2), (65536, GamepadButtons.Paddle3), (131072, GamepadButtons.Paddle4),
    ];

    /// <summary>The buttons shown on the test, in the order they are laid out.</summary>
    public static IReadOnlyList<GamepadButtons> StandardButtons { get; } =
    [
        GamepadButtons.A, GamepadButtons.B, GamepadButtons.X, GamepadButtons.Y,
        GamepadButtons.LeftShoulder, GamepadButtons.RightShoulder, GamepadButtons.View, GamepadButtons.Menu,
        GamepadButtons.LeftThumbstick, GamepadButtons.RightThumbstick,
        GamepadButtons.DPadUp, GamepadButtons.DPadDown, GamepadButtons.DPadLeft, GamepadButtons.DPadRight,
    ];

    private const GamepadButtons DPadButtons =
        GamepadButtons.DPadUp | GamepadButtons.DPadDown | GamepadButtons.DPadLeft | GamepadButtons.DPadRight;

    /// <summary>
    /// The named buttons to show for a controller. A generic controller (Windows.Gaming.Input RawGameController)
    /// only reports numbered buttons and a hat switch, which is read as the D-pad, so it gets only the D-pad here and
    /// its numbered buttons are shown separately.
    /// </summary>
    public static IReadOnlyList<GamepadButtons> NamedButtonsFor(GamepadSource source) =>
        source == GamepadSource.RawController
            ? StandardButtons.Where(b => (b & DPadButtons) != 0).ToArray()
            : StandardButtons;

    public static GamepadButtons FromXInputButtons(ushort mask)
    {
        var buttons = GamepadButtons.None;
        foreach (var (bit, button) in XInputBits)
        {
            if ((mask & bit) != 0)
            {
                buttons |= button;
            }
        }

        return buttons;
    }

    public static GamepadButtons FromWgiButtons(uint mask)
    {
        var buttons = GamepadButtons.None;
        foreach (var (bit, button) in WgiBits)
        {
            if ((mask & bit) != 0)
            {
                buttons |= button;
            }
        }

        return buttons;
    }

    /// <summary>
    /// A hat switch position (Windows.Gaming.Input.GameControllerSwitchPosition: 0 centre, 1 up, then clockwise in
    /// eighths to 8 up-left) as d-pad buttons.
    /// </summary>
    public static GamepadButtons FromSwitchPosition(int position) => position switch
    {
        1 => GamepadButtons.DPadUp,
        2 => GamepadButtons.DPadUp | GamepadButtons.DPadRight,
        3 => GamepadButtons.DPadRight,
        4 => GamepadButtons.DPadDown | GamepadButtons.DPadRight,
        5 => GamepadButtons.DPadDown,
        6 => GamepadButtons.DPadDown | GamepadButtons.DPadLeft,
        7 => GamepadButtons.DPadLeft,
        8 => GamepadButtons.DPadUp | GamepadButtons.DPadLeft,
        _ => GamepadButtons.None,
    };

    /// <summary>Battery percentage from a Windows battery report's remaining and full capacities (mWh).</summary>
    public static int? BatteryPercent(int? remainingMilliwattHours, int? fullMilliwattHours)
    {
        if (remainingMilliwattHours is not { } remaining || fullMilliwattHours is not { } full || full <= 0 || remaining < 0)
        {
            return null;
        }

        return (int)Math.Round(Math.Clamp(remaining * 100.0 / full, 0, 100));
    }

    /// <summary>
    /// XInput's battery information (type: 0 disconnected, 1 wired, 2 alkaline, 3 NiMH, 255 unknown; level: 0 empty
    /// to 3 full) as a rough percentage and state. XInput only knows four levels, so the percentage is approximate.
    /// </summary>
    public static (int? Percent, GamepadBatteryState State) FromXInputBattery(byte type, byte level)
    {
        if (type == 1)
        {
            return (null, GamepadBatteryState.Wired);
        }

        if (type is 2 or 3)
        {
            int percent = level switch { 0 => 5, 1 => 25, 2 => 60, _ => 100 };
            return (percent, GamepadBatteryState.Discharging);
        }

        return (null, GamepadBatteryState.Unknown);
    }

    /// <summary>
    /// Windows.Gaming.Input.Power.BatteryStatus (0 not present, 1 discharging, 2 idle, 3 charging) as the app's state.
    /// A controller without a battery (wired) reports "not present".
    /// </summary>
    public static GamepadBatteryState FromBatteryStatus(int status) => status switch
    {
        0 => GamepadBatteryState.Wired,
        1 => GamepadBatteryState.Discharging,
        2 => GamepadBatteryState.Idle,
        3 => GamepadBatteryState.Charging,
        _ => GamepadBatteryState.Unknown,
    };
}
