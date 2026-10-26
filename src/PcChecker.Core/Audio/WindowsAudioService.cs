using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Audio;

/// <summary>
/// Windows implementation of <see cref="IAudioService"/> on NAudio's Core Audio wrappers (WASAPI). Every call creates
/// its own <see cref="MMDeviceEnumerator"/> on a thread-pool (MTA) thread; the one long-lived enumerator only serves
/// device-change notifications. Default-device changes use the <see cref="IPolicyConfig"/> interop, the service
/// restart goes through <see cref="IChangeGuard"/>.
/// </summary>
public sealed class WindowsAudioService : IAudioService, IAudioLevelMeter, IDisposable
{
    private const string Module = "Audio";
    private static readonly TimeSpan NotificationDebounce = TimeSpan.FromMilliseconds(400);

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsAudioService> _logger;
    private readonly object _notifyGate = new();
    private MMDeviceEnumerator? _notificationEnumerator;
    private NotificationClient? _notificationClient;
    private Timer? _debounceTimer;
    private bool _disposed;

    public WindowsAudioService(IChangeGuard guard, ILogger<WindowsAudioService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public event EventHandler? DevicesChanged;

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<IReadOnlyList<AudioDevice>>([]);
        }

        return Task.Run<IReadOnlyList<AudioDevice>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotifications();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = CoreAudioReader.ReadDevices(enumerator, _logger);
                _logger.LogDebug("Read {Count} audio endpoints.", devices.Count);
                return AudioText.SortDevices(devices);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio devices could not be read. Is the Windows Audio service running?");
                return [];
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, float>> ReadPeakLevelsAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<IReadOnlyDictionary<string, float>>(new Dictionary<string, float>());
        }

        return Task.Run<IReadOnlyDictionary<string, float>>(() =>
        {
            var levels = new Dictionary<string, float>(StringComparer.Ordinal);
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
                {
                    using (device)
                    {
                        try
                        {
                            levels[device.ID] = device.AudioMeterInformation.MasterPeakValue;
                        }
                        catch
                        {
                            // A device without a meter simply shows no level.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Peak levels could not be read.");
            }

            return levels;
        }, cancellationToken);
    }

    public async Task<OperationResult> SetDefaultDeviceAsync(string deviceId, AudioFlow flow, bool forCommunications, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return OperationResult.Fail("No device was selected.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Changing the default device is only possible on Windows.");
        }

        var roleText = forCommunications ? "communications" : "playback";
        var kind = flow == AudioFlow.Capture ? "recording" : "output";
        var options = new ChangeOptions(false, false, Module, deviceId, true,
            "Pick the previous device on the Audio page and choose \"Set as default\" again.");

        return await _guard.RunGuardedAsync($"Set default {kind} device ({roleText})", options, ct => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var roles = forCommunications
                ? new[] { PolicyRole.Communications }
                : new[] { PolicyRole.Console, PolicyRole.Multimedia };
            var error = PolicyConfig.SetDefaultEndpoint(deviceId, roles);
            if (error is not null)
            {
                return OperationResult.Fail(error);
            }

            RaiseDevicesChanged();
            return OperationResult.Ok(forCommunications
                ? "This device will now be used for calls and chat apps."
                : $"This device is now the default {kind} device.");
        }, ct), null, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult> SetVolumeAsync(string deviceId, float percent, CancellationToken cancellationToken) =>
        WithDeviceAsync(deviceId, device =>
        {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = AudioText.PercentToScalar(percent);
            return OperationResult.Ok($"Volume set to {Math.Clamp(percent, 0, 100):0} %.");
        }, cancellationToken);

    public Task<OperationResult> SetMuteAsync(string deviceId, bool muted, CancellationToken cancellationToken) =>
        WithDeviceAsync(deviceId, device =>
        {
            device.AudioEndpointVolume.Mute = muted;
            return OperationResult.Ok(muted ? "Muted." : "Unmuted.");
        }, cancellationToken);

    public Task<IReadOnlyList<AudioSession>> GetSessionsAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<IReadOnlyList<AudioSession>>([]);
        }

        return Task.Run<IReadOnlyList<AudioSession>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return CoreAudioReader.ReadSessions(enumerator, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio sessions could not be read.");
                return [];
            }
        }, cancellationToken);
    }

    public Task<OperationResult> SetSessionVolumeAsync(string sessionId, float percent, CancellationToken cancellationToken) =>
        WithSessionAsync(sessionId, control => control.SimpleAudioVolume.Volume = AudioText.PercentToScalar(percent),
            $"App volume set to {Math.Clamp(percent, 0, 100):0} %.", cancellationToken);

    public Task<OperationResult> SetSessionMuteAsync(string sessionId, bool muted, CancellationToken cancellationToken) =>
        WithSessionAsync(sessionId, control => control.SimpleAudioVolume.Mute = muted,
            muted ? "App muted." : "App unmuted.", cancellationToken);

    public async Task<OperationResult> RestartAudioServiceAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("The Windows Audio service only exists on Windows.");
        }

        var options = new ChangeOptions(false, false, Module, "Audiosrv", true, "The service restarts on its own at the next sign-in; nothing to undo.");
        return await _guard.RunGuardedAsync("Restart the Windows Audio service", options, async ct =>
        {
            // AudioEndpointBuilder is the dependency; -Force restarts Audiosrv with it. Start Audiosrv again in case it stayed stopped.
            const string script = "Restart-Service -Name 'AudioEndpointBuilder' -Force -ErrorAction Stop; " +
                                  "Start-Service -Name 'Audiosrv' -ErrorAction SilentlyContinue; " +
                                  "(Get-Service -Name 'Audiosrv').Status";
            var result = await PowerShellRunner.RunAsync(script, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!result.Success)
            {
                var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                return OperationResult.Fail("The audio service could not be restarted: " + FirstLine(detail));
            }

            RaiseDevicesChanged();
            var status = FirstLine(result.StdOut);
            return status.Equals("Running", StringComparison.OrdinalIgnoreCase)
                ? OperationResult.Ok("The Windows Audio service was restarted and is running again.")
                : OperationResult.Ok($"The Windows Audio service was restarted (status: {status}).");
        }, null, cancellationToken).ConfigureAwait(false);
    }

    public OperationResult OpenSoundSettings() => ShellLauncher.Open("ms-settings:sound");

    public OperationResult OpenSoundControlPanel() => ShellLauncher.Open("control.exe", "mmsys.cpl");

    public OperationResult OpenAudioTroubleshooter()
    {
        // msdt.exe was retired on recent Windows 11 builds; fall back to the Settings troubleshooter page.
        try
        {
            var msdt = Path.Combine(Environment.SystemDirectory, "msdt.exe");
            if (File.Exists(msdt))
            {
                var result = ShellLauncher.Open(msdt, "-id AudioPlaybackDiagnostic");
                if (result.Success)
                {
                    return OperationResult.Ok("The audio playback troubleshooter was started.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "msdt.exe could not be started; using the Settings page instead.");
        }

        var fallback = ShellLauncher.Open("ms-settings:troubleshoot");
        return fallback.Success
            ? OperationResult.Ok("Opened Settings → Troubleshoot. Choose \"Other troubleshooters\" → \"Playing Audio\".")
            : fallback;
    }

    public OperationResult OpenVolumeMixer()
    {
        var result = ShellLauncher.Open("sndvol.exe");
        return result.Success ? result : ShellLauncher.Open("ms-settings:apps-volume");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_notifyGate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            try
            {
                if (_notificationEnumerator is not null && _notificationClient is not null)
                {
                    _notificationEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                }

                _notificationEnumerator?.Dispose();
            }
            catch
            {
                // Best effort during shutdown.
            }

            _notificationEnumerator = null;
            _notificationClient = null;
        }
    }

    private Task<OperationResult> WithDeviceAsync(string deviceId, Func<MMDevice, OperationResult> change, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return Task.FromResult(OperationResult.Fail("No device was selected."));
        }

        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Audio control is only possible on Windows."));
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(deviceId);
                if (device.State != DeviceState.Active)
                {
                    return OperationResult.Fail("This device is not active, so its volume cannot be changed. Plug it in or enable it first.");
                }

                return change(device);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio device {DeviceId} could not be changed.", deviceId);
                return OperationResult.Fail("The device could not be changed: " + ex.Message, ex);
            }
        }, cancellationToken);
    }

    private Task<OperationResult> WithSessionAsync(string sessionId, Action<AudioSessionControl> change, string successMessage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult(OperationResult.Fail("No app was selected."));
        }

        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Per-app volume is only possible on Windows."));
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return CoreAudioReader.WithSession(enumerator, sessionId, change)
                    ? OperationResult.Ok(successMessage)
                    : OperationResult.Fail("That app is no longer playing sound. Refresh the mixer to see the current apps.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio session {SessionId} could not be changed.", sessionId);
                return OperationResult.Fail("The app's volume could not be changed: " + ex.Message, ex);
            }
        }, cancellationToken);
    }

    private void EnsureNotifications()
    {
        lock (_notifyGate)
        {
            if (_disposed || _notificationEnumerator is not null)
            {
                return;
            }

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var client = new NotificationClient(ScheduleDevicesChanged);
                enumerator.RegisterEndpointNotificationCallback(client);
                _notificationEnumerator = enumerator;
                _notificationClient = client;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio device notifications are unavailable; the page will rely on manual refresh.");
            }
        }
    }

    /// <summary>Collapses bursts of notifications (Windows fires several per plug event) into one event.</summary>
    private void ScheduleDevicesChanged()
    {
        lock (_notifyGate)
        {
            if (_disposed)
            {
                return;
            }

            _debounceTimer ??= new Timer(_ => RaiseDevicesChanged(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(NotificationDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseDevicesChanged()
    {
        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A DevicesChanged subscriber threw.");
        }
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "unknown";
        }

        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "unknown" : line;
    }

    /// <summary>Receives endpoint notifications from Windows on a COM thread.</summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly Action _changed;

        public NotificationClient(Action changed) => _changed = changed;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();

        public void OnDeviceAdded(string pwstrDeviceId) => _changed();

        public void OnDeviceRemoved(string deviceId) => _changed();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _changed();

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Property changes fire constantly (volume popups, driver chatter); the page refreshes on demand instead.
        }
    }
}
