using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Audio;

namespace PcChecker.App.ViewModels;

/// <summary>
/// One sound device card. Slider and mute changes made by the user are forwarded to the owner through
/// <see cref="VolumeChangeRequested"/> / <see cref="MuteChangeRequested"/>; updates coming from Windows go through
/// <see cref="Update"/>, which never echoes back to the service.
/// </summary>
public sealed partial class AudioDeviceItem : ObservableObject
{
    private bool _suppressCallbacks;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _interfaceName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetDefault))]
    private bool _isDefault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetCommunications))]
    private bool _isDefaultCommunications;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanSetDefault))]
    [NotifyPropertyChangedFor(nameof(CanSetCommunications))]
    private string _state = "Unknown";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private float _volumePercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private bool _muted;

    [ObservableProperty]
    private string? _formatText;

    [ObservableProperty]
    private double _peakPercent;

    public AudioDeviceItem(AudioDevice device)
    {
        Id = device.Id;
        Flow = device.Flow;
        Update(device);
    }

    public string Id { get; }

    public AudioFlow Flow { get; }

    public bool IsOutput => Flow == AudioFlow.Render;

    public bool IsActive => string.Equals(State, "Active", StringComparison.OrdinalIgnoreCase);

    public bool CanSetDefault => IsActive && !IsDefault;

    public bool CanSetCommunications => IsActive && !IsDefaultCommunications;

    public string VolumeText => Muted ? "Muted" : $"{VolumePercent:0} %";

    /// <summary>Raised when the user moves the slider (not when Windows reports a new value).</summary>
    public event Action<AudioDeviceItem, float>? VolumeChangeRequested;

    /// <summary>Raised when the user toggles mute (not when Windows reports a new value).</summary>
    public event Action<AudioDeviceItem, bool>? MuteChangeRequested;

    /// <summary>Applies a fresh snapshot from the service without triggering callbacks.</summary>
    public void Update(AudioDevice device)
    {
        _suppressCallbacks = true;
        try
        {
            Name = device.Name;
            InterfaceName = device.InterfaceName;
            IsDefault = device.IsDefault;
            IsDefaultCommunications = device.IsDefaultCommunications;
            State = device.State;
            VolumePercent = device.VolumePercent;
            Muted = device.Muted;
            FormatText = device.FormatText;
            PeakPercent = AudioText.PeakToPercent(device.PeakLevel);
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    /// <summary>Sets the meter from a live peak reading (0..1).</summary>
    public void SetPeak(float? peak) => PeakPercent = AudioText.PeakToPercent(peak);

    partial void OnVolumePercentChanged(float value)
    {
        if (!_suppressCallbacks)
        {
            VolumeChangeRequested?.Invoke(this, value);
        }
    }

    partial void OnMutedChanged(bool value)
    {
        if (!_suppressCallbacks)
        {
            MuteChangeRequested?.Invoke(this, value);
        }
    }
}

/// <summary>One row of the per-app volume mixer.</summary>
public sealed partial class AudioSessionItem : ObservableObject
{
    private bool _suppressCallbacks;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private float _volumePercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private bool _muted;

    public AudioSessionItem(AudioSession session)
    {
        Id = session.Id;
        ProcessId = session.ProcessId;
        Update(session);
    }

    public string Id { get; }

    public int ProcessId { get; }

    public string ProcessText => ProcessId > 0 ? $"PID {ProcessId}" : "Windows";

    public string VolumeText => Muted ? "Muted" : $"{VolumePercent:0} %";

    public event Action<AudioSessionItem, float>? VolumeChangeRequested;

    public event Action<AudioSessionItem, bool>? MuteChangeRequested;

    public void Update(AudioSession session)
    {
        _suppressCallbacks = true;
        try
        {
            DisplayName = session.DisplayName;
            VolumePercent = session.VolumePercent;
            Muted = session.Muted;
        }
        finally
        {
            _suppressCallbacks = false;
        }
    }

    partial void OnVolumePercentChanged(float value)
    {
        if (!_suppressCallbacks)
        {
            VolumeChangeRequested?.Invoke(this, value);
        }
    }

    partial void OnMutedChanged(bool value)
    {
        if (!_suppressCallbacks)
        {
            MuteChangeRequested?.Invoke(this, value);
        }
    }
}
