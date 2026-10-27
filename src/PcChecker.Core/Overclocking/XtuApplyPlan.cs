namespace PcChecker.Core.Overclocking;

/// <summary>
/// One XTU write of a CPU profile: which tunable (by its known names), the value to send, and the value that puts
/// it back to what was live before (the previous profile's value, or "default" when it had none).
/// </summary>
public sealed record XtuApplyStep(string Setting, string[] Names, string Value, string RollbackValue);

/// <summary>Pure planner for the XTU writes of a CPU profile, so a failure part-way can put back what was already written.</summary>
public static class XtuApplyPlan
{
    public const string Default = "default";

    /// <summary>The writes that take the CPU from <paramref name="previous"/> (what is live now) to <paramref name="profile"/>.</summary>
    public static IReadOnlyList<XtuApplyStep> Build(OverclockProfile profile, OverclockProfile previous)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(previous);
        var steps = new List<XtuApplyStep>();
        Add("core ratio", XtuOutputParser.CoreRatioNames, profile.CpuAllCoreRatio, previous.CpuAllCoreRatio, IntelXtuCli.FormatRatio);
        Add("voltage offset", XtuOutputParser.VoltageOffsetNames, profile.CpuVoltageOffsetMv, previous.CpuVoltageOffsetMv, IntelXtuCli.FormatVoltageOffset);
        Add("power limit", XtuOutputParser.PowerLimitNames, profile.CpuPowerLimitW, previous.CpuPowerLimitW, IntelXtuCli.FormatWatts);
        return steps;

        void Add(string setting, string[] names, int? wanted, int? before, Func<int, string> format)
        {
            if (wanted is { } w)
            {
                steps.Add(new XtuApplyStep(setting, names, format(w), before is { } b ? format(b) : Default));
            }
        }
    }
}
