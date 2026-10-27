using PcChecker.App.Localization;
using PcChecker.Core.Benchmark;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Translated wording for the Benchmark page. Core keeps the English text for logs and the exported report; the page
/// shows these instead so they can be translated (the literals here are picked up by build/extract-strings.py).
/// </summary>
internal static class BenchmarkText
{
    public static string Name(BenchmarkTestKind test) => test switch
    {
        BenchmarkTestKind.CpuSingleCore => Loc.T("Processor, single-core"),
        BenchmarkTestKind.CpuMultiCore => Loc.T("Processor, multi-core"),
        BenchmarkTestKind.MemoryBandwidth => Loc.T("Memory bandwidth"),
        BenchmarkTestKind.MemoryLatency => Loc.T("Memory latency"),
        BenchmarkTestKind.Disk => Loc.T("System drive"),
        BenchmarkTestKind.GpuCompute => Loc.T("Graphics compute"),
        _ => BenchmarkCatalog.Name(test),
    };

    public static string Description(BenchmarkTestKind test) => test switch
    {
        BenchmarkTestKind.CpuSingleCore => Loc.T("Compression, a prime sieve, matrix maths and SHA-256 on one core. Drives how snappy apps and most games feel."),
        BenchmarkTestKind.CpuMultiCore => Loc.T("The same work on every core at once. Matters for video export, compiling and heavy multitasking."),
        BenchmarkTestKind.MemoryBandwidth => Loc.T("How fast a large block of memory can be copied, using several cores."),
        BenchmarkTestKind.MemoryLatency => Loc.T("How long the processor waits for one value from memory. Lower is better."),
        BenchmarkTestKind.Disk => Loc.T("Sequential and random 4K reads and writes on the Windows drive, with a small temporary file."),
        BenchmarkTestKind.GpuCompute => Loc.T("Shader maths on the graphics card through DirectX 11. Skipped when there is no DirectX 11 card."),
        _ => BenchmarkCatalog.Description(test),
    };

    /// <summary>The verdict for a score, with the same bands as <see cref="BenchmarkScoring.Rating"/>.</summary>
    public static string Rating(int? score) => BenchmarkScoring.Rating(score) switch
    {
        "No score" => Loc.T("No score"),
        "Well above the reference PC" => Loc.T("Well above the reference PC"),
        "Above the reference PC" => Loc.T("Above the reference PC"),
        "About the same as the reference PC" => Loc.T("About the same as the reference PC"),
        "Below the reference PC" => Loc.T("Below the reference PC"),
        "Well below the reference PC" => Loc.T("Well below the reference PC"),
        var other => other,
    };

    public static string ChangeWord(BenchmarkChange change) => change switch
    {
        BenchmarkChange.Better => Loc.T("better"),
        BenchmarkChange.Worse => Loc.T("worse"),
        BenchmarkChange.Same => Loc.T("no real change"),
        _ => Loc.T("not compared"),
    };

    /// <summary>The status line of a test card: the verdict, or why the test was skipped or did not finish.</summary>
    public static string Status(BenchmarkTestResult result) => result.Status switch
    {
        BenchmarkTestStatus.Skipped => result.Note is { } skip ? Loc.F("Skipped: {0}", skip) : Loc.T("Skipped: not available on this PC."),
        BenchmarkTestStatus.Failed => result.Note is { } fail ? Loc.F("Did not finish: {0}", fail) : Loc.T("Did not finish: the test stopped with an error."),
        _ => Rating(result.Score),
    };
}
