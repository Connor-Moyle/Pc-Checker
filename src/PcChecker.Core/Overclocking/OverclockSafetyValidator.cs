using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Overclocking;

/// <summary>Pure validation and clamping of overclock profiles against user limits and hardware limits.</summary>
public static class OverclockSafetyValidator
{
    public const int MaxCpuVoltageOffsetMv = 100;
    public const int MinCpuVoltageOffsetMv = -150;
    public const int MinGpuTempLimitC = 60;
    public const int MinGpuPowerLimitPercent = 50;
    public const int MaxFanPercent = 100;

    /// <summary>Checks a profile; returns a failure describing the first problem found, including consent.</summary>
    public static OperationResult Validate(OverclockProfile profile, OverclockCapabilities capabilities, OverclockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.ConsentAccepted)
        {
            return OperationResult.Fail("You need to read and accept the overclocking risks before applying changes.");
        }

        bool touchesGpu = profile.GpuCoreOffsetMhz != 0 || profile.GpuMemoryOffsetMhz != 0 || profile.GpuPowerLimitPercent != 100
            || profile.GpuTempLimitC is not null || profile.GpuFanPercent is not null;
        bool touchesCpu = profile.CpuAllCoreRatio is not null || profile.CpuVoltageOffsetMv is not null || profile.CpuPowerLimitW is not null;

        if (touchesGpu && !capabilities.CanTuneGpu)
        {
            return OperationResult.Fail("This graphics card cannot be tuned from PC Checker. " + VendorHint(capabilities.GpuVendor));
        }

        if (touchesCpu && !capabilities.CanTuneCpu)
        {
            return OperationResult.Fail("No supported CPU tuning tool was found (Intel XTU or AMD Ryzen Master). CPU settings must be changed in the BIOS.");
        }

        if (Math.Abs(profile.GpuCoreOffsetMhz) > settings.MaxGpuCoreOffsetMhz)
        {
            return OperationResult.Fail($"GPU core offset {profile.GpuCoreOffsetMhz:+#;-#;0} MHz exceeds your limit of ±{settings.MaxGpuCoreOffsetMhz} MHz.");
        }

        if (Math.Abs(profile.GpuMemoryOffsetMhz) > settings.MaxGpuMemoryOffsetMhz)
        {
            return OperationResult.Fail($"GPU memory offset {profile.GpuMemoryOffsetMhz:+#;-#;0} MHz exceeds your limit of ±{settings.MaxGpuMemoryOffsetMhz} MHz.");
        }

        if (profile.GpuPowerLimitPercent > settings.MaxGpuPowerLimitPercent || profile.GpuPowerLimitPercent < MinGpuPowerLimitPercent)
        {
            return OperationResult.Fail($"GPU power limit {profile.GpuPowerLimitPercent}% must be between {MinGpuPowerLimitPercent}% and {settings.MaxGpuPowerLimitPercent}%.");
        }

        if (profile.GpuTempLimitC is { } temp && (temp > settings.MaxGpuTempLimitC || temp < MinGpuTempLimitC))
        {
            return OperationResult.Fail($"GPU temperature limit {temp} °C must be between {MinGpuTempLimitC} and {settings.MaxGpuTempLimitC} °C.");
        }

        if (profile.GpuFanPercent is { } fan && (fan < 0 || fan > MaxFanPercent))
        {
            return OperationResult.Fail("GPU fan speed must be between 0 and 100 percent.");
        }

        if (capabilities.GpuLimits is { } limits)
        {
            if (profile.GpuCoreOffsetMhz < limits.CoreOffsetMinMhz || profile.GpuCoreOffsetMhz > limits.CoreOffsetMaxMhz)
            {
                return OperationResult.Fail($"The graphics driver allows core offsets from {limits.CoreOffsetMinMhz} to {limits.CoreOffsetMaxMhz} MHz.");
            }

            if (profile.GpuMemoryOffsetMhz < limits.MemoryOffsetMinMhz || profile.GpuMemoryOffsetMhz > limits.MemoryOffsetMaxMhz)
            {
                return OperationResult.Fail($"The graphics driver allows memory offsets from {limits.MemoryOffsetMinMhz} to {limits.MemoryOffsetMaxMhz} MHz.");
            }

            if (profile.GpuPowerLimitPercent < limits.PowerLimitMinPercent || profile.GpuPowerLimitPercent > limits.PowerLimitMaxPercent)
            {
                return OperationResult.Fail($"The graphics driver allows power limits from {limits.PowerLimitMinPercent}% to {limits.PowerLimitMaxPercent}%.");
            }

            if (profile.GpuFanPercent is not null && !limits.FanControl)
            {
                return OperationResult.Fail("Fan control is not available for this graphics card.");
            }
        }

