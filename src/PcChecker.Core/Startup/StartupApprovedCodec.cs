namespace PcChecker.Core.Startup;

/// <summary>
/// Encodes and decodes the 12-byte values Windows keeps under
/// <c>Explorer\StartupApproved\{Run,Run32,StartupFolder}</c> to remember whether a startup entry is enabled.
/// Byte 0 is 0x02 (enabled) or 0x03 (disabled); bytes 4–11 hold the FILETIME of when it was disabled.
/// </summary>
public static class StartupApprovedCodec
{
    public const int Length = 12;
    public const byte EnabledFlag = 0x02;
    public const byte DisabledFlag = 0x03;

    /// <summary>Builds the value for an enabled or disabled entry. <paramref name="disabledAt"/> defaults to now (UTC).</summary>
    public static byte[] Encode(bool enabled, DateTime? disabledAt = null)
    {
        var bytes = new byte[Length];
        bytes[0] = enabled ? EnabledFlag : DisabledFlag;
        if (!enabled)
        {
            var when = disabledAt ?? DateTime.UtcNow;
            long fileTime = when.Kind == DateTimeKind.Utc ? when.ToFileTimeUtc() : when.ToUniversalTime().ToFileTimeUtc();
            BitConverter.TryWriteBytes(bytes.AsSpan(4, 8), fileTime);
        }

        return bytes;
    }

    /// <summary>
    /// Reads a stored value. A missing value (null) or anything that is not a recognised flag means "enabled",
    /// which is how Explorer treats entries it has never touched.
    /// </summary>
    public static (bool Enabled, DateTime? DisabledAt) Decode(byte[]? value)
    {
        if (value is null || value.Length == 0)
        {
            return (true, null);
        }

        // Odd first byte (0x03, 0x01, 0x07...) means disabled; even (0x02, 0x06) means enabled.
        bool enabled = (value[0] & 0x01) == 0;
        DateTime? disabledAt = null;
        if (!enabled && value.Length >= Length)
        {
            long fileTime = BitConverter.ToInt64(value, 4);
            if (fileTime > 0)
            {
                try
                {
                    disabledAt = DateTime.FromFileTimeUtc(fileTime);
                }
                catch (ArgumentOutOfRangeException)
                {
                    disabledAt = null;
                }
            }
        }

        return (enabled, disabledAt);
    }

    /// <summary>Shortcut for callers that only need the flag.</summary>
    public static bool IsEnabled(byte[]? value) => Decode(value).Enabled;
}
