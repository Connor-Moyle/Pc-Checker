namespace PcChecker.Core.Network;

/// <summary>How a <c>netsh int ip reset</c> run ended.</summary>
public enum NetshResetStatus
{
    /// <summary>Exit code 0: every part was reset.</summary>
    Success,

    /// <summary>Non-zero exit code, but some parts reported "OK!": the reset mostly worked (typically a protected key was skipped).</summary>
    Partial,

    /// <summary>Non-zero exit code and nothing reported "OK!" (for example it needed administrator rights).</summary>
    Failed,
}

/// <summary>
/// Classifies the output of <c>netsh int ip reset</c>. It prints one "Resetting &lt;part&gt;, OK!" or
/// "Resetting &lt;part&gt;, failed." line per part; the "OK!" token is kept in localized Windows builds, so it is the
/// only text this relies on. A non-zero exit code is never treated as a clean success.
/// </summary>
public static class NetshResetOutcome
{
    public static NetshResetStatus Classify(int exitCode, string? output)
    {
        if (exitCode == 0)
        {
            return NetshResetStatus.Success;
        }

        return OkLineCount(output) > 0 ? NetshResetStatus.Partial : NetshResetStatus.Failed;
    }

    /// <summary>Lines that did not end in "OK!" and are not blank; used to tell the user what was skipped.</summary>
    public static IReadOnlyList<string> ProblemLines(string? output) =>
        Lines(output).Where(l => !IsOkLine(l) && l.Contains(',', StringComparison.Ordinal)).Take(5).ToList();

    private static int OkLineCount(string? output) => Lines(output).Count(IsOkLine);

    private static bool IsOkLine(string line) => line.EndsWith("OK!", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Lines(string? output) =>
        (output ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
