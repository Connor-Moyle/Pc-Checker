using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>
/// Demo GPU load for the sample desktop (RTX 4070 12 GB plus the i7-13700K's UHD Graphics 770): reports realistic
/// dispatch rates for the chosen intensity without touching any hardware. Pure .NET.
/// </summary>
public sealed class DemoGpuLoadGenerator : IGpuLoadGenerator
{
    private static readonly GpuAdapterInfo[] Adapters =
    [
        new(0, DemoData.GpuName, 0x10DE, 0x2786, DemoData.GpuMemoryBytes, 16L * 1024 * 1024 * 1024, false),
        new(1, "Intel(R) UHD Graphics 770", 0x8086, 0xA780, 128L * 1024 * 1024, 16L * 1024 * 1024 * 1024, false),
        new(2, "Microsoft Basic Render Driver", GpuAdapterRanking.MicrosoftVendorId, GpuAdapterRanking.BasicRenderDeviceId, 0, 16L * 1024 * 1024 * 1024, true),
    ];

    private readonly TimeSpan _tick;

    /// <param name="tick">Simulated time step between progress reports (shorter in tests).</param>
    public DemoGpuLoadGenerator(TimeSpan? tick = null)
    {
        _tick = tick ?? TimeSpan.FromMilliseconds(350);
    }

    public IReadOnlyList<GpuAdapterChoice> ListAdapters() => GpuAdapterRanking.Rank(Adapters);

    public async Task<GpuLoadResult> RunAsync(GpuLoadOptions options, IProgress<GpuLoadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var choice = GpuAdapterRanking.Pick(ListAdapters(), options.AdapterIndex);
        if (choice is null)
        {
            return GpuLoadResult.CouldNotStart("GPU", "No DirectX 11 graphics card was found.");
        }

        var plan = GpuLoadPlanner.Create(options.Intensity, choice.DedicatedVideoMemoryBytes);
        double rate = 1000 / plan.TargetDispatchMs * plan.DutyCycle * (choice.IsRecommended ? 1.0 : 0.96);
        var random = DemoData.CreateRandom(4070 + (int)options.Intensity);
        var elapsed = TimeSpan.Zero;
        double dispatches = 0;
        while (elapsed < options.Duration)
        {
            try
            {
                await Task.Delay(_tick, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Demo time runs faster than the clock so a long test still animates briskly.
            var step = TimeSpan.FromSeconds(Math.Max(1, options.Duration.TotalSeconds / 20));
            elapsed = elapsed + step > options.Duration ? options.Duration : elapsed + step;
            double live = DemoData.Jitter(random, rate, rate * 0.03);
            dispatches += live * step.TotalSeconds;
            progress?.Report(new GpuLoadProgress(elapsed, Math.Round(live, 1), 0));
        }

        int iterations = options.Intensity switch
        {
            GpuLoadIntensity.Light => 1536,
            GpuLoadIntensity.Medium => 2304,
            _ => 3072,
        };
        return new GpuLoadResult(true, (long)dispatches, 0, choice.Name, null, elapsed) { Iterations = iterations, BufferBytes = plan.BufferBytes };
    }
}
