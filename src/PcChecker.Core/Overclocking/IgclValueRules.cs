namespace PcChecker.Core.Overclocking;

/// <summary>One IGCL overclock control's range (ctl_oc_control_info_t).</summary>
public sealed record IgclControl(bool Supported, bool Relative, double Min, double Max, double Step, double Default)
{
    public static IgclControl Unsupported { get; } = new(false, false, 0, 0, 0, 0);
}

/// <summary>Pure validation and mapping for Intel Arc tuning through the Graphics Control Library.</summary>
public static class IgclValueRules
{
    /// <summary>Snaps a value to the control's step and clamps it into range.</summary>
    public static double Snap(IgclControl control, double value)
    {
        ArgumentNullException.ThrowIfNull(control);
        double min = Math.Min(control.Min, control.Max), max = Math.Max(control.Min, control.Max);
        double v = Math.Clamp(value, min, max);
        if (control.Step > 0)
        {
            v = min + Math.Round((v - min) / control.Step) * control.Step;
            v = Math.Clamp(v, min, max);
        }

        return v;
    }

    public static bool IsWithin(IgclControl control, double value)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.Supported && value >= Math.Min(control.Min, control.Max) - 1e-9 && value <= Math.Max(control.Min, control.Max) + 1e-9;
    }

    /// <summary>IGCL power limits are in milliwatts; PC Checker uses percent of the default.</summary>
    public static double PercentToMilliwatts(IgclControl powerLimit, int percent)
    {
        ArgumentNullException.ThrowIfNull(powerLimit);
        double baseline = powerLimit.Default > 0 ? powerLimit.Default : powerLimit.Max;
        return Snap(powerLimit, baseline * percent / 100.0);
    }

    public static int MilliwattsToPercent(IgclControl powerLimit, double milliwatts)
    {
        ArgumentNullException.ThrowIfNull(powerLimit);
        double baseline = powerLimit.Default > 0 ? powerLimit.Default : powerLimit.Max;
        return baseline <= 0 ? 100 : (int)Math.Round(milliwatts / baseline * 100);
    }

    public static GpuLimits BuildLimits(IgclControl gpuOffset, IgclControl vramOffset, IgclControl powerLimit, IgclControl tempLimit)
    {
        ArgumentNullException.ThrowIfNull(gpuOffset);
        ArgumentNullException.ThrowIfNull(vramOffset);
        ArgumentNullException.ThrowIfNull(powerLimit);
        ArgumentNullException.ThrowIfNull(tempLimit);

        int coreMin = gpuOffset.Supported ? (int)Math.Round(Math.Min(gpuOffset.Min, gpuOffset.Max)) : 0;
        int coreMax = gpuOffset.Supported ? (int)Math.Round(Math.Max(gpuOffset.Min, gpuOffset.Max)) : 0;
        int memMin = vramOffset.Supported ? (int)Math.Round(Math.Min(vramOffset.Min, vramOffset.Max)) : 0;
        int memMax = vramOffset.Supported ? (int)Math.Round(Math.Max(vramOffset.Min, vramOffset.Max)) : 0;
        int powMin = powerLimit.Supported ? MilliwattsToPercent(powerLimit, Math.Min(powerLimit.Min, powerLimit.Max)) : 100;
        int powMax = powerLimit.Supported ? MilliwattsToPercent(powerLimit, Math.Max(powerLimit.Min, powerLimit.Max)) : 100;
        int tMin = tempLimit.Supported ? (int)Math.Round(Math.Min(tempLimit.Min, tempLimit.Max)) : 0;
        int tMax = tempLimit.Supported ? (int)Math.Round(Math.Max(tempLimit.Min, tempLimit.Max)) : 0;
        return new GpuLimits(coreMin, coreMax, memMin, memMax, powMin, powMax, tMin, tMax, false);
    }

    /// <summary>Human-readable IGCL result codes (igcl_api.h ctl_result_t).</summary>
    public static string DescribeResult(uint code) => code switch
    {
        0 => "OK",
        0x40000000 => "generic error",
        0x40000001 => "not initialized",
        0x40000002 => "already initialized",
        0x40000003 => "device lost",
        0x40000004 => "out of host memory",
        0x40000005 => "out of device memory",
        0x40000006 => "insufficient permissions",
        0x40000007 => "not available",
        0x40000008 => "unsupported version",
        0x40000009 => "invalid argument",
        0x4000000A => "invalid null handle",
        0x4000000B => "invalid null pointer",
        0x4000000C => "invalid size",
        0x4000000D => "unsupported size",
        0x4000000E => "unsupported feature",
        0x40000011 => "null driver instance",
        0x40000013 => "unsupported operation",
        0x44000001 => "overclock waiver not set (accept the waiver first)",
        0x44000002 => "invalid overclock value",
        0x44000004 => "overclock: invalid voltage/frequency",
        0x44000005 => "overclock: reset required",
        0x44000006 => "overclock: waiver not set",
        _ => $"IGCL error 0x{code:X8}",
    };
}
