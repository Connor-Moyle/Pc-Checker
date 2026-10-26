namespace PcChecker.Core.Memory;

/// <summary>Fill-and-verify patterns for the user-mode memory test. Pure; operates on spans of 64-bit words.</summary>
public static class MemoryTestPatterns
{
    public const ulong Pattern5 = 0x5555555555555555UL;
    public const ulong PatternA = 0xAAAAAAAAAAAAAAAAUL;

    /// <summary>Names of the patterns in the order the test runs them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["0x55 fill", "0xAA fill", "Walking ones", "Random (seeded)"];

    public static void Fill(Span<ulong> block, int patternIndex, int seed)
    {
        switch (patternIndex)
        {
            case 0:
                block.Fill(Pattern5);
                break;
            case 1:
                block.Fill(PatternA);
                break;
            case 2:
                for (int i = 0; i < block.Length; i++)
                {
                    block[i] = 1UL << (i % 64);
                }

                break;
            default:
                ulong state = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0x1234567UL);
                for (int i = 0; i < block.Length; i++)
                {
                    state = Next(state);
                    block[i] = state;
                }

                break;
        }
    }

    /// <summary>Verifies a block against the pattern. Returns the mismatch count and up to <paramref name="maxOffsets"/> first offsets (in words).</summary>
    public static (long Mismatches, IReadOnlyList<int> FirstOffsets) Verify(ReadOnlySpan<ulong> block, int patternIndex, int seed, int maxOffsets = 8)
    {
        long mismatches = 0;
        var offsets = new List<int>();
        ulong state = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0x1234567UL);
        for (int i = 0; i < block.Length; i++)
        {
            ulong expected;
            switch (patternIndex)
            {
                case 0:
                    expected = Pattern5;
                    break;
                case 1:
                    expected = PatternA;
                    break;
                case 2:
                    expected = 1UL << (i % 64);
                    break;
                default:
                    state = Next(state);
                    expected = state;
                    break;
            }

            if (block[i] != expected)
            {
                mismatches++;
                if (offsets.Count < maxOffsets)
                {
                    offsets.Add(i);
                }
            }
        }

        return (mismatches, offsets);
    }

    /// <summary>xorshift64* step — fast, deterministic and good enough for a fill pattern.</summary>
    public static ulong Next(ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return unchecked(state * 0x2545F4914F6CDD1DUL);
    }
}
