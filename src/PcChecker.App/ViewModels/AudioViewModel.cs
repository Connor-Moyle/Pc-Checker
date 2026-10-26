using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Audio;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the Audio page: output and input device cards with live meters, the per-app mixer and the
/// Windows sound tools. Volume changes are debounced per device so dragging a slider sends one update at a time.
/// </summary>
public sealed partial class AudioViewModel : ViewModelBase
{
    private static readonly TimeSpan VolumeDebounce = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(100);

    private readonly IAudioService _audio;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<AudioViewModel> _logger;
    private readonly Dictionary<string, CancellationTokenSource> _pendingVolume = new(StringComparer.Ordinal);
    private DispatcherTimer? _meterTimer;
    private bool _meterReadInFlight;
    private bool _refreshQueued;

    [ObservableProperty]
    private string _defaultOutputName = "None";

    [ObservableProperty]
    private string? _defaultOutputDetail;

    [ObservableProperty]
    private string _defaultInputName = "None";

    [ObservableProperty]
    private string? _defaultInputDetail;

    [ObservableProperty]
    private string _sessionSummary = "No apps are playing sound";

    [ObservableProperty]
    private string? _lastRefreshedText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoOutputs))]
    private int _outputCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoInputs))]
    private int _inputCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSessions))]
    private int _sessionCount;

    [ObservableProperty]
    private bool _isLiveMetering;

    [ObservableProperty]
    private string? _statusMessage;

    public AudioViewModel(IAudioService audio, IDialogService dialogs, INotificationService notifications,
        ISettingsService settings, ILogger<AudioViewModel> logger)
        : base(settings)
    {
        _audio = audio;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        _audio.DevicesChanged += OnDevicesChanged;
    }

    public ObservableCollection<AudioDeviceItem> OutputDevices { get; } = [];

    public ObservableCollection<AudioDeviceItem> InputDevices { get; } = [];

    public ObservableCollection<AudioSessionItem> Sessions { get; } = [];

    public bool HasNoOutputs => OutputCount == 0;

    public bool HasNoInputs => InputCount == 0;

    public bool HasNoSessions => SessionCount == 0;

    public bool SupportsLiveMeters => _audio is IAudioLevelMeter;

    public override Task InitializeAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public override Task RefreshAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async ct =>
        {
            await LoadDevicesAsync(ct);
            await LoadSessionsAsync(ct);
            LastRefreshedText = "Updated " + DateTime.Now.ToString("HH:mm:ss");
        }, "Reading sound devices…");

    /// <summary>Called by the page when it becomes visible: starts the 100 ms meter timer.</summary>
    public void StartLiveMeters()
    {
        if (!SupportsLiveMeters || _meterTimer is not null)
        {
            return;
        }

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = MeterInterval };
        _meterTimer.Tick += OnMeterTick;
        _meterTimer.Start();
        IsLiveMetering = true;
    }

    /// <summary>Called by the page when it is hidden: stops polling so nothing runs in the background.</summary>
    public void StopLiveMeters()
    {
        if (_meterTimer is null)
        {
            return;
        }

        _meterTimer.Stop();
        _meterTimer.Tick -= OnMeterTick;
        _meterTimer = null;
        IsLiveMetering = false;
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private Task RefreshSessions() => RunBusyAsync(LoadSessionsAsync, "Reading apps that play sound…");

    [RelayCommand]
    private async Task SetDefault(AudioDeviceItem? item)
    {
        if (item is null)
        {
            return;
        }

        var result = await _audio.SetDefaultDeviceAsync(item.Id, item.Flow, false, CancellationToken.None);
        Report(result, item.IsOutput ? "Default output" : "Default microphone");
        await LoadDevicesQuietlyAsync();
    }

    [RelayCommand]
    private async Task SetDefaultForCalls(AudioDeviceItem? item)
    {
        if (item is null)
        {
            return;
        }

        var result = await _audio.SetDefaultDeviceAsync(item.Id, item.Flow, true, CancellationToken.None);
        Report(result, "Device for calls");
        await LoadDevicesQuietlyAsync();
    }

    [RelayCommand]
    private async Task RestartAudioService()
    {
        bool ok = await _dialogs.ConfirmAsync(
            "Restart the Windows Audio service?",
            "Sound will cut out for a few seconds in every app while the service restarts. This fixes most \"no sound\" " +
            "and crackling problems without a reboot. Apps that are playing audio may need to be restarted afterwards.",
            "Restart service",
            isDanger: true);
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _audio.RestartAudioServiceAsync(ct);
            Report(result, "Audio service");
            await LoadDevicesAsync(ct);
            await LoadSessionsAsync(ct);
        }, "Restarting the Windows Audio service…");
    }

    [RelayCommand]
    private void RunTroubleshooter() => Report(_audio.OpenAudioTroubleshooter(), "Troubleshooter");

    [RelayCommand]
    private void OpenSoundSettings() => Report(_audio.OpenSoundSettings(), "Sound settings", quietOnSuccess: true);

    [RelayCommand]
    private void OpenVolumeMixer() => Report(_audio.OpenVolumeMixer(), "Volume mixer", quietOnSuccess: true);

    [RelayCommand]
    private void OpenControlPanel() => Report(_audio.OpenSoundControlPanel(), "Sound control panel", quietOnSuccess: true);

    private async Task LoadDevicesAsync(CancellationToken ct)
    {
        var devices = await _audio.GetDevicesAsync(ct);
        ct.ThrowIfCancellationRequested();
        Merge(OutputDevices, devices.Where(d => d.Flow == AudioFlow.Render));
        Merge(InputDevices, devices.Where(d => d.Flow == AudioFlow.Capture));
        OutputCount = OutputDevices.Count;
        InputCount = InputDevices.Count;
        (DefaultOutputName, DefaultOutputDetail) = Describe(devices.FirstOrDefault(d => d.Flow == AudioFlow.Render && d.IsDefault));
        (DefaultInputName, DefaultInputDetail) = Describe(devices.FirstOrDefault(d => d.Flow == AudioFlow.Capture && d.IsDefault));
        StatusMessage = devices.Count == 0
            ? "No sound devices were found. If the Windows Audio service is stopped, use \"Restart audio service\" below."
            : null;
    }

    private static (string Name, string? Detail) Describe(AudioDevice? device)
    {
        if (device is null)
        {
            return ("None", "No default device is set");
        }

        var level = device.Muted ? "muted" : $"{device.VolumePercent:0} % volume";
        return (device.Name, device.InterfaceName is null ? level : $"{device.InterfaceName} · {level}");
    }

    private async Task LoadSessionsAsync(CancellationToken ct)
    {
        var sessions = await _audio.GetSessionsAsync(ct);
        ct.ThrowIfCancellationRequested();

        var byId = Sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var session in sessions)
        {
            keep.Add(session.Id);
            if (byId.TryGetValue(session.Id, out var existing))
            {
                existing.Update(session);
                int current = Sessions.IndexOf(existing);
                if (current != index)
                {
                    Sessions.Move(current, index);
                }
            }
            else
            {
                var item = new AudioSessionItem(session);
                item.VolumeChangeRequested += OnSessionVolumeRequested;
                item.MuteChangeRequested += OnSessionMuteRequested;
                Sessions.Insert(index, item);
            }

            index++;
        }

        for (int i = Sessions.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Sessions[i].Id))
            {
                var removed = Sessions[i];
                removed.VolumeChangeRequested -= OnSessionVolumeRequested;
                removed.MuteChangeRequested -= OnSessionMuteRequested;
                Sessions.RemoveAt(i);
            }
        }

        SessionCount = Sessions.Count;
        SessionSummary = SessionCount switch
        {
            0 => "No apps are playing sound",
            1 => "1 app in the mixer",
            var n => $"{n} apps in the mixer",
        };
    }

    /// <summary>Keeps existing items (so sliders being dragged are not recreated) and applies the new order.</summary>
    private void Merge(ObservableCollection<AudioDeviceItem> target, IEnumerable<AudioDevice> devices)
    {
        var byId = target.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var device in devices)
        {
            keep.Add(device.Id);
            if (byId.TryGetValue(device.Id, out var existing))
            {
                existing.Update(device);
                int current = target.IndexOf(existing);
                if (current != index)
                {
                    target.Move(current, index);
                }
            }
            else
            {
                var item = new AudioDeviceItem(device);
                item.VolumeChangeRequested += OnDeviceVolumeRequested;
                item.MuteChangeRequested += OnDeviceMuteRequested;
                target.Insert(index, item);
            }

            index++;
        }

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i].Id))
            {
                var removed = target[i];
                removed.VolumeChangeRequested -= OnDeviceVolumeRequested;
                removed.MuteChangeRequested -= OnDeviceMuteRequested;
                target.RemoveAt(i);
            }
        }
    }

    private void OnDeviceVolumeRequested(AudioDeviceItem item, float percent) =>
        Debounce("device:" + item.Id, ct => _audio.SetVolumeAsync(item.Id, percent, ct));

    private void OnDeviceMuteRequested(AudioDeviceItem item, bool muted) =>
        _ = ApplyAsync(ct => _audio.SetMuteAsync(item.Id, muted, ct), item.Name);

    private void OnSessionVolumeRequested(AudioSessionItem item, float percent) =>
        Debounce("session:" + item.Id, ct => _audio.SetSessionVolumeAsync(item.Id, percent, ct));

    private void OnSessionMuteRequested(AudioSessionItem item, bool muted) =>
        _ = ApplyAsync(ct => _audio.SetSessionMuteAsync(item.Id, muted, ct), item.DisplayName);

    /// <summary>Waits a moment after the last slider movement, then sends only the latest value.</summary>
    private void Debounce(string key, Func<CancellationToken, Task<OperationResult>> apply)
    {
        if (_pendingVolume.TryGetValue(key, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingVolume[key] = cts;
        _ = DebouncedAsync(key, cts, apply);
    }

    private async Task DebouncedAsync(string key, CancellationTokenSource cts, Func<CancellationToken, Task<OperationResult>> apply)
    {
        try
        {
            await Task.Delay(VolumeDebounce, cts.Token);
            var result = await apply(CancellationToken.None);
            if (!result.Success)
            {
                _notifications.Warning("Volume", result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer slider value.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Volume change failed.");
        }
        finally
        {
            if (_pendingVolume.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
            {
                _pendingVolume.Remove(key);
                cts.Dispose();
            }
        }
    }

    private async Task ApplyAsync(Func<CancellationToken, Task<OperationResult>> apply, string target)
    {
        try
        {
            var result = await apply(CancellationToken.None);
            if (!result.Success)
            {
                _notifications.Warning(target, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio change for {Target} failed.", target);
            _notifications.Error(target, ex.Message);
        }
    }

    private async Task LoadDevicesQuietlyAsync()
    {
        try
        {
            await LoadDevicesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device list could not be refreshed.");
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        DispatcherHelper.PostToUi(async () =>
        {
            if (_refreshQueued || IsBusy)
            {
                return;
            }

            _refreshQueued = true;
            try
            {
                await LoadDevicesQuietlyAsync();
                await LoadSessionsQuietlyAsync();
                LastRefreshedText = "Updated " + DateTime.Now.ToString("HH:mm:ss") + " (device change)";
            }
            finally
            {
                _refreshQueued = false;
            }
        });
    }

    private async Task LoadSessionsQuietlyAsync()
    {
        try
        {
            await LoadSessionsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session list could not be refreshed.");
        }
    }

    private async void OnMeterTick(object? sender, EventArgs e)
    {
        if (_meterReadInFlight || _audio is not IAudioLevelMeter meter)
        {
            return;
        }

        _meterReadInFlight = true;
        try
        {
            var levels = await meter.ReadPeakLevelsAsync(CancellationToken.None);
            foreach (var item in OutputDevices)
            {
                item.SetPeak(levels.TryGetValue(item.Id, out var level) ? level : null);
            }

            foreach (var item in InputDevices)
            {
                item.SetPeak(levels.TryGetValue(item.Id, out var level) ? level : null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Level meter read failed.");
        }
        finally
        {
            _meterReadInFlight = false;
        }
    }

    private void Report(OperationResult result, string title, bool quietOnSuccess = false)
    {
        if (result.Success)
        {
            if (!quietOnSuccess)
            {
                _notifications.Success(title, result.Message);
            }
        }
        else
        {
            _notifications.Error(title, result.Message);
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Audio page operation failed.");
}
