using System.Buffers.Binary;

namespace PcChecker.Core.Audio;

/// <summary>The parts of a WAVEFORMATEX / WAVEFORMATEXTENSIBLE blob that matter for display.</summary>
public sealed record WaveFormatSummary(int Channels, int SampleRate, int BitsPerSample, bool IsFloat)
{
    /// <summary>"24 bit, 48000 Hz, Stereo" (float formats are shown as "32 bit float").</summary>
    public string ToDisplayText()
    {
        var text = AudioText.BuildFormatText(BitsPerSample, SampleRate, Channels) ?? "Unknown format";
        return IsFloat ? text.Replace(" bit,", " bit float,", StringComparison.Ordinal) : text;
    }
}

/// <summary>
/// Parses the raw <c>PKEY_AudioEngine_DeviceFormat</c> blob (a WAVEFORMATEX, usually WAVEFORMATEXTENSIBLE) without
/// any COM dependency. Layout: wFormatTag(2) nChannels(2) nSamplesPerSec(4) nAvgBytesPerSec(4) nBlockAlign(2)
/// wBitsPerSample(2) cbSize(2) [wValidBitsPerSample(2) dwChannelMask(4) SubFormat(16)].
/// </summary>
public static class WaveFormatParser
{
    private const ushort FormatTagPcm = 0x0001;
    private const ushort FormatTagIeeeFloat = 0x0003;
    private const ushort FormatTagExtensible = 0xFFFE;
    private const int BaseLength = 18;

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = 00000003-0000-0010-8000-00aa00389b71 (first four bytes little-endian).
    private static readonly byte[] IeeeFloatSubtypePrefix = [0x03, 0x00, 0x00, 0x00];

    public static bool TryParse(ReadOnlySpan<byte> blob, out WaveFormatSummary summary)
    {
        summary = new WaveFormatSummary(0, 0, 0, false);
        if (blob.Length < BaseLength - 2) // cbSize may be missing on the oldest PCMWAVEFORMAT layout
        {
            return false;
        }

        ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(blob);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]);
        int sampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]), int.MaxValue);
        int bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(blob[14..]);
        bool isFloat = formatTag == FormatTagIeeeFloat;

        if (formatTag == FormatTagExtensible && blob.Length >= 20)
        {
            int validBits = BinaryPrimitives.ReadUInt16LittleEndian(blob[18..]);
            if (validBits > 0 && validBits <= bitsPerSample)
            {
                bitsPerSample = validBits;
            }

            if (blob.Length >= 40)
            {
                isFloat = blob.Slice(24, 4).SequenceEqual(IeeeFloatSubtypePrefix);
            }
        }
        else if (formatTag != FormatTagPcm && formatTag != FormatTagIeeeFloat)
        {
            // Compressed or unknown tag: still report the rate/channels if they look sane.
            if (channels == 0 || sampleRate == 0)
            {
                return false;
            }
        }

        if (channels <= 0 || channels > 64 || sampleRate <= 0 || sampleRate > 1_000_000)
        {
            return false;
        }

        summary = new WaveFormatSummary(channels, sampleRate, bitsPerSample, isFloat);
        return true;
    }
}
