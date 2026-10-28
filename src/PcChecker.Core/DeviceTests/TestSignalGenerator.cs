namespace PcChecker.Core.DeviceTests;

/// <summary>
/// Makes the speaker test sounds as float samples (-1..1), interleaved for the output's channel count. Channel 0 is
/// the left speaker and channel 1 the right; on surround outputs the other channels stay silent, and a mono output
/// plays every tone. Every sound fades in and out over 15 ms so it starts and stops without a click.
/// </summary>
public static class TestSignalGenerator
{
    /// <summary>About -14 dB below full scale: clearly audible, never painful at a sensible volume.</summary>
    public const double DefaultAmplitude = 0.2;

    public const double ToneFrequencyHz = 440;
    public const double ToneSeconds = 2;
    public const double SweepStartHz = 40;
    public const double SweepEndHz = 16_000;
    public const double SweepSeconds = 8;
    public const double FadeSeconds = 0.015;

    /// <summary>How long a speaker test plays.</summary>
    public static TimeSpan Duration(SoundTest test) => TimeSpan.FromSeconds(test == SoundTest.Sweep ? SweepSeconds : ToneSeconds);

    /// <summary>The samples for one speaker test.</summary>
    public static float[] ForTest(SoundTest test, int sampleRate, int channels) => test switch
    {
        SoundTest.Left => Tone(ToneFrequencyHz, ToneSeconds, sampleRate, channels, ToneChannel.Left),
        SoundTest.Right => Tone(ToneFrequencyHz, ToneSeconds, sampleRate, channels, ToneChannel.Right),
        SoundTest.Both => Tone(ToneFrequencyHz, ToneSeconds, sampleRate, channels, ToneChannel.Both),
        _ => Sweep(SweepStartHz, SweepEndHz, SweepSeconds, sampleRate, channels),
    };

    /// <summary>A sine tone on the chosen speaker(s).</summary>
    public static float[] Tone(double frequencyHz, double seconds, int sampleRate, int channels, ToneChannel which, double amplitude = DefaultAmplitude)
    {
        Validate(sampleRate, channels);
        int frames = FrameCount(seconds, sampleRate);
        var mono = new float[frames];
        double step = 2 * Math.PI * frequencyHz / sampleRate;
        for (int i = 0; i < frames; i++)
        {
            mono[i] = (float)(amplitude * Math.Sin(step * i));
        }

        ApplyFade(mono, (int)(FadeSeconds * sampleRate));
        return Spread(mono, channels, which);
    }

    /// <summary>A logarithmic sweep from <paramref name="startHz"/> to <paramref name="endHz"/> on both speakers.</summary>
    public static float[] Sweep(double startHz, double endHz, double seconds, int sampleRate, int channels, double amplitude = DefaultAmplitude)
    {
        Validate(sampleRate, channels);
        startHz = Math.Max(1, startHz);
        endHz = Math.Clamp(endHz, startHz, sampleRate / 2.0 - 1);
        int frames = FrameCount(seconds, sampleRate);
        var mono = new float[frames];
        double k = Math.Log(endHz / startHz);
        double phase = 0;
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)Math.Max(1, frames - 1);
            double frequency = startHz * Math.Exp(k * t);
            phase += 2 * Math.PI * frequency / sampleRate;
            mono[i] = (float)(amplitude * Math.Sin(phase));
        }

        ApplyFade(mono, (int)(FadeSeconds * sampleRate));
        return Spread(mono, channels, ToneChannel.Both);
    }

    /// <summary>Copies mono samples into an interleaved buffer, only on the channels <paramref name="which"/> selects.</summary>
    public static float[] Spread(ReadOnlySpan<float> mono, int channels, ToneChannel which)
    {
        channels = Math.Max(1, channels);
        var result = new float[mono.Length * channels];
        for (int c = 0; c < channels; c++)
        {
            if (!ChannelPlays(c, channels, which))
            {
                continue;
            }

            for (int i = 0; i < mono.Length; i++)
            {
                result[i * channels + c] = mono[i];
            }
        }

        return result;
    }

    /// <summary>True when channel <paramref name="channel"/> of <paramref name="channels"/> carries a tone meant for <paramref name="which"/>.</summary>
    public static bool ChannelPlays(int channel, int channels, ToneChannel which)
    {
        if (channels <= 1)
        {
            return channel == 0;
        }

        return which switch
        {
            ToneChannel.Left => channel == 0,
            ToneChannel.Right => channel == 1,
            _ => channel is 0 or 1,
        };
    }

    /// <summary>Fades the first and last <paramref name="fadeSamples"/> samples in and out linearly.</summary>
    public static void ApplyFade(Span<float> samples, int fadeSamples)
    {
        fadeSamples = Math.Min(fadeSamples, samples.Length / 2);
        for (int i = 0; i < fadeSamples; i++)
        {
            float gain = i / (float)fadeSamples;
            samples[i] *= gain;
            samples[samples.Length - 1 - i] *= gain;
        }
    }

    public static int FrameCount(double seconds, int sampleRate) => Math.Max(0, (int)Math.Round(seconds * sampleRate));

    private static void Validate(int sampleRate, int channels)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }
    }
}
