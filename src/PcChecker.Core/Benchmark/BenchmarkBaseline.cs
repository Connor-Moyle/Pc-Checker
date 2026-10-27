namespace PcChecker.Core.Benchmark;

/// <summary>One scored number and what the reference PC reaches on it.</summary>
public sealed record BaselineValue(string Key, string Name, double Reference, string Unit, bool LowerIsBetter = false);

/// <summary>
/// The reference PC every score is measured against: a mid-range 2024 desktop (6-core/12-thread processor,
/// dual-channel DDR5-6000, PCIe 4.0 NVMe drive, GeForce RTX 4060-class graphics). It scores about 1000 on each test.
/// The numbers are estimates for PC Checker's own workloads, not vendor figures. Change them only together with
/// <see cref="Version"/>, so older runs are flagged as scored differently instead of compared silently.
/// </summary>
public static class BenchmarkBaseline
{
    /// <summary>Version of this table; stored with each run.</summary>
    public const int Version = 1;

    /// <summary>The score of a PC exactly as fast as the reference on a test.</summary>
    public const int ReferenceScore = 1000;

    public static IReadOnlyList<BaselineValue> CpuSingleCore { get; } =
    [
        new("cpu1.compression", "Compression", 120, "MB/s"),
        new("cpu1.sieve", "Prime sieve", 750, "M numbers/s"),
        new("cpu1.matrix", "Matrix multiply", 7, "GFLOPS"),
        new("cpu1.sha256", "SHA-256", 2200, "MB/s"),
    ];

    public static IReadOnlyList<BaselineValue> CpuMultiCore { get; } =
    [
        new("cpuN.compression", "Compression", 840, "MB/s"),
        new("cpuN.sieve", "Prime sieve", 4500, "M numbers/s"),
        new("cpuN.matrix", "Matrix multiply", 45, "GFLOPS"),
        new("cpuN.sha256", "SHA-256", 15000, "MB/s"),
    ];

    public static IReadOnlyList<BaselineValue> MemoryBandwidth { get; } =
    [
        new("mem.copy", "Copy", 32, "GB/s"),
    ];

    public static IReadOnlyList<BaselineValue> MemoryLatency { get; } =
    [
        new("mem.latency", "Latency", 100, "ns", LowerIsBetter: true),
    ];

    /// <summary>Unbuffered, queue depth 1, as the Storage page's benchmark measures (1 MB sequential, 4 KB random).</summary>
    public static IReadOnlyList<BaselineValue> Disk { get; } =
    [
        new("disk.seqread", "Sequential read", 3500, "MB/s"),
        new("disk.seqwrite", "Sequential write", 3000, "MB/s"),
        new("disk.rand4kread", "Random 4K read", 60, "MB/s"),
        new("disk.rand4kwrite", "Random 4K write", 160, "MB/s"),
    ];

    /// <summary>Billions of steps of the GPU load shader's inner loop per second, on the whole card.</summary>
    public static IReadOnlyList<BaselineValue> GpuCompute { get; } =
    [
        new("gpu.throughput", "Shader throughput", 160, "G steps/s"),
    ];

    /// <summary>The scored values of <paramref name="test"/>.</summary>
    public static IReadOnlyList<BaselineValue> For(BenchmarkTestKind test) => test switch
    {
        BenchmarkTestKind.CpuSingleCore => CpuSingleCore,
        BenchmarkTestKind.CpuMultiCore => CpuMultiCore,
        BenchmarkTestKind.MemoryBandwidth => MemoryBandwidth,
        BenchmarkTestKind.MemoryLatency => MemoryLatency,
        BenchmarkTestKind.Disk => Disk,
        BenchmarkTestKind.GpuCompute => GpuCompute,
        _ => [],
    };

    /// <summary>The baseline for a CPU kernel in the single- or multi-core table.</summary>
    public static BaselineValue ForKernel(CpuKernel kernel, bool multiCore)
    {
        var table = multiCore ? CpuMultiCore : CpuSingleCore;
        return table[(int)kernel];
    }
}
