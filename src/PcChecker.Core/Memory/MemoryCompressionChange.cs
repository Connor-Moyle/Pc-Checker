using PcChecker.Core.Common;

namespace PcChecker.Core.Memory;

/// <summary>
/// Turns the before/after state reported by Get-MMAgent into the result of a memory compression change.
/// Enable-/Disable-MMAgent -MemoryCompression only takes effect after a restart, so a real change always
/// asks for one; asking for the state the PC is already in does not.
/// </summary>
public static class MemoryCompressionChange
{
    public const string RestartNote = "Takes effect after a restart.";

    public static OperationResult Evaluate(bool enabled, bool? before, bool? after)
    {
        string state = enabled ? "on" : "off";
        if (after is not null && after != enabled)
        {
            return OperationResult.Fail($"Windows reported memory compression still {(after.Value ? "on" : "off")} after the change.");
        }

        if (before == enabled)
        {
            return OperationResult.Ok($"Memory compression was already {state}.");
        }

        return OperationResult.Ok($"Memory compression is set to {state}. {RestartNote}", rebootRequired: true);
    }

    /// <summary>Parses the "before" and "after" lines printed by the compression change script.</summary>
    public static (bool? Before, bool? After) ParseStates(string? output)
    {
        var lines = (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool? Parse(int index) => index >= 0 && index < lines.Length && bool.TryParse(lines[index], out bool value) ? value : null;
        return lines.Length >= 2 ? (Parse(lines.Length - 2), Parse(lines.Length - 1)) : (null, Parse(lines.Length - 1));
    }
}
