using System.Buffers.Binary;
using System.Text;

namespace PcChecker.Core.DeviceTests;

/// <summary>Sample formats <see cref="WavFile"/> can write.</summary>
public enum WavSampleFormat
{
    /// <summary>16-bit signed integers (format tag 1).</summary>
    Pcm16,

    /// <summary>32-bit IEEE floats (format tag 3), the format Windows mixes in.</summary>
    Float32,
}

/// <summary>Builds a complete WAV file in memory (44-byte RIFF header plus samples). Nothing is written to disk.</summary>
public static class WavFile
{
    public const int HeaderSize = 44;

    /// <summary>Wraps interleaved float samples (-1..1) in a WAV file of the chosen sample format.</summary>
    public static byte[] Create(ReadOnlySpan<float> interleaved, int sampleRate, int channels, WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        int bytesPerSample = format == WavSampleFormat.Float32 ? 4 : 2;
        int dataBytes = interleaved.Length * bytesPerSample;
        var bytes = new byte[HeaderSize + dataBytes];
        var span = bytes.AsSpan();

        Encoding.ASCII.GetBytes("RIFF", span[0..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..8], 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE", span[8..12]);
        Encoding.ASCII.GetBytes("fmt ", span[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..22], (short)(format == WavSampleFormat.Float32 ? 3 : 1));
        BinaryPrimitives.WriteInt16LittleEndian(span[22..24], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..28], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..32], sampleRate * channels * bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..34], (short)(channels * bytesPerSample));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..36], (short)(bytesPerSample * 8));
        Encoding.ASCII.GetBytes("data", span[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..44], dataBytes);

        var data = span[HeaderSize..];
        for (int i = 0; i < interleaved.Length; i++)
        {
            float sample = Math.Clamp(interleaved[i], -1f, 1f);
            if (format == WavSampleFormat.Float32)
            {
                BinaryPrimitives.WriteSingleLittleEndian(data.Slice(i * 4, 4), sample);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(data.Slice(i * 2, 2), ToPcm16(sample));
            }
        }

        return bytes;
    }

    /// <summary>A float sample as a 16-bit integer, rounded and clamped.</summary>
    public static short ToPcm16(float sample) => (short)Math.Clamp(Math.Round(sample * 32767.0), short.MinValue, short.MaxValue);
}
