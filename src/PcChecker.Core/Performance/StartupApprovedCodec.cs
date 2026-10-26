namespace PcChecker.Core.Performance;

/// <summary>
/// Encodes and decodes the 12-byte values Windows keeps under
/// <c>...\Explorer\StartupApproved\{Run, Run32, StartupFolder}</c>: the first byte is 0x02 for enabled and 0x03 for
/// disabled (0x06/0x07 for items Windows enabled or disabled itself); bytes 4–11 hold the FILETIME of the moment the
/// item was disabled (zero while enabled). Pure, so it can be unit-tested anywhere.
/// </summary>
public static class StartupApprovedCodec
{
    public const int ValueLength = 12;
    public const byte EnabledFlag = 0x02;
    public const byte DisabledFlag = 0x03;

    /// <summary>True when the value marks the item as enabled, false when disabled, null when empty or unreadable.</summary>
    public static bool? IsEnabled(byte[]? value)
    {
        if (value is null || value.Length == 0)
        {
            return null;
        }

        // Even flags (0x02, 0x06) are enabled, odd flags (0x03, 0x07) are disabled.
        return (value[0] & 0x01) == 0;
    }

    /// <summary>The moment the item was disabled, or null when enabled or no timestamp was written.</summary>
    public static DateTimeOffset? DisabledAt(byte[]? value)
    {
        if (value is null || value.Length < ValueLength || IsEnabled(value) != false)
        {
            return null;
        }

        long fileTime = BitConverter.ToInt64(value, 4);
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Builds the 12-byte value for an enabled item, or a disabled item stamped with <paramref name="disabledAt"/>.</summary>
    public static byte[] Encode(bool enabled, DateTimeOffset? disabledAt = null)
    {
        var bytes = new byte[ValueLength];
        bytes[0] = enabled ? EnabledFlag : DisabledFlag;
        if (!enabled)
        {
            long fileTime = (disabledAt ?? DateTimeOffset.UtcNow).ToFileTime();
            BitConverter.TryWriteBytes(bytes.AsSpan(4, 8), fileTime);
        }

        return bytes;
    }

    /// <summary>Flips an existing value, keeping Windows' own 0x06/0x07 "system" flavour of the flag.</summary>
    public static byte[] Toggle(byte[]? existing, bool enabled, DateTimeOffset? disabledAt = null)
    {
        var bytes = Encode(enabled, disabledAt);
        if (existing is { Length: > 0 } && (existing[0] & 0x04) != 0)
        {
            bytes[0] |= 0x04;
        }

        return bytes;
    }
}
