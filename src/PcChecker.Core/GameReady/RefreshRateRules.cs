using PcChecker.Core.Displays;

namespace PcChecker.Core.GameReady;

/// <summary>
/// Finds the fastest refresh rate a monitor offers at the resolution (and colour depth) it already uses, so the fix
/// never changes how sharp or how colourful the picture is. Pure.
/// </summary>
public static class RefreshRateRules
{
    /// <summary>
    /// Rates closer than this count as the same: Windows lists 59 and 60 Hz, 119 and 120 Hz, 143 and 144 Hz for the
    /// same panel timing (59.94 Hz and friends).
    /// </summary>
    public const int SameRateTolerance = 2;

    /// <summary>
    /// The mode with the highest refresh rate at the current width and height, preferring the current colour depth
    /// (bits per pixel). Null when there is no faster mode than the current one.
    /// </summary>
    public static DisplayMode? BestAtCurrentResolution(DisplayMode current, IEnumerable<DisplayMode> modes)
    {
        ArgumentNullException.ThrowIfNull(current);
        var sameResolution = DisplayModeMath.Normalize(modes ?? [])
            .Where(m => m.Width == current.Width && m.Height == current.Height)
            .ToList();
        if (sameResolution.Count == 0)
        {
            return null;
        }

        var sameDepth = sameResolution.Where(m => m.BitsPerPixel == current.BitsPerPixel).ToList();
        var pool = sameDepth.Count > 0 ? sameDepth : sameResolution.Where(m => m.BitsPerPixel >= current.BitsPerPixel).ToList();
        if (pool.Count == 0)
        {
            return null;
        }

        var best = pool.OrderByDescending(m => m.RefreshHz).ThenByDescending(m => m.BitsPerPixel).First();
        return IsFaster(best.RefreshHz, current.RefreshHz) ? best : null;
    }

    /// <summary>True when <paramref name="candidateHz"/> is really faster than <paramref name="currentHz"/> (not 60 vs 59).</summary>
    public static bool IsFaster(int candidateHz, int currentHz) => candidateHz - currentHz > SameRateTolerance;

    /// <summary>
    /// How much the faster rate matters: High when a gaming panel (100 Hz or more) runs at office speed (75 Hz or less),
    /// Medium for other big steps (165 → 240 Hz), Low for small ones.
    /// </summary>
    public static GameReadyImpact Impact(int currentHz, int bestHz)
    {
        if (!IsFaster(bestHz, currentHz))
        {
            return GameReadyImpact.None;
        }

        if (bestHz >= 100 && currentHz <= 75)
        {
            return GameReadyImpact.High;
        }

        return bestHz - currentHz >= 30 ? GameReadyImpact.Medium : GameReadyImpact.Low;
    }
}
