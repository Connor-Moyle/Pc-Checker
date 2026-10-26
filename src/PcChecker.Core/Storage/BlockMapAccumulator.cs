namespace PcChecker.Core.Storage;

/// <summary>
/// Keeps a fixed-size cell map up to date while a scan runs, so progress can be reported every block without
/// re-downsampling hundreds of thousands of block states. Each cell shows the worst state of the blocks it covers.
/// </summary>
public sealed class BlockMapAccumulator
{
    private readonly BlockState[] _cells;
    private readonly long _totalBlocks;

    public BlockMapAccumulator(long totalBlocks, int cells = BlockMapBuilder.DefaultCells)
    {
        _totalBlocks = Math.Max(1, totalBlocks);
        int count = (int)Math.Min(Math.Max(1, cells), _totalBlocks);
        _cells = new BlockState[count];
    }

    public int CellCount => _cells.Length;

    /// <summary>Cell index that block <paramref name="blockIndex"/> falls into.</summary>
    public int CellOf(long blockIndex)
    {
        if (blockIndex < 0)
        {
            return 0;
        }

        if (blockIndex >= _totalBlocks)
        {
            return _cells.Length - 1;
        }

        return (int)(blockIndex * _cells.Length / _totalBlocks);
    }

    /// <summary>Records the state of one block; the covering cell keeps its worst state.</summary>
    public void Set(long blockIndex, BlockState state)
    {
        int cell = CellOf(blockIndex);
        if (BlockMapBuilder.Rank(state) > BlockMapBuilder.Rank(_cells[cell]))
        {
            _cells[cell] = state;
        }
    }

    /// <summary>Copy of the current cells.</summary>
    public IReadOnlyList<BlockState> Snapshot() => (BlockState[])_cells.Clone();
}
