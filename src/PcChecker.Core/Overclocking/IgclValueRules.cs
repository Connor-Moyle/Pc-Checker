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
        0x00000000 => "OK",
        0x00000001 => "OK (still open by another caller)",
        0x40000001 => "not initialized",
        0x40000002 => "already initialized",
        0x40000003 => "device lost",
        0x40000004 => "out of host memory",
        0x40000005 => "out of device memory",
        0x40000006 => "insufficient permissions",
        0x40000007 => "not available",
        0x40000008 => "library not initialized",
        0x40000009 => "unsupported version",
        0x4000000A => "unsupported feature",
        0x4000000B => "invalid argument",
        0x4000000C => "invalid API handle",
        0x4000000D => "invalid null handle",
        0x4000000E => "invalid null pointer",
        0x4000000F => "invalid size",
        0x40000010 => "unsupported size",
        0x40000011 => "unsupported image format",
        0x40000012 => "data read error",
        0x40000013 => "data write error",
        0x40000014 => "data not found",
        0x40000015 => "not implemented",
        0x40000016 => "operating system call failed",
        0x40000017 => "kernel-mode driver call failed",
        0x40000018 => "library unload failed",
        0x40000019 => "Level Zero loader not found",
        0x4000001A => "invalid operation type",
        0x4000001B => "null OS interface",
        0x4000001C => "null OS adapter handle",
        0x4000001D => "null display output handle",
        0x4000001E => "wait timed out",
        0x4000001F => "persistence not supported",
        0x40000020 => "platform not supported",
        0x40000021 => "unknown application UID",
        0x40000022 => "invalid enumeration",
        0x40000023 => "file delete failed",
        0x40000024 => "device reset required",
        0x40000025 => "full reboot required",
        0x40000026 => "library load failed",
        0x40000027 => "device unavailable (loading, low power or recovering)",
        0x4000FFFF => "unknown error",
        0x40010000 => "retry the operation",
        0x40010001 => "IGSC loader not found",
        0x40010002 => "application not allowed",
        0x44000001 => "overclocking is not supported on this GPU",
        0x44000002 => "overclock: voltage outside the allowed range",
        0x44000003 => "overclock: frequency outside the allowed range",
        0x44000004 => "overclock: power outside the allowed range",
        0x44000005 => "overclock: temperature outside the allowed range",
        0x44000006 => "overclock: the GPU is in voltage-locked mode",
        0x44000007 => "overclock: a device reset is required before the change applies",
        0x44000008 => "overclock waiver not set (accept the waiver first)",
        0x44000009 => "overclock: deprecated API, a newer version is required",
        0x4400000D => "overclock: VRAM memory speed outside the allowed range",
        0x4400000E => "overclock: invalid custom voltage/frequency curve",
        _ => $"IGCL error 0x{code:X8}",
    };
}
