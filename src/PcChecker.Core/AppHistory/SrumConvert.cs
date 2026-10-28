using System.Buffers.Binary;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// Turns SRUM's raw column values into figures. Columns are read as raw bytes and decoded here, so a column that is a
/// 4-byte number on one Windows version and an 8-byte number on another still reads correctly.
/// </summary>
public static class SrumConvert
{
    /// <summary>The processor speed assumed when Windows does not report one (3 GHz).</summary>
    public const double FallbackMhz = 3000;

    /// <summary>Interface types (IANA ifType) of mobile broadband adapters, which Windows treats as metered.</summary>
    private static readonly HashSet<int> MobileBroadbandTypes = [243, 244];

    /// <summary>A little-endian integer of 1, 2, 4 or 8 bytes (1-byte values are unsigned), or null for any other length.</summary>
    public static long? ReadInteger(byte[]? value) => value?.Length switch
    {
        1 => value[0],
        2 => BinaryPrimitives.ReadInt16LittleEndian(value),
        4 => BinaryPrimitives.ReadInt32LittleEndian(value),
        8 => BinaryPrimitives.ReadInt64LittleEndian(value),
        _ => null,
    };

    /// <summary>An ESE date-time column: an 8-byte OLE Automation date in UTC.</summary>
    public static DateTimeOffset? ReadOleDate(byte[]? value) =>
        value is { Length: 8 } ? FromOleDate(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(value))) : null;

    /// <summary>
    /// An OLE Automation date (days since 30 December 1899, the fraction being the time of day) as a UTC moment. SRUM
    /// writes its timestamps in UTC. Null for zero, NaN and values outside the range .NET accepts.
    /// </summary>
    public static DateTimeOffset? FromOleDate(double oleDate)
    {
        // DateTime.FromOADate accepts -657435.0 (year 100) to 2958465.99999999 (year 9999).
        if (double.IsNaN(oleDate) || double.IsInfinity(oleDate) || oleDate == 0 || oleDate <= -657435.0 || oleDate >= 2958466.0)
        {
            return null;
        }

        try
        {
            var time = DateTime.SpecifyKind(DateTime.FromOADate(oleDate), DateTimeKind.Utc);
            return new DateTimeOffset(time);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The processor speed to divide cycle counts by: the speed Windows reports (registry <c>~MHz</c>) when it is
    /// plausible, otherwise <see cref="FallbackMhz"/>.
    /// </summary>
    public static double EffectiveMhz(double? reportedMhz) =>
        reportedMhz is { } mhz && !double.IsNaN(mhz) && mhz >= 100 && mhz <= 20_000 ? mhz : FallbackMhz;

    /// <summary>
    /// Converts processor cycles to processor time. SRUM counts the cycles an app used on all cores together; dividing
    /// by the processor's base speed gives the time one core would have needed, which is how Task Manager's App
    /// history shows it. It is an approximation: turbo, power saving and cores of different speeds make real clocks
    /// differ from the base speed, so the result can be a little high or low.
    /// </summary>
    public static TimeSpan CyclesToCpuTime(long cycles, double mhz)
    {
        if (cycles <= 0)
        {
            return TimeSpan.Zero;
        }

        double seconds = cycles / (EffectiveMhz(mhz) * 1_000_000d);
        return seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>The interface type (IANA ifType: 6 Ethernet, 71 Wi-Fi, 243 and 244 mobile broadband) inside a NET_LUID.</summary>
    public static int InterfaceType(long luid) => (int)(((ulong)luid >> 48) & 0xFFFF);

    /// <summary>
    /// True when data over this interface counts as metered. SRUM does not mark rows as metered, so this is decided by
    /// the kind of adapter: mobile broadband (cellular) is metered; Wi-Fi and Ethernet networks you set as metered in
    /// Settings are not told apart.
    /// </summary>
    public static bool IsMeteredInterface(long luid) => MobileBroadbandTypes.Contains(InterfaceType(luid));
}
