using PcChecker.Core.Common;

namespace PcChecker.Core.Checkup;

/// <summary>
/// One progress line for a run made of several parts: "Check-up 3 of 8: Drivers — Searching Windows Update (41%)"
/// with an overall percentage, so the page shows a single, steadily moving progress view. Pure.
/// </summary>
public static class CheckupProgress
{
    /// <summary>
    /// The overall percentage (0–100) while part <paramref name="index"/> (0-based) of <paramref name="count"/> is
    /// <paramref name="partPercent"/> done. An unknown part percentage counts as the start of the part.
    /// </summary>
    public static double Overall(int index, int count, double? partPercent)
    {
        if (count <= 0)
        {
            return 0;
        }

        index = Math.Clamp(index, 0, count - 1);
        double part = partPercent is { } p && double.IsFinite(p) ? Math.Clamp(p, 0, 100) / 100.0 : 0;
        return Math.Round(100.0 * (index + part) / count, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>Wraps a part's progress into the combined update for the whole run.</summary>
    public static ProgressUpdate Combine(string runName, int index, int count, string partName, ProgressUpdate? inner)
    {
        var stage = $"{runName} {Math.Clamp(index + 1, 1, Math.Max(1, count))} of {Math.Max(1, count)}: {partName}";
        var detail = inner is null ? null
            : string.IsNullOrWhiteSpace(inner.Detail) ? inner.Stage
            : $"{inner.Stage} — {inner.Detail}";
        return new ProgressUpdate(stage, Overall(index, count, inner?.Percent), detail);
    }
}
