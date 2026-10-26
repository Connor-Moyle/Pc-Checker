using PcChecker.Core.Common;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Memory;

/// <summary>What the memory profile (XMP/EXPO) check concluded, with plain-English guidance for the page.</summary>
/// <param name="State">"Enabled", "Disabled" or "Unknown" (the value stored in <see cref="MemoryOverview.XmpState"/>).</param>
/// <param name="ProfileName">"XMP", "EXPO" or "XMP/EXPO" depending on the platform and memory generation.</param>
public sealed record XmpEvaluation(
    string State,
    string ProfileName,
    int? ConfiguredMhz,
    int? RatedMhz,
    string Title,
    string Guidance,
    Severity Severity)
{
    public bool IsEnabled => State == "Enabled";
    public bool IsDisabled => State == "Disabled";
}

/// <summary>What the channel heuristic concluded from the installed modules.</summary>
public sealed record ChannelEvaluation(bool DualChannelLikely, string Title, string Detail);

/// <summary>Everything the warning rules need, gathered by a service (Windows or demo) and evaluated by <see cref="MemoryRules"/>.</summary>
public sealed record MemoryFacts(
    long TotalBytes,
    long AvailableBytes,
    long CommitTotalBytes,
    long CommitLimitBytes,
    bool MemoryCompressionEnabled,
    bool SysMainEnabled,
    bool AutomaticPageFile,
    IReadOnlyList<PageFileInfo> PageFiles,
    IReadOnlyList<MemoryModuleInfo> Modules,
    XmpEvaluation Xmp,
    string? LastMemoryDiagnosticResult,
    int LowMemoryPercent = 10,
    bool WarnOnLowMemory = true);

/// <summary>One mismatch found by the in-app memory test.</summary>
public sealed record MemoryTestError(int Chunk, long OffsetBytes, ulong Expected, ulong Actual, string Pattern)
{
    public override string ToString() =>
        $"Chunk {Chunk + 1}, offset 0x{OffsetBytes:X}, pattern \"{Pattern}\": expected 0x{Expected:X16}, read 0x{Actual:X16}";
}

/// <summary>The last Windows Memory Diagnostic result found in the System event log (event 1201 = clean, 1202 = errors).</summary>
public sealed record MemoryDiagnosticEvent(long EventId, DateTimeOffset Time, string? Message);
