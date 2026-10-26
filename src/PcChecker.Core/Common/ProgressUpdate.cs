namespace PcChecker.Core.Common;

/// <summary>Progress message for long-running scans. <paramref name="Percent"/> is 0..100 when known.</summary>
public sealed record ProgressUpdate(string Stage, double? Percent = null, string? Detail = null);
