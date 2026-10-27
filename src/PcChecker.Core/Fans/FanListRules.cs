using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

/// <summary>
/// Pure rules for the fan list: read-only and control-only fans, and which motherboard headers count as empty
/// (nothing plugged in) so they can be hidden until "Show empty headers" is on.
/// </summary>
public static class FanListRules
{
    public static FanCapability Classify(FanDevice fan)
    {
        ArgumentNullException.ThrowIfNull(fan);
        if (!fan.Controllable)
        {
            return FanCapability.ReadOnly;
        }

        return fan.RpmSensorId is null ? FanCapability.ControlWithoutRpm : FanCapability.Controllable;
    }

    /// <summary>One line for the card explaining a read-only or control-only fan; null for an ordinary controllable fan.</summary>
    public static string? Describe(FanCapability capability, HardwareKind hardware) => capability switch
    {
        FanCapability.ReadOnly when hardware == HardwareKind.Motherboard =>
            "Read-only: the sensor chip reports this header's speed but offers no control for it. Many boards have more speed inputs than controllable outputs (for example CPU_OPT follows CPU_FAN).",
        FanCapability.ReadOnly =>
            "Read-only: this fan's speed can be read, but its hardware offers no control here. Fans on a hub or an all-in-one cooler driven over USB can only be set in their maker's app.",
        FanCapability.ControlWithoutRpm =>
            "Control without RPM reading: this header can be driven, but no speed sensor could be matched to it with confidence, so its speed is not shown.",
        _ => null,
    };

    /// <summary>
    /// True for a motherboard header that reports 0 RPM, is under automatic control and has never been seen spinning
    /// this session: almost always nothing is plugged in. Graphics card fans are never hidden (they stop at idle on
    /// purpose), nor is a fan whose speed has not been read yet, nor one PC Checker is driving.
    /// </summary>
    public static bool IsEmptyHeader(FanDevice fan, bool everSpun)
    {
        ArgumentNullException.ThrowIfNull(fan);
        return fan.Hardware == HardwareKind.Motherboard
            && fan.RpmSensorId is not null
            && fan.Mode == FanMode.Auto
            && !everSpun
            && fan.Rpm is { } rpm && rpm < 1;
    }

    /// <summary>The fans to show and how many empty headers were left out.</summary>
    public static (IReadOnlyList<FanDevice> Visible, int HiddenEmpty) Visible(IEnumerable<FanDevice> fans, bool showEmptyHeaders, ISet<string> everSpun)
    {
        ArgumentNullException.ThrowIfNull(fans);
        ArgumentNullException.ThrowIfNull(everSpun);
        var visible = new List<FanDevice>();
        int hidden = 0;
        foreach (var fan in fans)
        {
            if (fan.Rpm is > 0)
            {
                everSpun.Add(fan.Id);
            }

            if (!showEmptyHeaders && IsEmptyHeader(fan, everSpun.Contains(fan.Id)))
            {
                hidden++;
                continue;
            }

            visible.Add(fan);
        }

        return (visible, hidden);
    }
}
