using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PcChecker.Core.Common;

namespace PcChecker.Core.DeviceTests;

/// <summary>
/// Speaker and microphone tests on NAudio's WASAPI wrappers, the same Core Audio layer the Audio page uses. Sounds are
/// generated in memory in the output's own mix format (sample rate and channel count) and played in shared mode, so
/// Windows does no conversion and the left and right channels stay exactly where they belong. The microphone is read in
/// shared mode too; a recording stays in memory.
/// </summary>
public sealed class WindowsSoundTestService : ISoundTestService
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int PlaybackLatencyMs = 100;

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: the sub-format of a WAVEFORMATEXTENSIBLE float stream.
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly ILogger<WindowsSoundTestService> _logger;

    public WindowsSoundTestService(ILogger<WindowsSoundTestService> logger)
    {
        _logger = logger;
    }

    public Task<OperationResult> PlayTestAsync(SoundTest test, CancellationToken cancellationToken) =>
        PlayOnDefaultOutputAsync(format => TestSignalGenerator.ForTest(test, format.SampleRate, format.Channels), cancellationToken);

    public Task<OperationResult> PlayRecordingAsync(RecordedAudio audio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return PlayOnDefaultOutputAsync(format =>
        {
            var mono = AudioLevelMath.Resample(audio.Samples, audio.SampleRate, format.SampleRate);
            return TestSignalGenerator.Spread(mono, format.Channels, ToneChannel.Both);
        }, cancellationToken);
    }

    public Task<OperationResult<RecordedAudio>> ListenAsync(TimeSpan? recordFor, IProgress<MicrophoneLevel>? levels, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult<RecordedAudio>.Fail("The microphone test only works on Windows."));
        }

        return Task.Run(async () =>
        {
            MMDevice? device = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                try
                {
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "No default microphone.");
                    return OperationResult<RecordedAudio>.Fail("No microphone is set as the default recording device.");
                }

                return await CaptureAsync(device, recordFor, levels, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                _logger.LogInformation(ex, "Windows blocked microphone access.");
                return OperationResult<RecordedAudio>.Fail("Windows blocked access to the microphone.", new UnauthorizedAccessException(ex.Message, ex));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Microphone test failed.");
                return OperationResult<RecordedAudio>.Fail("The microphone could not be opened: " + ex.Message, ex);
            }
            finally
            {
                device?.Dispose();
            }
        }, cancellationToken);
    }

    private async Task<OperationResult<RecordedAudio>> CaptureAsync(MMDevice device, TimeSpan? recordFor, IProgress<MicrophoneLevel>? levels, CancellationToken cancellationToken)
    {
        using var capture = new WasapiCapture(device, false, 50);
        var waveFormat = capture.WaveFormat;
        var format = new PcmFormat(waveFormat.SampleRate, waveFormat.Channels, waveFormat.BitsPerSample, IsFloat(waveFormat));
        int target = recordFor is { } span ? TestSignalGenerator.FrameCount(span.TotalSeconds, format.SampleRate) : 0;
        var recorded = new List<float>(Math.Max(0, target));
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();

        capture.DataAvailable += (_, e) =>
        {
            var mono = AudioLevelMath.ToMono(e.Buffer.AsSpan(0, e.BytesRecorded), format);
            levels?.Report(AudioLevelMath.Measure(mono));
            if (target <= 0)
            {
                return;
            }

            bool full;
            lock (gate)
            {
                int take = Math.Min(mono.Length, target - recorded.Count);
                recorded.AddRange(mono.AsSpan(0, take).ToArray());
                full = recorded.Count >= target;
            }

            if (full)
            {
                try
                {
                    capture.StopRecording();
                }
                catch (Exception ex)
                {
                    stopped.TrySetResult(ex);
                }
            }
        };
        capture.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);

        capture.StartRecording();
        Exception? error;
        using (cancellationToken.Register(() =>
        {
            try
            {
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                stopped.TrySetResult(ex);
            }
        }))
        {
            error = await stopped.Task.ConfigureAwait(false);
        }

        if (error is not null)
        {
            if (IsAccessDenied(error))
            {
                return OperationResult<RecordedAudio>.Fail("Windows blocked access to the microphone.", new UnauthorizedAccessException(error.Message, error));
            }

            return OperationResult<RecordedAudio>.Fail("The microphone stopped: " + error.Message, error);
        }

        if (target <= 0 || cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new OperationResult<RecordedAudio>(true, "Stopped.", null);
        }

        float[] samples;
        lock (gate)
        {
            samples = recorded.ToArray();
        }

        return OperationResult<RecordedAudio>.Ok(new RecordedAudio(samples, format.SampleRate), "Recorded.");
    }

    private Task<OperationResult> PlayOnDefaultOutputAsync(Func<PcmFormat, float[]> makeSamples, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("The speaker test only works on Windows."));
        }

        return Task.Run(async () =>
        {
            MMDevice? device = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                try
                {
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "No default output device.");
                    return OperationResult.Fail("No speakers or headphones are set as the default output.");
                }

                WaveFormat mix;
                using (var client = device.AudioClient)
                {
                    mix = client.MixFormat;
                }

                var format = new PcmFormat(mix.SampleRate, Math.Max(1, mix.Channels), 32, true);
                var samples = makeSamples(format);
                var wav = WavFile.Create(samples, format.SampleRate, format.Channels, WavSampleFormat.Float32);

                using var reader = new WaveFileReader(new MemoryStream(wav, writable: false));
                using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, PlaybackLatencyMs);
                var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                output.PlaybackStopped += (_, e) => done.TrySetResult(e.Exception);
                output.Init(reader);
                output.Play();
                Exception? error;
                using (cancellationToken.Register(() =>
                {
                    try
                    {
                        output.Stop();
                    }
                    catch (Exception ex)
                    {
                        done.TrySetResult(ex);
                    }
                }))
                {
                    error = await done.Task.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return error is null ? OperationResult.Ok("Played.") : OperationResult.Fail("Playback stopped: " + error.Message, error);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Test sound could not be played.");
                return OperationResult.Fail("The test sound could not be played: " + ex.Message, ex);
            }
            finally
            {
                device?.Dispose();
            }
        }, cancellationToken);
    }

    private static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat
        || (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubtype);

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is COMException com && com.HResult == AccessDenied) || ex.HResult == AccessDenied;
}
