using PcChecker.Core.Common;

namespace PcChecker.Core.Audio;

/// <summary>Demo audio: speakers, a USB headset, monitor audio, two microphones and a few app sessions.</summary>
public sealed class DemoAudioService : IAudioService, IAudioLevelMeter
{
    private readonly object _gate = new();
    private int _meterTick;
    private readonly List<AudioDevice> _devices =
    [
        new("{0.0.0.00000000}.{demo-speakers}", "Speakers", "Realtek(R) Audio", AudioFlow.Render, true, false, "Active", 42, false, "24 bit, 48000 Hz", 0.12f),
        new("{0.0.0.00000000}.{demo-headset}", "Headset Earphone", "SteelSeries Arctis Nova Pro", AudioFlow.Render, false, true, "Active", 65, false, "16 bit, 48000 Hz", 0),
        new("{0.0.0.00000000}.{demo-monitor}", DemoData.MonitorName, "NVIDIA High Definition Audio", AudioFlow.Render, false, false, "Active", 100, true, "24 bit, 48000 Hz", 0),
        new("{0.0.1.00000000}.{demo-mic}", "Headset Microphone", "SteelSeries Arctis Nova Pro", AudioFlow.Capture, true, true, "Active", 80, false, "16 bit, 48000 Hz", 0.05f),
        new("{0.0.1.00000000}.{demo-webcam}", "Microphone", "Logitech BRIO", AudioFlow.Capture, false, false, "Active", 60, false, "16 bit, 32000 Hz", 0),
    ];

    private readonly List<AudioSession> _sessions =
    [
        new("session.system", 0, "System sounds", 60, false, null),
        new("session.discord", 4312, "Discord", 85, false, null),
        new("session.spotify", 7788, "Spotify", 48, false, null),
        new("session.chrome", 9120, "Google Chrome", 70, false, null),
    ];

    public event EventHandler? DevicesChanged;

    public async Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var random = DemoData.CreateRandom(Environment.TickCount / 300);
        lock (_gate)
        {
            return AudioText.SortDevices(_devices.Select(d => d with { PeakLevel = d.Muted ? 0 : (float)Math.Clamp(DemoData.Jitter(random, d.PeakLevel ?? 0.1, 0.08), 0, 1) }));
        }
    }

    /// <summary>Animated levels: the default output "plays music", the default microphone picks up a little room noise.</summary>
    public Task<IReadOnlyDictionary<string, float>> ReadPeakLevelsAsync(CancellationToken cancellationToken)
    {
        int tick = Interlocked.Increment(ref _meterTick);
        var levels = new Dictionary<string, float>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var d in _devices)
            {
                if (d.Muted || d.State != "Active")
                {
                    levels[d.Id] = 0;
                    continue;
                }

                double phase = tick / 7.0;
                float level = (d.Flow, d.IsDefault) switch
                {
                    (AudioFlow.Render, true) => (float)(0.35 + 0.25 * Math.Sin(phase) + 0.12 * Math.Sin(phase * 3.3)),
                    (AudioFlow.Capture, true) => (float)(0.08 + 0.05 * Math.Abs(Math.Sin(phase * 1.7))),
                    _ => 0f,
                };
                levels[d.Id] = Math.Clamp(level, 0f, 1f);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, float>>(levels);
    }

    public async Task<OperationResult> SetDefaultDeviceAsync(string deviceId, AudioFlow flow, bool forCommunications, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            for (int i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                if (d.Flow != flow)
                {
                    continue;
                }

                bool isTarget = d.Id == deviceId;
                _devices[i] = forCommunications ? d with { IsDefaultCommunications = isTarget } : d with { IsDefault = isTarget };
            }
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok($"Default {(forCommunications ? "communications " : string.Empty)}device changed (demo).");
    }

    public Task<OperationResult> SetVolumeAsync(string deviceId, float percent, CancellationToken cancellationToken) =>
        UpdateDevice(deviceId, d => d with { VolumePercent = Math.Clamp(percent, 0, 100) }, $"Volume set to {Math.Clamp(percent, 0, 100):0}% (demo).", cancellationToken);

    public Task<OperationResult> SetMuteAsync(string deviceId, bool muted, CancellationToken cancellationToken) =>
        UpdateDevice(deviceId, d => d with { Muted = muted }, muted ? "Muted (demo)." : "Unmuted (demo).", cancellationToken);

    public async Task<IReadOnlyList<AudioSession>> GetSessionsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 250).ConfigureAwait(false);
        lock (_gate)
        {
            return _sessions.ToList();
        }
    }

    public Task<OperationResult> SetSessionVolumeAsync(string sessionId, float percent, CancellationToken cancellationToken) =>
        UpdateSession(sessionId, s => s with { VolumePercent = Math.Clamp(percent, 0, 100) }, "Session volume changed (demo).", cancellationToken);

    public Task<OperationResult> SetSessionMuteAsync(string sessionId, bool muted, CancellationToken cancellationToken) =>
        UpdateSession(sessionId, s => s with { Muted = muted }, muted ? "Session muted (demo)." : "Session unmuted (demo).", cancellationToken);

    public async Task<OperationResult> RestartAudioServiceAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 500, 1200).ConfigureAwait(false);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Windows Audio service restarted (demo).");
    }

    public OperationResult OpenSoundSettings() => OperationResult.Ok("Would open Sound settings (demo).");
    public OperationResult OpenSoundControlPanel() => OperationResult.Ok("Would open the Sound control panel (demo).");
    public OperationResult OpenAudioTroubleshooter() => OperationResult.Ok("Would run the audio troubleshooter (demo).");
    public OperationResult OpenVolumeMixer() => OperationResult.Ok("Would open the volume mixer (demo).");

    private async Task<OperationResult> UpdateDevice(string id, Func<AudioDevice, AudioDevice> change, string message, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 30, 120).ConfigureAwait(false);
        lock (_gate)
        {
            int i = _devices.FindIndex(d => d.Id == id);
            if (i < 0)
            {
                return OperationResult.Fail("Unknown audio device.");
            }

            _devices[i] = change(_devices[i]);
        }

        return OperationResult.Ok(message);
    }

    private async Task<OperationResult> UpdateSession(string id, Func<AudioSession, AudioSession> change, string message, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 30, 120).ConfigureAwait(false);
        lock (_gate)
        {
            int i = _sessions.FindIndex(s => s.Id == id);
            if (i < 0)
            {
                return OperationResult.Fail("Unknown audio session.");
            }

            _sessions[i] = change(_sessions[i]);
        }

        return OperationResult.Ok(message);
    }
}
