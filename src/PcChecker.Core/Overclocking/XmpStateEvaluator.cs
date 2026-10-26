namespace PcChecker.Core.Overclocking;

/// <summary>Result of comparing the configured memory speed with the rated speed.</summary>
public sealed record XmpEvaluation(string State, int? ConfiguredMhz, int? MaxMhz, string Advice)
{
    public const string Enabled = "Enabled";
    public const string Disabled = "Disabled";
    public const string Unknown = "Unknown";
}

/// <summary>Pure XMP/EXPO detection from Win32_PhysicalMemory ConfiguredClockSpeed vs Speed.</summary>
public static class XmpStateEvaluator
{
    /// <summary>Speeds at or below this are JEDEC defaults for DDR5; above means a profile is active or the kit is unusual.</summary>
    public const int Ddr5JedecMaxMhz = 5600;

    public static XmpEvaluation Evaluate(IEnumerable<(int? ConfiguredMhz, int? RatedMhz)> modules, string? memoryType = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        int? configured = null, rated = null;
        foreach (var (c, r) in modules)
        {
            if (c is > 0)
            {
                configured = configured is null ? c : Math.Min(configured.Value, c.Value);
            }

            if (r is > 0)
            {
                rated = rated is null ? r : Math.Max(rated.Value, r.Value);
            }
        }

        string profileName = memoryType is not null && memoryType.Contains("DDR5", StringComparison.OrdinalIgnoreCase) ? "XMP 3.0 / EXPO" : "XMP / DOCP";

        if (configured is null || rated is null)
        {
            return new XmpEvaluation(XmpEvaluation.Unknown, configured, rated,
                "Windows did not report both the configured and the rated memory speed, so the profile state is unknown. Check the BIOS memory page.");
        }

        if (configured < rated - 50)
        {
            return new XmpEvaluation(XmpEvaluation.Disabled, configured, rated,
                $"Your memory is rated for {rated} MT/s but runs at {configured} MT/s. Enable {profileName} in the BIOS (usually under Ai Tweaker, OC or Extreme Memory Profile) to get the speed you paid for. This is the single safest performance gain available.");
        }

        return new XmpEvaluation(XmpEvaluation.Enabled, configured, rated,
            $"Memory runs at its rated {configured} MT/s, so {profileName} is active (or the kit has no faster profile).");
    }
}
