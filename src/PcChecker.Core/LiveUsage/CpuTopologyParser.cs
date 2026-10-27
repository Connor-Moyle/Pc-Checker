using System.Buffers.Binary;
using System.Numerics;

namespace PcChecker.Core.LiveUsage;

/// <summary>Sockets, cores, logical processors and cache sizes of the processor.</summary>
public sealed record CpuTopology(int Sockets, int Cores, int LogicalProcessors, long L1CacheBytes, long L2CacheBytes, long L3CacheBytes);

/// <summary>
/// Reads the array that <c>GetLogicalProcessorInformation</c> fills (SYSTEM_LOGICAL_PROCESSOR_INFORMATION). Each entry
/// is a processor mask, a relationship and a 16-byte union; for caches the union holds level, associativity, line size,
/// size and type. Every cache appears once even when cores share it, so adding sizes per level gives Task Manager's
/// totals (L1 counts both the instruction and the data cache).
/// </summary>
public static class CpuTopologyParser
{
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int RelationProcessorPackage = 3;

    /// <summary>Size of one entry: 32 bytes on 64-bit Windows, 24 on 32-bit.</summary>
    public static int EntrySize(int pointerSize) => pointerSize == 8 ? 32 : 24;

    /// <summary>Parses <paramref name="buffer"/>; a truncated last entry is ignored.</summary>
    public static CpuTopology Parse(ReadOnlySpan<byte> buffer, int pointerSize)
    {
        if (pointerSize is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(pointerSize));
        }

        int entrySize = EntrySize(pointerSize);
        int unionOffset = pointerSize == 8 ? 16 : 8;
        int sockets = 0, cores = 0, logical = 0;
        long l1 = 0, l2 = 0, l3 = 0;
        for (int offset = 0; offset + entrySize <= buffer.Length; offset += entrySize)
        {
            var entry = buffer.Slice(offset, entrySize);
            ulong mask = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(entry) : BinaryPrimitives.ReadUInt32LittleEndian(entry);
            int relationship = BinaryPrimitives.ReadInt32LittleEndian(entry[pointerSize..]);
            switch (relationship)
            {
                case RelationProcessorCore:
                    cores++;
                    logical += BitOperations.PopCount(mask);
                    break;
                case RelationProcessorPackage:
                    sockets++;
                    break;
                case RelationCache:
                    var cache = entry[unionOffset..];
                    byte level = cache[0];
                    long size = BinaryPrimitives.ReadUInt32LittleEndian(cache[4..]);
                    switch (level)
                    {
                        case 1: l1 += size; break;
                        case 2: l2 += size; break;
                        case 3: l3 += size; break;
                    }

                    break;
            }
        }

        return new CpuTopology(sockets, cores, logical, l1, l2, l3);
    }

    /// <summary>Builds one entry (used by the tests and the demo data to describe a processor).</summary>
    public static byte[] Entry(int pointerSize, ulong mask, int relationship, byte cacheLevel = 0, uint cacheSize = 0, int cacheType = 0)
    {
        var entry = new byte[EntrySize(pointerSize)];
        if (pointerSize == 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(entry, mask);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)mask);
        }

        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(pointerSize), relationship);
        if (relationship == RelationCache)
        {
            int union = pointerSize == 8 ? 16 : 8;
            entry[union] = cacheLevel;
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(union + 4), cacheSize);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(union + 8), cacheType);
        }

        return entry;
    }
}
