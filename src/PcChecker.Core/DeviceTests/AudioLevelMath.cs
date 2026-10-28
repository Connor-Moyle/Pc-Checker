using System.Buffers.Binary;

namespace PcChecker.Core.DeviceTests;

/// <summary>Microphone level maths: decibels, peak and RMS, clipping and the plain hint under the meter.</summary>
public static class AudioLevelMath
{
    /// <summary>The quietest level shown; silence reads as this.</summary>
    public const double FloorDb = -96;

    /// <summary>Samples at or above this (about -0.1 dB) count as clipped.</summary>
    public const double ClipThreshold = 0.989;

    /// <summary>Peaks below this mean nothing is reaching the microphone.</summary>
    public const double NoSignalDb = -60;

    /// <summary>Peaks below this are audible but too quiet for calls.</summary>
    public const double QuietDb = -35;

    /// <summary>Amplitude (0..1) to decibels below full scale, floored at <see cref="FloorDb"/>.</summary>
    public static double ToDb(double amplitude)
    {
        amplitude = Math.Abs(amplitude);
        if (amplitude <= 0 || double.IsNaN(amplitude))
        {
            return FloorDb;
        }

        return Math.Max(FloorDb, 20 * Math.Log10(amplitude));
    }

    /// <summary>A decibel value mapped to 0..100 for a meter that spans <paramref name="rangeDb"/> below full scale.</summary>
    public static double MeterPercent(double db, double rangeDb = 60) =>
        Math.Clamp((db + rangeDb) / rangeDb * 100, 0, 100);

    /// <summary>Peak, RMS and clipped-sample count of one buffer.</summary>
    public static MicrophoneLevel Measure(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return new MicrophoneLevel(FloorDb, FloorDb, 0, 0);
        }

        double peak = 0;
        double sumSquares = 0;
        int clipped = 0;
        foreach (float s in samples)
        {
            double a = Math.Abs(s);
            peak = Math.Max(peak, a);
            sumSquares += a * a;
            if (a >= ClipThreshold)
            {
                clipped++;
            }
        }

        double rms = Math.Sqrt(sumSquares / samples.Length);
        return new MicrophoneLevel(ToDb(peak), ToDb(rms), peak, clipped);
    }

    /// <summary>
    /// The hint under the meter from the last few readings: clipping wins (it ruins a recording), then no signal at
    /// all, then too quiet, otherwise good.
    /// </summary>
    public static MicrophoneHint Hint(IReadOnlyList<MicrophoneLevel> recent)
    {
        if (recent is null || recent.Count == 0)
        {
            return MicrophoneHint.Waiting;
        }

        if (recent.Count(l => l.Clipped) >= 2)
        {
            return MicrophoneHint.Clipping;
        }

        double loudest = recent.Max(l => l.PeakDb);
        return loudest < NoSignalDb ? MicrophoneHint.NoSignal
            : loudest < QuietDb ? MicrophoneHint.Quiet
            : MicrophoneHint.Good;
    }

    /// <summary>
    /// Peak, RMS and clipping of a captured buffer, measured on every channel before they are mixed down, so one
    /// clipping channel is not hidden by averaging it with a quieter one.
    /// </summary>
    public static MicrophoneLevel MeasureCapture(ReadOnlySpan<byte> data, PcmFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int channels = Math.Max(1, format.Channels);
        if (bytesPerSample is not (2 or 3 or 4) || (format.IsFloat && bytesPerSample != 4))
        {
            return Measure([]);
        }

        int count = data.Length / (bytesPerSample * channels) * channels;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (float)ReadSample(data.Slice(i * bytesPerSample, bytesPerSample), format.IsFloat);
        }

        return Measure(samples);
    }

    /// <summary>
    /// Converts a captured buffer to mono floats by averaging the channels. Handles 32-bit float and 16, 24 and 32-bit
    /// integer PCM; a partial frame at the end is dropped.
    /// </summary>
    public static float[] ToMono(ReadOnlySpan<byte> data, PcmFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int channels = Math.Max(1, format.Channels);
        if (bytesPerSample is not (2 or 3 or 4) || (format.IsFloat && bytesPerSample != 4))
        {
            return [];
        }

        int frameBytes = bytesPerSample * channels;
        int frames = data.Length / frameBytes;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                sum += ReadSample(data.Slice(f * frameBytes + c * bytesPerSample, bytesPerSample), format.IsFloat);
            }

            mono[f] = (float)(sum / channels);
        }

        return mono;
    }

    /// <summary>Linear resampling of mono audio, used to play a recording on an output with a different sample rate.</summary>
    public static float[] Resample(float[] mono, int fromRate, int toRate)
    {
        if (mono is null || mono.Length == 0 || fromRate <= 0 || toRate <= 0)
        {
            return [];
        }

        if (fromRate == toRate)
        {
            return (float[])mono.Clone();
        }

        int length = (int)Math.Max(1, Math.Round(mono.LongLength * (double)toRate / fromRate));
        var result = new float[length];
        double ratio = (double)fromRate / toRate;
        for (int i = 0; i < length; i++)
        {
            double position = i * ratio;
            int index = (int)position;
            if (index >= mono.Length - 1)
            {
                result[i] = mono[^1];
                continue;
            }

            double fraction = position - index;
            result[i] = (float)(mono[index] + (mono[index + 1] - mono[index]) * fraction);
        }

        return result;
    }

    private static double ReadSample(ReadOnlySpan<byte> bytes, bool isFloat) => bytes.Length switch
    {
        4 when isFloat => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        4 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0,
        3 => ((bytes[0] | (bytes[1] << 8) | (bytes[2] << 16)) << 8 >> 8) / 8388608.0,
        _ => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
    };
}
