// TODO(audio): audio module — NAudio MMDeviceEnumerator, IPolicyConfig defaults, sessions, Audiosrv restart.
using PcChecker.Core.Common;

namespace PcChecker.Core.Audio;

/// <summary>Windows implementation of <see cref="IAudioService"/> on NAudio's Core Audio wrappers.</summary>
public sealed class WindowsAudioService : IAudioService
{
    private const string NotImplemented = "Audio management is not implemented yet.";

    public event EventHandler? DevicesChanged;

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AudioDevice>>([]);
    public Task<OperationResult> SetDefaultDeviceAsync(string deviceId, AudioFlow flow, bool forCommunications, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetVolumeAsync(string deviceId, float percent, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetMuteAsync(string deviceId, bool muted, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<IReadOnlyList<AudioSession>> GetSessionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AudioSession>>([]);
    public Task<OperationResult> SetSessionVolumeAsync(string sessionId, float percent, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetSessionMuteAsync(string sessionId, bool muted, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> RestartAudioServiceAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public OperationResult OpenSoundSettings() => ShellLauncher.Open("ms-settings:sound");
    public OperationResult OpenSoundControlPanel() => ShellLauncher.Open("control.exe", "mmsys.cpl");
    public OperationResult OpenAudioTroubleshooter() => ShellLauncher.Open("ms-settings:troubleshoot");
    public OperationResult OpenVolumeMixer() => ShellLauncher.Open("sndvol.exe");

    internal void RaiseForCompiler() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
