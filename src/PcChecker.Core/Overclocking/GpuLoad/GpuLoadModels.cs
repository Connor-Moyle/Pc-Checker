using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>How hard the built-in GPU load generator works the graphics card.</summary>
public enum GpuLoadIntensity
{
    /// <summary>Small buffer, roughly half-time load: warm, not hot. Good for a first look.</summary>
    Light,

    /// <summary>Medium buffer, near-constant load: like a demanding game.</summary>
    Medium,

    /// <summary>Large buffer, back-to-back work: the hottest, most power-hungry setting for stability testing.</summary>
    Heavy,
}

/// <summary>What to run: which DXGI adapter (negative = the recommended one), how hard, how long, and whether to check results.</summary>
public sealed record GpuLoadOptions(int AdapterIndex, GpuLoadIntensity Intensity, TimeSpan Duration, bool VerifyResults);

/// <summary>Live numbers from a running generator.</summary>
public sealed record GpuLoadProgress(TimeSpan Elapsed, double DispatchesPerSecond, long ErrorsSoFar)
{
    /// <summary>Iterations per element in use when this was reported (0 when the generator does not say).</summary>
    public int Iterations { get; init; }
}

/// <summary>
/// Outcome of a GPU load run. <see cref="Completed"/> is true when the run ended without a fault (it reached its
/// duration or the caller stopped it); false means it could not start or the GPU failed (see <see cref="FailureReason"/>).
/// </summary>
public sealed record GpuLoadResult(bool Completed, long Dispatches, long Errors, string AdapterName, string? FailureReason, TimeSpan Elapsed)
{
    /// <summary>True when the graphics driver reset (device removed, hung or reset) during the run.</summary>
    public bool DriverReset { get; init; }

    /// <summary>True when the load never started (no adapter, no DirectX 11, shader compile failure).</summary>
    public bool NotStarted { get; init; }

    /// <summary>Iterations per element the calibration settled on (0 when it never ran).</summary>
    public int Iterations { get; init; }

    /// <summary>Size of the work buffer in video memory.</summary>
    public long BufferBytes { get; init; }

    public static GpuLoadResult CouldNotStart(string adapterName, string reason) =>
        new(false, 0, 0, adapterName, reason, TimeSpan.Zero) { NotStarted = true };
}

/// <summary>Raw facts about one DXGI adapter, as enumerated (pure record, no DirectX types).</summary>
public sealed record GpuAdapterInfo(int Index, string Name, uint VendorId, uint DeviceId, long DedicatedVideoMemoryBytes, long SharedSystemMemoryBytes, bool IsSoftware);

/// <summary>A graphics adapter the user can pick for the GPU stress test.</summary>
public sealed record GpuAdapterChoice(int Index, string Name, string Vendor, long DedicatedVideoMemoryBytes, bool IsRecommended)
{
    /// <summary>"NVIDIA GeForce RTX 4070 · 12 GB" (and "recommended" for the default pick).</summary>
    public string DisplayName =>
        $"{Name} · {MemoryText(DedicatedVideoMemoryBytes)}{(IsRecommended ? " (recommended)" : string.Empty)}";

    public override string ToString() => DisplayName;

    /// <summary>"12 GB", "1.5 GB", "128 MB" or "shared memory" for adapters without their own memory.</summary>
    public static string MemoryText(long bytes)
    {
        if (bytes <= 0)
        {
            return "shared memory";
        }

        return bytes >= ByteSize.Gigabyte
            ? (bytes / (double)ByteSize.Gigabyte).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB"
            : Math.Max(1, bytes / ByteSize.Megabyte).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";
    }
}
