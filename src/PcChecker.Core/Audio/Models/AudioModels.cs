namespace PcChecker.Core.Audio;

public enum AudioFlow
{
    /// <summary>Playback (speakers, headphones).</summary>
    Render,

    /// <summary>Recording (microphones, line in).</summary>
    Capture,
}

public sealed record AudioDevice(
    string Id,
    string Name,
    string? InterfaceName,
    AudioFlow Flow,
    bool IsDefault,
    bool IsDefaultCommunications,
    string State,
    float VolumePercent,
    bool Muted,
    string? FormatText,
    float? PeakLevel);

/// <summary>One application's audio session (a row in the volume mixer).</summary>
public sealed record AudioSession(string Id, int ProcessId, string DisplayName, float VolumePercent, bool Muted, string? IconPath);
