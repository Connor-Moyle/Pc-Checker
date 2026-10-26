namespace PcChecker.Core.Audio;

/// <summary>
/// Pure helpers shared by the audio services and the page: volume conversions, format text, friendly-name
/// splitting, session naming and device ordering. No Windows dependencies, so everything here is unit-tested.
/// </summary>
public static class AudioText
{
    /// <summary>Core Audio volume scalar (0..1) → percent (0..100), clamped.</summary>
    public static float ScalarToPercent(float scalar)
    {
        if (float.IsNaN(scalar))
        {
            return 0;
        }

        return (float)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f, 1);
    }

    /// <summary>Percent (0..100) → Core Audio volume scalar (0..1), clamped.</summary>
    public static float PercentToScalar(float percent)
    {
        if (float.IsNaN(percent))
        {
            return 0;
        }

        return Math.Clamp(percent, 0f, 100f) / 100f;
    }

    /// <summary>Peak meter value (0..1) → percent for a progress bar, clamped.</summary>
    public static double PeakToPercent(float? peak) =>
        peak is null || float.IsNaN(peak.Value) ? 0 : Math.Clamp(peak.Value, 0f, 1f) * 100.0;

    /// <summary>Builds "24 bit, 48000 Hz, Stereo" from the parts Windows reports; null when nothing is known.</summary>
    public static string? BuildFormatText(int? bitsPerSample, int? sampleRate, int? channels)
    {
        var parts = new List<string>(3);
        if (bitsPerSample is > 0)
        {
            parts.Add($"{bitsPerSample} bit");
        }

        if (sampleRate is > 0)
        {
            parts.Add($"{sampleRate} Hz");
        }

        if (channels is > 0)
        {
            parts.Add(ChannelText(channels.Value));
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Channel count → "Mono", "Stereo", "5.1", "7.1" or "n channels".</summary>
    public static string ChannelText(int channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        4 => "Quadraphonic",
        6 => "5.1 surround",
        8 => "7.1 surround",
        _ => $"{channels} channels",
    };

    /// <summary>
    /// Splits a Windows endpoint friendly name such as "Speakers (Realtek(R) Audio)" into the endpoint part
    /// ("Speakers") and the adapter part ("Realtek(R) Audio"). Names without a trailing bracket are returned whole.
    /// </summary>
    public static (string Name, string? Interface) SplitFriendlyName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return ("Unknown device", null);
        }

        var text = friendlyName.Trim();
        if (!text.EndsWith(')'))
        {
            return (text, null);
        }

        // Walk backwards to the bracket that matches the final ')', so "(R)" inside the adapter name survives.
        int depth = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == ')')
            {
                depth++;
            }
            else if (text[i] == '(')
            {
                depth--;
                if (depth == 0)
                {
                    var name = text[..i].TrimEnd();
                    var iface = text[(i + 1)..^1].Trim();
                    if (name.Length == 0 || iface.Length == 0)
                    {
                        return (text, null);
                    }

                    return (name, iface);
                }
            }
        }

        return (text, null);
    }

    /// <summary>Maps the Core Audio device state name to plain English.</summary>
    public static string StateText(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return "Unknown";
        }

        return state.Trim() switch
        {
            "Active" => "Active",
            "Disabled" => "Disabled",
            "NotPresent" => "Not present",
            "Unplugged" => "Unplugged",
            var other => other,
        };
    }

    /// <summary>The badge labels shown on a device card, in display order.</summary>
    public static IReadOnlyList<string> DefaultBadges(AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var badges = new List<string>(2);
        if (device.IsDefault)
        {
            badges.Add("Default");
        }

        if (device.IsDefaultCommunications)
        {
            badges.Add("Default for calls");
        }

        return badges;
    }

    /// <summary>
    /// Picks the name shown in the per-app mixer: the session's display name unless it is empty or a resource
    /// reference ("@%SystemRoot%\..."), then the process name, then "Process 1234". System sounds get a fixed label.
    /// </summary>
    public static string SessionDisplayName(string? displayName, string? processName, int processId, bool isSystemSounds)
    {
        if (isSystemSounds)
        {
            return "System sounds";
        }

        if (!string.IsNullOrWhiteSpace(displayName) && !displayName.TrimStart().StartsWith('@'))
        {
            return displayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            return PrettyProcessName(processName);
        }

        return processId <= 0 ? "Unknown app" : $"Process {processId}";
    }

    /// <summary>"chrome" → "Chrome", "Discord.exe" → "Discord".</summary>
    public static string PrettyProcessName(string processName)
    {
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (name.Length == 0)
        {
            return "Unknown app";
        }

        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Orders devices for display: default first, then the communications default, then active devices, then the rest,
    /// each group alphabetically.
    /// </summary>
    public static IReadOnlyList<AudioDevice> SortDevices(IEnumerable<AudioDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices
            .OrderByDescending(d => d.IsDefault)
            .ThenByDescending(d => d.IsDefaultCommunications)
            .ThenByDescending(d => string.Equals(d.State, "Active", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Short status line for the header, e.g. "Speakers (Realtek(R) Audio) · 42 %".</summary>
    public static string Summarize(AudioDevice? device)
    {
        if (device is null)
        {
            return "None";
        }

        var name = device.InterfaceName is null ? device.Name : $"{device.Name} ({device.InterfaceName})";
        return device.Muted ? $"{name} · muted" : $"{name} · {device.VolumePercent:0} %";
    }
}
