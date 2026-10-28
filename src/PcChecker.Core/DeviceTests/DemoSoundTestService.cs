using PcChecker.Core.Common;

namespace PcChecker.Core.DeviceTests;

/// <summary>
/// Demo speaker and microphone tests: tones "play" for their real length without making a sound, and the microphone
/// reports a speech-like level that rises and falls around -20 dB. A demo recording is a quiet tone. Pure .NET.
/// </summary>
public sealed class DemoSoundTestService : ISoundTestService
{
    public const int DemoSampleRate = 48_000;
    private const int LevelIntervalMs = 50;

    public async Task<OperationResult> PlayTestAsync(SoundTest test, CancellationToken cancellationToken)
    {
        await Task.Delay(TestSignalGenerator.Duration(test), cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Played (demo).");
    }

    public async Task<OperationResult> PlayRecordingAsync(RecordedAudio audio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        await Task.Delay(audio.Duration, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Played (demo).");
    }

    public async Task<OperationResult<RecordedAudio>> ListenAsync(TimeSpan? recordFor, IProgress<MicrophoneLevel>? levels, CancellationToken cancellationToken)
    {
        var random = DemoData.CreateRandom(77);
        var started = DateTime.UtcNow;
        int tick = 0;
        while (recordFor is null || DateTime.UtcNow - started < recordFor.Value)
        {
            await Task.Delay(LevelIntervalMs, cancellationToken).ConfigureAwait(false);
            levels?.Report(LevelAt(tick++, random));
        }

        int frames = TestSignalGenerator.FrameCount(recordFor!.Value.TotalSeconds, DemoSampleRate);
        var samples = TestSignalGenerator.Tone(220, frames / (double)DemoSampleRate, DemoSampleRate, 1, ToneChannel.Both, 0.1);
        return OperationResult<RecordedAudio>.Ok(new RecordedAudio(samples, DemoSampleRate), "Recorded (demo).");
    }

    /// <summary>A speech-like level: syllables every few hundred milliseconds with short pauses between words.</summary>
    public static MicrophoneLevel LevelAt(int tick, Random random)
    {
        double word = Math.Sin(tick * 0.31) * 0.5 + 0.5;
        double syllable = Math.Abs(Math.Sin(tick * 1.7));
        bool pause = tick % 40 > 32;
        double peak = pause ? 0.002 + random.NextDouble() * 0.002 : 0.04 + 0.22 * word * syllable + random.NextDouble() * 0.02;
        double rms = peak * 0.35;
        return new MicrophoneLevel(AudioLevelMath.ToDb(peak), AudioLevelMath.ToDb(rms), peak, 0);
    }
}
