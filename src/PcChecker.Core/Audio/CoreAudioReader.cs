using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PcChecker.Core.Audio;

/// <summary>
/// Reads devices and sessions from NAudio's Core Audio wrappers. Every property read is guarded individually so one
/// misbehaving driver degrades a single field instead of the whole list. Call from an MTA (thread pool) thread.
/// </summary>
internal static class CoreAudioReader
{
    /// <summary>Default device ids per flow and role; missing entries mean Windows has no default for that role.</summary>
    internal sealed record Defaults(string? Render, string? RenderCommunications, string? Capture, string? CaptureCommunications);

    public static Defaults ReadDefaults(MMDeviceEnumerator enumerator) => new(
        TryDefaultId(enumerator, DataFlow.Render, Role.Multimedia),
        TryDefaultId(enumerator, DataFlow.Render, Role.Communications),
        TryDefaultId(enumerator, DataFlow.Capture, Role.Multimedia),
        TryDefaultId(enumerator, DataFlow.Capture, Role.Communications));

    public static List<AudioDevice> ReadDevices(MMDeviceEnumerator enumerator, ILogger logger)
    {
        var defaults = ReadDefaults(enumerator);
        var list = new List<AudioDevice>();
        MMDeviceCollection collection;
        try
        {
            collection = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audio endpoints could not be enumerated.");
            return list;
        }

        foreach (var device in collection)
        {
            using (device)
            {
                var item = ReadDevice(device, defaults, logger);
                if (item is not null)
                {
                    list.Add(item);
                }
            }
        }

        return list;
    }

    public static AudioDevice? ReadDevice(MMDevice device, Defaults defaults, ILogger logger)
    {
        string id;
        try
        {
            id = device.ID;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "An audio endpoint has no readable id; skipped.");
            return null;
        }

        var flow = Guard(() => device.DataFlow == DataFlow.Capture ? AudioFlow.Capture : AudioFlow.Render, AudioFlow.Render);
        var state = Guard(() => device.State, DeviceState.NotPresent);
        bool active = state == DeviceState.Active;

        var (name, iface) = ReadNames(device);
        bool isDefault = flow == AudioFlow.Render ? id == defaults.Render : id == defaults.Capture;
        bool isComms = flow == AudioFlow.Render ? id == defaults.RenderCommunications : id == defaults.CaptureCommunications;

        float volume = 0;
        bool muted = false;
        float? peak = null;
        if (active)
        {
            volume = Guard(() => AudioText.ScalarToPercent(device.AudioEndpointVolume.MasterVolumeLevelScalar), 0f);
            muted = Guard(() => device.AudioEndpointVolume.Mute, false);
            peak = Guard<float?>(() => device.AudioMeterInformation.MasterPeakValue, null);
        }

        return new AudioDevice(
            id,
            name,
            iface,
            flow,
            isDefault,
            isComms,
            AudioText.StateText(state.ToString()),
            volume,
            muted,
            ReadFormatText(device, active),
            peak);
    }

    public static (string Name, string? Interface) ReadNames(MMDevice device)
    {
        var friendly = Guard<string?>(() => device.FriendlyName, null);
        var description = Guard<string?>(() =>
            device.Properties.Contains(PropertyKeys.PKEY_Device_DeviceDesc)
                ? device.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string
                : null, null);
        var adapter = Guard<string?>(() => device.DeviceFriendlyName, null);

        if (!string.IsNullOrWhiteSpace(description))
        {
            return (description.Trim(), string.IsNullOrWhiteSpace(adapter) ? AudioText.SplitFriendlyName(friendly).Interface : adapter.Trim());
        }

        var split = AudioText.SplitFriendlyName(friendly);
        return (split.Name, split.Interface ?? (string.IsNullOrWhiteSpace(adapter) ? null : adapter.Trim()));
    }

    public static string? ReadFormatText(MMDevice device, bool active)
    {
        // Preferred: the format shown in the Windows "Advanced" tab (the endpoint's configured device format).
        var blob = Guard<byte[]?>(() =>
            device.Properties.Contains(PropertyKeys.PKEY_AudioEngine_DeviceFormat)
                ? device.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat].Value as byte[]
                : null, null);
        if (blob is not null && WaveFormatParser.TryParse(blob, out var summary))
        {
            return summary.ToDisplayText();
        }

        if (!active)
        {
            return null;
        }

        // Fallback: the shared-mode mix format (usually 32-bit float at the device rate).
        return Guard<string?>(() =>
        {
            var mix = device.AudioClient.MixFormat;
            return AudioText.BuildFormatText(mix.BitsPerSample, mix.SampleRate, mix.Channels);
        }, null);
    }

    /// <summary>Reads the sessions of every active playback device (the volume mixer view).</summary>
    public static List<AudioSession> ReadSessions(MMDeviceEnumerator enumerator, ILogger logger)
    {
        var sessions = new List<AudioSession>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        MMDeviceCollection devices;
        try
        {
            devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Playback devices could not be enumerated for the mixer.");
            return sessions;
        }

        foreach (var device in devices)
        {
            using (device)
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var collection = manager.Sessions;
                    for (int i = 0; i < collection.Count; i++)
                    {
                        var session = ReadSession(collection[i]);
                        if (session is not null && seen.Add(session.Id))
                        {
                            sessions.Add(session);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Sessions of one playback device could not be read.");
                }
            }
        }

        return sessions
            .OrderByDescending(s => s.ProcessId == 0)
            .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static AudioSession? ReadSession(AudioSessionControl control)
    {
        var state = Guard(() => control.State, AudioSessionState.AudioSessionStateInactive);
        if (state == AudioSessionState.AudioSessionStateExpired)
        {
            return null;
        }

        int pid = Guard(() => (int)control.GetProcessID, 0);
        bool system = Guard(() => control.IsSystemSoundsSession, false);
        var id = Guard<string?>(() => control.GetSessionInstanceIdentifier, null) ?? $"pid:{pid}";
        var processName = pid > 0 ? Guard<string?>(() => Process.GetProcessById(pid).ProcessName, null) : null;
        var display = AudioText.SessionDisplayName(Guard<string?>(() => control.DisplayName, null), processName, pid, system);
        float volume = Guard(() => AudioText.ScalarToPercent(control.SimpleAudioVolume.Volume), 100f);
        bool muted = Guard(() => control.SimpleAudioVolume.Mute, false);
        var icon = Guard<string?>(() => control.IconPath, null);
        return new AudioSession(id, pid, display, volume, muted, string.IsNullOrWhiteSpace(icon) ? null : icon);
    }

    /// <summary>Finds a session by instance id across active playback devices and applies <paramref name="apply"/>.</summary>
    public static bool WithSession(MMDeviceEnumerator enumerator, string sessionId, Action<AudioSessionControl> apply)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                AudioSessionManager manager;
                try
                {
                    manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                }
                catch
                {
                    continue;
                }

                var collection = manager.Sessions;
                for (int i = 0; i < collection.Count; i++)
                {
                    var control = collection[i];
                    var id = Guard<string?>(() => control.GetSessionInstanceIdentifier, null)
                        ?? $"pid:{Guard(() => (int)control.GetProcessID, 0)}";
                    if (id == sessionId)
                    {
                        apply(control);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string? TryDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch
        {
            return null; // No device for this role (e.g. no microphone connected).
        }
    }

    private static T Guard<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }
}
