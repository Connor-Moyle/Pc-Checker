using System.Globalization;
using System.Text;

namespace PcChecker.Core.Processes;

/// <summary>
/// Processor affinity masks: bit n set means the program may run on logical processor n. Windows keeps one 64-bit
/// mask per processor group; PC Checker offers affinity on PCs with up to 64 logical processors (one group).
/// </summary>
public static class AffinityMath
{
    /// <summary>The most logical processors one mask can describe.</summary>
    public const int MaxProcessors = 64;

    /// <summary>A mask with the first <paramref name="processorCount"/> bits set.</summary>
    public static ulong FullMask(int processorCount)
    {
        if (processorCount <= 0)
        {
            return 0;
        }

        return processorCount >= MaxProcessors ? ulong.MaxValue : (1UL << processorCount) - 1;
    }

    /// <summary>The mask for the given processor numbers; numbers outside 0–63 are ignored.</summary>
    public static ulong ToMask(IEnumerable<int> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        ulong mask = 0;
        foreach (int index in processors)
        {
            if (index is >= 0 and < MaxProcessors)
            {
                mask |= 1UL << index;
            }
        }

        return mask;
    }

    /// <summary>The processor numbers in <paramref name="mask"/>, limited to the PC's <paramref name="processorCount"/>.</summary>
    public static IReadOnlyList<int> Processors(ulong mask, int processorCount)
    {
        var list = new List<int>();
        for (int i = 0; i < Math.Min(processorCount, MaxProcessors); i++)
        {
            if ((mask & (1UL << i)) != 0)
            {
                list.Add(i);
            }
        }

        return list;
    }

    /// <summary>Why <paramref name="mask"/> cannot be applied, or null when it is fine.</summary>
    public static string? Validate(ulong mask, int processorCount)
    {
        if (processorCount > MaxProcessors)
        {
            return "This PC has more than 64 logical processors. Windows splits them into groups, and PC Checker only sets affinity on PCs with one group.";
        }

        if ((mask & ~FullMask(processorCount)) != 0)
        {
            return "The selection includes processors this PC does not have.";
        }

        return mask == 0 ? "Pick at least one processor, or the program could not run at all." : null;
    }

    /// <summary>"All 16 processors", "CPU 0–3, 8" (ranges collapsed).</summary>
    public static string Describe(ulong mask, int processorCount)
    {
        var processors = Processors(mask, processorCount);
        if (processors.Count == 0)
        {
            return "No processors";
        }

        if (processors.Count == Math.Min(processorCount, MaxProcessors))
        {
            return string.Create(CultureInfo.InvariantCulture, $"All {processors.Count} processors");
        }

        var text = new StringBuilder("CPU ");
        for (int i = 0; i < processors.Count;)
        {
            int start = processors[i];
            int end = start;
            while (i + 1 < processors.Count && processors[i + 1] == end + 1)
            {
                end = processors[++i];
            }

            if (text.Length > 4)
            {
                text.Append(", ");
            }

            text.Append(start.ToString(CultureInfo.InvariantCulture));
            if (end > start)
            {
                text.Append('–').Append(end.ToString(CultureInfo.InvariantCulture));
            }

            i++;
        }

        return text.ToString();
    }
}
