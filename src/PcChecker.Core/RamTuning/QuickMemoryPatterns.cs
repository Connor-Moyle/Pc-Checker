namespace PcChecker.Core.RamTuning;

/// <summary>The patterns the quick memory check cycles through.</summary>
public enum QuickPattern
{
    /// <summary>One set bit walking through every position of a 64-bit word.</summary>
    WalkingOnes,

    /// <summary>One cleared bit walking through every position.</summary>
    WalkingZeros,

    /// <summary>Seeded pseudo-random words, regenerated to verify.</summary>
    Random,

    /// <summary>Moving inversions: fill, then read-check-invert upwards and downwards, then check (catches coupling faults).</summary>
    MovingInversions,
}

/// <summary>
/// Pure fill-and-verify routines for the quick memory check, on spans of 64-bit words, so they can be tested on small
/// buffers with an injected fault. Every routine returns the number of words that did not read back as written.
/// </summary>
public static class QuickMemoryPatterns
{
    public static IReadOnlyList<QuickPattern> All { get; } = [QuickPattern.WalkingOnes, QuickPattern.WalkingZeros, QuickPattern.Random, QuickPattern.MovingInversions];

    /// <summary>
    /// Runs one pattern over <paramref name="block"/>. <paramref name="afterFill"/> runs between the first write and the
    /// first read (self-tests use it to inject a fault).
    /// </summary>
    public static long Run(Span<ulong> block, QuickPattern pattern, ulong seed, SpanAction? afterFill = null)
    {
        switch (pattern)
        {
            case QuickPattern.WalkingOnes:
            case QuickPattern.WalkingZeros:
            case QuickPattern.Random:
                Fill(block, pattern, seed);
                afterFill?.Invoke(block);
                return Verify(block, pattern, seed);
            default:
                return MovingInversions(block, seed, afterFill);
        }
    }

    /// <summary>Writes the pattern.</summary>
    public static void Fill(Span<ulong> block, QuickPattern pattern, ulong seed)
    {
        ulong state = Start(seed);
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = Expected(pattern, i, ref state);
        }
    }

    /// <summary>Counts words that differ from the pattern.</summary>
    public static long Verify(ReadOnlySpan<ulong> block, QuickPattern pattern, ulong seed)
    {
        long errors = 0;
        ulong state = Start(seed);
        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] != Expected(pattern, i, ref state))
            {
                errors++;
            }
        }

        return errors;
    }

    /// <summary>
    /// Moving inversions with the word <c>p</c> derived from the seed: fill p upwards; upwards read p, write ~p;
    /// downwards read ~p, write p; finally read p. A cell that flips, or a write that disturbs a neighbour, shows up as
    /// a read mismatch.
    /// </summary>
    public static long MovingInversions(Span<ulong> block, ulong seed, SpanAction? afterFill = null)
    {
        ulong p = Mix(seed) | 1UL;
        ulong inverse = ~p;
        long errors = 0;
        block.Fill(p);
        afterFill?.Invoke(block);
        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] != p)
            {
                errors++;
            }

            block[i] = inverse;
        }

        for (int i = block.Length - 1; i >= 0; i--)
        {
            if (block[i] != inverse)
            {
                errors++;
            }

            block[i] = p;
        }

        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] != p)
            {
                errors++;
            }
        }

        return errors;
    }

    /// <summary>A span callback (spans cannot be used with <see cref="Action{T}"/>).</summary>
    public delegate void SpanAction(Span<ulong> block);

    private static ulong Start(ulong seed) => Mix(seed) | 1UL;

    private static ulong Expected(QuickPattern pattern, int index, ref ulong state)
    {
        switch (pattern)
        {
            case QuickPattern.WalkingOnes:
                return 1UL << (index & 63);
            case QuickPattern.WalkingZeros:
                return ~(1UL << (index & 63));
            default:
                state = Memory.MemoryTestPatterns.Next(state);
                return state;
        }
    }

    // SplitMix64 finalizer: spreads small seeds over all 64 bits.
    private static ulong Mix(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }
}
