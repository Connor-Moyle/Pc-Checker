using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.DeviceTests;

namespace PcChecker.App.ViewModels;

/// <summary>The speaker and microphone tests.</summary>
public sealed partial class DeviceTestsViewModel
{
    private static readonly TimeSpan RecordLength = TimeSpan.FromSeconds(5);
    private const int HintWindow = 30;
    private const double LoudVolumePercent = 60;

    private readonly List<MicrophoneLevel> _recentLevels = [];
    private CancellationTokenSource? _soundCts;
    private CancellationTokenSource? _micCts;
    private RecordedAudio? _recording;
    private bool _volumeWarningShown;
    private int _micSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotPlaying))]
    [NotifyCanExecuteChangedFor(nameof(PlaySoundCommand), nameof(StopSoundCommand), nameof(PlayRecordingCommand))]
    private bool _isPlaying;

    [ObservableProperty] private string _soundStatus = Loc.T("Pick a test. Each tone plays for 2 seconds; the sweep for 8.");
    [ObservableProperty] private float? _outputVolume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenButtonText))]
    private bool _isListening;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordCommand), nameof(PlayRecordingCommand), nameof(ToggleListeningCommand))]
    private bool _isRecording;

    [ObservableProperty] private double _micLevelPercent;
    [ObservableProperty] private string _micLevelText = "—";
    [ObservableProperty] private string _micHintText = Loc.T("Press Listen and speak normally.");
    [ObservableProperty] private bool _micHintIsWarning;
    [ObservableProperty] private bool _micAccessDenied;
    [ObservableProperty] private string? _micStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayRecordingCommand))]
    private bool _hasRecording;

    public bool IsNotPlaying => !IsPlaying;

    public string ListenButtonText => IsListening ? Loc.T("Stop listening") : Loc.T("Listen");

    // ----- Speakers -----

    private bool CanPlay() => !IsPlaying;

    /// <summary>Plays "left", "right", "both" or "sweep" after the one-time volume warning.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlaySoundAsync(string? which)
    {
        var test = which switch
        {
            "left" => SoundTest.Left,
            "right" => SoundTest.Right,
            "sweep" => SoundTest.Sweep,
            _ => SoundTest.Both,
        };

        if (!await ConfirmVolumeAsync())
        {
            return;
        }

        SoundStatus = test switch
        {
            SoundTest.Left => Loc.T("Playing on the LEFT speaker only…"),
            SoundTest.Right => Loc.T("Playing on the RIGHT speaker only…"),
            SoundTest.Both => Loc.T("Playing on both speakers…"),
            _ => Loc.T("Sweeping from a low hum to a high whistle… Listen for rattles or a range that goes silent."),
        };

        var outcome = await RunSoundAsync(ct => _sound.PlayTestAsync(test, ct));
        SoundStatus = outcome ?? (test switch
        {
            SoundTest.Left => Loc.T("Did you hear it on the left only? If it came from the right, the channels are swapped."),
            SoundTest.Right => Loc.T("Did you hear it on the right only? If it came from the left, the channels are swapped."),
            SoundTest.Both => Loc.T("Both sides should have sounded equally loud."),
            _ => Loc.T("Most speakers go quiet at the very low and very high ends; that is normal."),
        });
    }

    [RelayCommand(CanExecute = nameof(IsPlaying))]
    private void StopSound()
    {
        try
        {
            _soundCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<bool> ConfirmVolumeAsync()
    {
        if (_volumeWarningShown)
        {
            return true;
        }

        string volume = OutputVolume is { } v
            ? Loc.F("The output volume is {0:0} %.", v) + " " + (v > LoudVolumePercent ? Loc.T("That is quite loud for headphones.") : string.Empty)
            : string.Empty;
        bool ok = await _dialogs.ConfirmAsync(
            Loc.T("Turn the volume down first"),
            Loc.T("The test sounds play at a moderate level, but they can still be loud in headphones. Set the volume to about a quarter, then turn it up if you need to.") + "\n\n" + volume.Trim(),
            Loc.T("Play"));
        _volumeWarningShown = ok;
        return ok;
    }

    /// <summary>Runs a playback with the shared Stop button; returns an error text, or null when it played.</summary>
    private async Task<string?> RunSoundAsync(Func<CancellationToken, Task<Core.Common.OperationResult>> play)
    {
        _soundCts?.Dispose();
        _soundCts = new CancellationTokenSource();
        IsPlaying = true;
        try
        {
            var result = await play(_soundCts.Token);
            return result.Success ? null : Loc.T(result.Message);
        }
        catch (OperationCanceledException)
        {
            return Loc.T("Stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sound test failed.");
            return ex.Message;
        }
        finally
        {
            IsPlaying = false;
        }
    }

    // ----- Microphone -----

    // The same command stops the meter, so it must stay enabled while the listen task is still running.
    [RelayCommand(CanExecute = nameof(IsNotRecording), AllowConcurrentExecutions = true)]
    private async Task ToggleListeningAsync()
    {
        if (IsListening)
        {
            StopListening();
            return;
        }

        int session = BeginMicSession();
        MicStatus = null;
        try
        {
            var result = await _sound.ListenAsync(null, LevelReporter(session), _micCts!.Token);
            if (session == _micSession)
            {
                HandleMicResult(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Microphone meter failed.");
            MicStatus = ex.Message;
        }
        finally
        {
            EndMicSession(session);
        }
    }

    private bool IsNotRecording() => !IsRecording;

    private bool CanRecord() => !IsRecording;

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private async Task RecordAsync()
    {
        StopListening();
        int session = BeginMicSession();
        IsRecording = true;
        MicStatus = Loc.T("Recording for 5 seconds… say a few words.");
        try
        {
            var result = await _sound.ListenAsync(RecordLength, LevelReporter(session), _micCts!.Token);
            if (HandleMicResult(result) && result.Value is { } audio)
            {
                _recording = audio;
                HasRecording = true;
                MicStatus = Loc.T("Recorded. Press Play back to hear it. The recording stays in memory and is never saved.");
            }
        }
        catch (OperationCanceledException)
        {
            MicStatus = Loc.T("Recording stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Microphone recording failed.");
            MicStatus = ex.Message;
        }
        finally
        {
            IsRecording = false;
            EndMicSession(session);
        }
    }

    /// <summary>Starts a microphone session; an older session that is still winding down no longer touches the meter.</summary>
    private int BeginMicSession()
    {
        _micCts?.Dispose();
        _micCts = new CancellationTokenSource();
        _recentLevels.Clear();
        MicAccessDenied = false;
        IsListening = true;
        return ++_micSession;
    }

    private void EndMicSession(int session)
    {
        if (session != _micSession)
        {
            return;
        }

        IsListening = false;
        ResetMeter();
    }

    private IProgress<MicrophoneLevel> LevelReporter(int session) => new Progress<MicrophoneLevel>(level =>
    {
        if (session == _micSession)
        {
            ShowLevel(level);
        }
    });

    private bool CanPlayRecording() => HasRecording && !IsPlaying && !IsRecording;

    [RelayCommand(CanExecute = nameof(CanPlayRecording))]
    private async Task PlayRecordingAsync()
    {
        if (_recording is not { } audio)
        {
            return;
        }

        MicStatus = Loc.T("Playing the recording…");
        MicStatus = await RunSoundAsync(ct => _sound.PlayRecordingAsync(audio, ct)) ?? Loc.T("Finished. If it sounded muffled or crackly, try another USB port or cable.");
    }

    private bool HandleMicResult(Core.Common.OperationResult<RecordedAudio> result)
    {
        if (result.Success)
        {
            return true;
        }

        MicAccessDenied = result.Error is UnauthorizedAccessException;
        MicStatus = MicAccessDenied
            ? Loc.T("Windows blocked microphone access for desktop apps. Turn on \"Let desktop apps access your microphone\" in privacy settings.")
            : Loc.T(result.Message);
        return false;
    }

    private void ShowLevel(MicrophoneLevel level)
    {
        if (!IsListening)
        {
            return;
        }

        _recentLevels.Add(level);
        if (_recentLevels.Count > HintWindow)
        {
            _recentLevels.RemoveAt(0);
        }

        MicLevelPercent = AudioLevelMath.MeterPercent(level.PeakDb);
        MicLevelText = Loc.F("{0:0} dB", level.PeakDb);
        var hint = _recentLevels.Count < 10 ? MicrophoneHint.Waiting : AudioLevelMath.Hint(_recentLevels);
        MicHintIsWarning = hint is MicrophoneHint.NoSignal or MicrophoneHint.Clipping;
        MicHintText = hint switch
        {
            MicrophoneHint.NoSignal => Loc.T("No signal. Check the mute switch on the headset, the cable, and the microphone privacy settings."),
            MicrophoneHint.Quiet => Loc.T("Very quiet. Move closer or raise the input volume on the Audio page."),
            MicrophoneHint.Good => Loc.T("Good level. The bar should jump when you speak and drop when you stop."),
            MicrophoneHint.Clipping => Loc.T("Too loud: the sound is clipping (distorting). Lower the input volume or move back a little."),
            _ => Loc.T("Listening… speak normally."),
        };
    }

    private void ResetMeter()
    {
        MicLevelPercent = 0;
        MicLevelText = "—";
        if (!MicAccessDenied && MicStatus is null)
        {
            MicHintText = Loc.T("Press Listen and speak normally.");
            MicHintIsWarning = false;
        }
    }

    private void StopListening()
    {
        try
        {
            _micCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
