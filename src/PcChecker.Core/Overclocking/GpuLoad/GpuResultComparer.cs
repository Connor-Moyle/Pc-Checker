namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>
/// Pure error counting for GPU results. The shader is deterministic, so the same element must come back bit-for-bit
/// identical on every run; any difference means the card computed or stored a wrong value (what a stress test calls
/// an artifact). Values outside the shader's mathematical range, NaN or infinity are wrong on their own.
/// </summary>
public static class GpuResultComparer
{
    /// <summary>Every value the shader writes stays within ±3 (|acc| ≤ 2 plus a fraction); 4 leaves margin.</summary>
    public const float OutputBound = 4f;

    /// <summary>Counts values that differ bit-for-bit; extra or missing values on either side count as mismatches.</summary>
    public static long CountMismatches(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual)
    {
        int common = Math.Min(expected.Length, actual.Length);
        long mismatches = Math.Abs(expected.Length - actual.Length);
        for (int i = 0; i < common; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
            {
                mismatches++;
            }
        }

        return mismatches;
    }

    /// <summary>Counts NaN, infinity and values outside ±<see cref="OutputBound"/>.</summary>
    public static long CountInvalid(ReadOnlySpan<float> values)
    {
        long invalid = 0;
        foreach (float v in values)
        {
            if (!float.IsFinite(v) || Math.Abs(v) > OutputBound)
            {
                invalid++;
            }
        }

        return invalid;
    }

    /// <summary>
    /// Compares a fresh read-back with the remembered values for its window. The first read of a window becomes the
    /// reference (its invalid values are counted as errors); later reads count mismatches against it.
    /// </summary>
    public static long Check(float[]?[] references, int slot, float[] actual)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(actual);
        if (references[slot] is not { } expected)
        {
            references[slot] = actual;
            return CountInvalid(actual);
        }

        return CountMismatches(expected, actual);
    }
}
