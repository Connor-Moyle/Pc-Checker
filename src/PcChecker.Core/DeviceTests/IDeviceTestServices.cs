using PcChecker.Core.Common;

namespace PcChecker.Core.DeviceTests;

/// <summary>
/// Reads game controllers for the controller test (Windows.Gaming.Input with an XInput fallback) and runs their
/// vibration motors. Read-only apart from vibration, which stops by itself when the test asks it to.
/// </summary>
public interface IGamepadTestService
{
    /// <summary>The controllers connected now.</summary>
    Task<IReadOnlyList<GamepadInfo>> GetGamepadsAsync(CancellationToken cancellationToken);

    /// <summary>The current reading of controller <paramref name="id"/>, or null when it is gone. Cheap; safe to call 60 times a second.</summary>
    GamepadState? ReadState(string id);

    /// <summary>Sets the vibration motors of controller <paramref name="id"/>; <see cref="RumbleRequest.Off"/> stops them.</summary>
    OperationResult SetRumble(string id, RumbleRequest rumble);

    /// <summary>Stops vibration on every controller.</summary>
    void StopAllRumble();

    /// <summary>Raised when a controller is plugged in or removed.</summary>
    event EventHandler? GamepadsChanged;
}

/// <summary>
/// Plays the speaker test sounds on the default output and listens to the default microphone. A recording is kept in
/// memory only and nothing is saved.
/// </summary>
public interface ISoundTestService
{
    /// <summary>Plays one speaker test on the default output until it ends or is cancelled.</summary>
    Task<OperationResult> PlayTestAsync(SoundTest test, CancellationToken cancellationToken);

    /// <summary>Plays a recording on the default output.</summary>
    Task<OperationResult> PlayRecordingAsync(RecordedAudio audio, CancellationToken cancellationToken);

    /// <summary>
    /// Listens to the default microphone and reports its level. With <paramref name="recordFor"/> it stops after that
    /// long and returns the recording; without it, it runs until cancelled and returns no recording. When Windows blocks
    /// microphone access the result fails with an <see cref="UnauthorizedAccessException"/> as its error.
    /// </summary>
    Task<OperationResult<RecordedAudio>> ListenAsync(TimeSpan? recordFor, IProgress<MicrophoneLevel>? levels, CancellationToken cancellationToken);
}
