using PcChecker.Core.Common;

namespace PcChecker.Core.Audio;

/// <summary>Sound devices, defaults, volumes, per-app sessions and the Windows sound tools.</summary>
public interface IAudioService
{
    Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetDefaultDeviceAsync(string deviceId, AudioFlow flow, bool forCommunications, CancellationToken cancellationToken);
    Task<OperationResult> SetVolumeAsync(string deviceId, float percent, CancellationToken cancellationToken);
    Task<OperationResult> SetMuteAsync(string deviceId, bool muted, CancellationToken cancellationToken);
    Task<IReadOnlyList<AudioSession>> GetSessionsAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetSessionVolumeAsync(string sessionId, float percent, CancellationToken cancellationToken);
    Task<OperationResult> SetSessionMuteAsync(string sessionId, bool muted, CancellationToken cancellationToken);
    Task<OperationResult> RestartAudioServiceAsync(CancellationToken cancellationToken);
    OperationResult OpenSoundSettings();
    OperationResult OpenSoundControlPanel();
    OperationResult OpenAudioTroubleshooter();
    OperationResult OpenVolumeMixer();
    event EventHandler? DevicesChanged;
}
