namespace PcChecker.Core.Storage;

/// <summary>Downsamples per-block scan states into a fixed number of cells for display (worst state wins).</summary>
public static class BlockMapBuilder
{
    public const int DefaultCells = 2000;

    public static IReadOnlyList<BlockState> Downsample(IReadOnlyList<BlockState> blocks, int cells = DefaultCells)
    {
        if (blocks is null || blocks.Count == 0 || cells <= 0)
        {
            return [];
        }

        if (blocks.Count <= cells)
        {
            return blocks.ToList();
        }

        var result = new BlockState[cells];
        for (int cell = 0; cell < cells; cell++)
        {
            int start = (int)((long)cell * blocks.Count / cells);
            int end = (int)((long)(cell + 1) * blocks.Count / cells);
            var worst = BlockState.Untested;
            bool any = false;
            for (int i = start; i < Math.Max(end, start + 1) && i < blocks.Count; i++)
            {
                any = true;
                if (Rank(blocks[i]) > Rank(worst))
                {
                    worst = blocks[i];
                }
            }

            result[cell] = any ? worst : BlockState.Untested;
        }

        return result;
    }

    /// <summary>Creates an all-<see cref="BlockState.Untested"/> map for a device of the given size.</summary>
    public static BlockState[] CreateEmpty(long sizeBytes, int blockSizeMb)
    {
        long blockBytes = Math.Max(1, blockSizeMb) * 1024L * 1024L;
        long count = Math.Max(1, (sizeBytes + blockBytes - 1) / blockBytes);
        return new BlockState[(int)Math.Min(count, int.MaxValue)];
    }

    /// <summary>
    /// An all-<see cref="BlockState.Untested"/> cell map for a drive of <paramref name="totalBlocks"/> blocks: at most
    /// <paramref name="cells"/> entries, never one per block. Matches the cell count <see cref="BlockMapAccumulator"/> uses.
    /// </summary>
    public static BlockState[] CreateEmptyCells(long totalBlocks, int cells = DefaultCells)
    {
        int count = (int)Math.Min(Math.Max(1, cells), Math.Max(1, totalBlocks));
        return new BlockState[count];
    }

    /// <summary>Ordering used to pick the "worst" state: Bad > Slow > Ok > Untested.</summary>
    public static int Rank(BlockState state) => state switch
    {
        BlockState.Bad => 3,
        BlockState.Slow => 2,
        BlockState.Ok => 1,
        _ => 0,
    };
}
