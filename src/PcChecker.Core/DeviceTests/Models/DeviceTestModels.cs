namespace PcChecker.Core.DeviceTests;

/// <summary>Gamepad buttons in one set, whichever API reported them (Windows.Gaming.Input, XInput or a generic controller's hat).</summary>
[Flags]
public enum GamepadButtons : uint
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    LeftShoulder = 1 << 4,
    RightShoulder = 1 << 5,
    View = 1 << 6,
    Menu = 1 << 7,
    LeftThumbstick = 1 << 8,
    RightThumbstick = 1 << 9,
    DPadUp = 1 << 10,
    DPadDown = 1 << 11,
    DPadLeft = 1 << 12,
    DPadRight = 1 << 13,
    Paddle1 = 1 << 14,
    Paddle2 = 1 << 15,
    Paddle3 = 1 << 16,
    Paddle4 = 1 << 17,
}

/// <summary>Where a controller's readings come from.</summary>
public enum GamepadSource
{
    /// <summary>Windows.Gaming.Input Gamepad: Xbox and Xbox-compatible controllers.</summary>
    Gamepad,

    /// <summary>Windows.Gaming.Input RawGameController: other controllers (PlayStation, Switch Pro, arcade sticks).</summary>
    RawController,

    /// <summary>XInput: the older Xbox API, used when Windows.Gaming.Input lists nothing.</summary>
    XInput,
}

/// <summary>Battery state as far as the controller reports it.</summary>
public enum GamepadBatteryState
{
    Unknown,
    Wired,
    Charging,
    Discharging,
    Idle,
}

/// <summary>One stick position: X right, Y up, both from -1 to 1.</summary>
public readonly record struct StickPoint(double X, double Y)
{
    public double Magnitude => Math.Sqrt(X * X + Y * Y);
}

/// <summary>A connected controller.</summary>
public sealed record GamepadInfo(
    string Id,
    string Name,
    GamepadSource Source,
    bool IsWireless,
    bool CanRumble,
    bool HasTriggerRumble,
    int? BatteryPercent,
    GamepadBatteryState Battery,
    int RawButtonCount);

/// <summary>One reading of a controller. <see cref="RawButtons"/> is filled for generic controllers only.</summary>
public sealed record GamepadState(
    StickPoint Left,
    StickPoint Right,
    double LeftTrigger,
    double RightTrigger,
    GamepadButtons Buttons,
    IReadOnlyList<bool> RawButtons);

/// <summary>Vibration strengths from 0 to 1 for the two body motors and the two trigger motors (Xbox One and later).</summary>
public readonly record struct RumbleRequest(double LeftMotor, double RightMotor, double LeftTrigger, double RightTrigger)
{
    public static RumbleRequest Off => default;

    public bool IsOff => LeftMotor <= 0 && RightMotor <= 0 && LeftTrigger <= 0 && RightTrigger <= 0;
}

/// <summary>Which speaker a test tone plays from.</summary>
public enum ToneChannel
{
    Left,
    Right,
    Both,
}

/// <summary>The speaker tests.</summary>
public enum SoundTest
{
    Left,
    Right,
    Both,
    Sweep,
}

/// <summary>PCM sample layout of an audio buffer.</summary>
public readonly record struct PcmFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    public int BlockAlign => Channels * (BitsPerSample / 8);
}

/// <summary>A short recording kept in memory only: mono samples from -1 to 1.</summary>
public sealed record RecordedAudio(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
}

/// <summary>One microphone level reading.</summary>
public sealed record MicrophoneLevel(double PeakDb, double RmsDb, double Peak, int ClippedSamples)
{
    public bool Clipped => ClippedSamples > 0;
}

/// <summary>Plain verdict for the microphone meter.</summary>
public enum MicrophoneHint
{
    Waiting,
    NoSignal,
    Quiet,
    Good,
    Clipping,
}