        if (profile.CpuVoltageOffsetMv is { } mv && (mv > MaxCpuVoltageOffsetMv || mv < MinCpuVoltageOffsetMv))
        {
            return OperationResult.Fail($"CPU voltage offset must be between {MinCpuVoltageOffsetMv} and {MaxCpuVoltageOffsetMv} mV.");
        }

        if (profile.CpuAllCoreRatio is { } ratio && (ratio < 8 || ratio > 70))
        {
            return OperationResult.Fail("CPU all-core ratio must be between 8 and 70.");
        }

        if (profile.CpuPowerLimitW is { } watts && (watts < 15 || watts > 400))
        {
            return OperationResult.Fail("CPU power limit must be between 15 and 400 W.");
        }

        return OperationResult.Ok("The profile is within your safety limits.");
    }

    /// <summary>Returns a copy of the profile with every value clamped into the user's and the hardware's limits.</summary>
    /// <summary>
    /// Rejects a change while the CPU or GPU is already at or above the user's temperature limit. Unknown readings
    /// (no sensor) do not block; the caller decides how to warn about that.
    /// </summary>
    public static OperationResult? CheckTemperatures(double? cpuTempC, double? gpuTempC, OverclockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (cpuTempC is { } c && c >= settings.MaxCpuTempC)
        {
            return OperationResult.Fail($"The CPU is already at {c:0} °C, above your {settings.MaxCpuTempC} °C limit. Let it cool down before applying changes.");
        }

        if (gpuTempC is { } g && g >= settings.MaxGpuTempLimitC)
        {
            return OperationResult.Fail($"The GPU is already at {g:0} °C, above your {settings.MaxGpuTempLimitC} °C limit. Let it cool down before applying changes.");
        }

        return null;
    }

    public static OverclockProfile Clamp(OverclockProfile profile, OverclockSettings settings, GpuLimits? limits)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        var p = profile.Clone();

        int coreMin = -settings.MaxGpuCoreOffsetMhz, coreMax = settings.MaxGpuCoreOffsetMhz;
        int memMin = -settings.MaxGpuMemoryOffsetMhz, memMax = settings.MaxGpuMemoryOffsetMhz;
        int powMin = MinGpuPowerLimitPercent, powMax = settings.MaxGpuPowerLimitPercent;
        int tempMin = MinGpuTempLimitC, tempMax = settings.MaxGpuTempLimitC;

        if (limits is not null)
        {
            coreMin = Math.Max(coreMin, limits.CoreOffsetMinMhz);
            coreMax = Math.Min(coreMax, limits.CoreOffsetMaxMhz);
            memMin = Math.Max(memMin, limits.MemoryOffsetMinMhz);
            memMax = Math.Min(memMax, limits.MemoryOffsetMaxMhz);
            powMin = Math.Max(powMin, limits.PowerLimitMinPercent);
            powMax = Math.Min(powMax, limits.PowerLimitMaxPercent);
            tempMin = Math.Max(tempMin, limits.TempLimitMinC);
            tempMax = Math.Min(tempMax, limits.TempLimitMaxC);
            if (!limits.FanControl)
            {
                p.GpuFanPercent = null;
            }
        }

        p.GpuCoreOffsetMhz = ClampRange(p.GpuCoreOffsetMhz, coreMin, coreMax);
        p.GpuMemoryOffsetMhz = ClampRange(p.GpuMemoryOffsetMhz, memMin, memMax);
        p.GpuPowerLimitPercent = ClampRange(p.GpuPowerLimitPercent, powMin, powMax);
        if (p.GpuTempLimitC is { } t)
        {
            p.GpuTempLimitC = ClampRange(t, tempMin, tempMax);
        }

        if (p.GpuFanPercent is { } fan)
        {
            p.GpuFanPercent = Math.Clamp(fan, 0, MaxFanPercent);
        }

        if (p.CpuVoltageOffsetMv is { } mv)
        {
            p.CpuVoltageOffsetMv = Math.Clamp(mv, MinCpuVoltageOffsetMv, MaxCpuVoltageOffsetMv);
        }

        if (p.CpuAllCoreRatio is { } ratio)
        {
            p.CpuAllCoreRatio = Math.Clamp(ratio, 8, 70);
        }

        if (p.CpuPowerLimitW is { } watts)
        {
            p.CpuPowerLimitW = Math.Clamp(watts, 15, 400);
        }

        return p;
    }

    private static int ClampRange(int value, int min, int max) => min > max ? min : Math.Clamp(value, min, max);

    private static string VendorHint(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Amd => "Use AMD Software: Adrenalin Edition to tune Radeon cards.",
        GpuVendor.Intel => "Use Intel Graphics Software to tune Arc cards.",
        GpuVendor.Nvidia => "The NVIDIA driver did not expose tuning controls.",
        _ => "No supported graphics card was detected.",
    };
}
