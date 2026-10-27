namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>Everything the generator needs to know before it starts, derived from the intensity and the card's memory.</summary>
public sealed record GpuLoadPlan(
    GpuLoadIntensity Intensity,
    int InitialIterations,
    long BufferBytes,
    int Elements,
    double TargetDispatchMs,
    double DutyCycle,
    int MaxInFlight,
    int VerifyEveryDispatches,
    int VerifyWindowElements,
    int VerifyWindows);

/// <summary>
/// Pure sizing rules for the GPU load: buffer size by intensity (capped at a quarter of dedicated video memory and
/// 1 GiB), the starting iteration count, how long one dispatch should take, how busy the card is kept, and where
/// the verification windows sit. No DirectX dependency so it is unit-testable anywhere.
/// </summary>
public static class GpuLoadPlanner
{
    /// <summary>One element is a float4 (16 bytes).</summary>
    public const int ElementBytes = 16;

    /// <summary>Must match <c>[numthreads(256, 1, 1)]</c> in <see cref="GpuLoadShader.Source"/>.</summary>
    public const int ThreadsPerGroup = 256;

    /// <summary>Direct3D 11 limit for thread groups in one dispatch dimension.</summary>
    public const int MaxGroupsPerDimension = 65535;

    public const long MinBufferBytes = 16L * 1024 * 1024;
    public const long MaxBufferBytes = 1024L * 1024 * 1024;
    public const double VramFraction = 0.25;

    /// <summary>Elements copied back per verification (1 MiB).</summary>
    public const int VerifyWindowElements = 64 * 1024;

    /// <summary>Distinct windows spread across the buffer; each remembers its own expected values.</summary>
    public const int VerifyWindows = 8;

    /// <summary>Builds the plan for an intensity on a card with <paramref name="dedicatedVramBytes"/> of its own memory.</summary>
    public static GpuLoadPlan Create(GpuLoadIntensity intensity, long dedicatedVramBytes)
    {
        long bytes = BufferBytesFor(intensity, dedicatedVramBytes);
        int elements = (int)(bytes / ElementBytes);
        double targetMs = TargetDispatchMs(intensity);
        double duty = DutyCycle(intensity);
        int window = Math.Min(VerifyWindowElements, elements);

        // Verify about every two seconds of work regardless of how long one dispatch takes.
        int verifyEvery = Math.Max(4, (int)Math.Round(2000 / targetMs));
        return new GpuLoadPlan(intensity, InitialIterations(intensity), bytes, elements, targetMs, duty,
            intensity == GpuLoadIntensity.Heavy ? 2 : 1, verifyEvery, window, VerifyWindows);
    }

    /// <summary>
    /// Requested buffer size for the intensity (64 MiB / 256 MiB / 1 GiB), capped at 25 % of dedicated video memory
    /// and 1 GiB, never below 16 MiB, and rounded down to whole thread groups.
    /// </summary>
    public static long BufferBytesFor(GpuLoadIntensity intensity, long dedicatedVramBytes)
    {
        long requested = intensity switch
        {
            GpuLoadIntensity.Light => 64L * 1024 * 1024,
            GpuLoadIntensity.Medium => 256L * 1024 * 1024,
            _ => MaxBufferBytes,
        };

        long cap = dedicatedVramBytes > 0 ? (long)(dedicatedVramBytes * VramFraction) : MinBufferBytes;
        long bytes = Math.Clamp(Math.Min(requested, cap), MinBufferBytes, MaxBufferBytes);
        long groupBytes = (long)ThreadsPerGroup * ElementBytes;
        return bytes / groupBytes * groupBytes;
    }

    /// <summary>Deliberately small starting points; calibration raises them until one dispatch takes the target time.</summary>
    public static int InitialIterations(GpuLoadIntensity intensity) => intensity switch
    {
        GpuLoadIntensity.Light => 8,
        GpuLoadIntensity.Medium => 16,
        _ => 32,
    };

    /// <summary>How long one dispatch should take: long enough to keep the card busy, far below the 2 s driver timeout.</summary>
    public static double TargetDispatchMs(GpuLoadIntensity intensity) => intensity switch
    {
        GpuLoadIntensity.Light => 50,
        GpuLoadIntensity.Medium => 75,
        _ => 100,
    };

    /// <summary>Share of time the card is kept busy (the rest is idle between dispatches).</summary>
    public static double DutyCycle(GpuLoadIntensity intensity) => intensity switch
    {
        GpuLoadIntensity.Light => 0.5,
        GpuLoadIntensity.Medium => 0.85,
        _ => 1.0,
    };

    /// <summary>Idle time after a dispatch of <paramref name="dispatchMs"/> so the card is busy <paramref name="dutyCycle"/> of the time.</summary>
    public static TimeSpan IdleAfterDispatch(double dispatchMs, double dutyCycle)
    {
        if (dutyCycle >= 1 || dutyCycle <= 0 || dispatchMs <= 0 || double.IsNaN(dispatchMs))
        {
            return TimeSpan.Zero;
        }

        double idle = dispatchMs * (1 - dutyCycle) / dutyCycle;
        return TimeSpan.FromMilliseconds(Math.Min(idle, 1000));
    }

    /// <summary>Thread groups for <paramref name="elements"/>, folded into two dimensions to respect the 65,535 limit.</summary>
    public static (int X, int Y) GroupCounts(int elements)
    {
        if (elements <= 0)
        {
            return (1, 1);
        }

        long groups = (elements + (long)ThreadsPerGroup - 1) / ThreadsPerGroup;
        int x = (int)Math.Min(groups, MaxGroupsPerDimension);
        int y = (int)((groups + x - 1) / x);
        return (x, y);
    }

    /// <summary>First element of verification window <paramref name="verifyIndex"/> (rotating through evenly spread windows).</summary>
    public static int WindowOffset(int verifyIndex, int elements, int windowElements, int windowCount = VerifyWindows)
    {
        if (elements <= windowElements || windowCount <= 1)
        {
            return 0;
        }

        int k = (int)((uint)verifyIndex % (uint)windowCount);
        long span = elements - windowElements;
        long offset = span * k / (windowCount - 1);
        return (int)(offset / 4 * 4);
    }

    /// <summary>Window slot used by verification <paramref name="verifyIndex"/>.</summary>
    public static int WindowSlot(int verifyIndex, int windowCount = VerifyWindows) => windowCount <= 1 ? 0 : (int)((uint)verifyIndex % (uint)windowCount);
}
