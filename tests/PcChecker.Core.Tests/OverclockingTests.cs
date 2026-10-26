using PcChecker.Core.Overclocking;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class OverclockSafetyValidatorTests
{
    private static OverclockSettings Settings(bool consent = true) => new() { ConsentAccepted = consent };

    private static OverclockCapabilities Nvidia(GpuLimits? limits = null) =>
        new(GpuVendor.Nvidia, "RTX", GpuControlMethod.NvApi, limits, "CPU", CpuControlMethod.None, null, "Enabled", 6000, 6000, []);

    [Fact]
    public void Consent_is_required()
    {
        var r = OverclockSafetyValidator.Validate(new OverclockProfile(), Nvidia(), Settings(consent: false));
        Assert.False(r.Success);
        Assert.Contains("accept", r.Message);
    }

    [Fact]
    public void Stock_profile_passes()
    {
        Assert.True(OverclockSafetyValidator.Validate(OverclockProfile.Stock(), OverclockCapabilities.None(), Settings()).Success);
    }

    [Fact]
    public void Gpu_changes_need_a_tunable_gpu()
    {
        var r = OverclockSafetyValidator.Validate(new OverclockProfile { GpuCoreOffsetMhz = 50 }, OverclockCapabilities.None(), Settings());
        Assert.False(r.Success);
        Assert.Contains("cannot be tuned", r.Message);
    }

    [Fact]
    public void User_limits_reject_large_offsets()
    {
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { GpuCoreOffsetMhz = 250 }, Nvidia(), Settings()).Success);
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { GpuMemoryOffsetMhz = -1500 }, Nvidia(), Settings()).Success);
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { GpuPowerLimitPercent = 130 }, Nvidia(), Settings()).Success);
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { GpuTempLimitC = 95 }, Nvidia(), Settings()).Success);
        Assert.True(OverclockSafetyValidator.Validate(new OverclockProfile { GpuCoreOffsetMhz = 150, GpuMemoryOffsetMhz = 800, GpuPowerLimitPercent = 110, GpuTempLimitC = 83 }, Nvidia(), Settings()).Success);
    }

    [Fact]
    public void Hardware_limits_are_enforced_too()
    {
        var limits = new GpuLimits(-100, 100, -500, 500, 70, 105, 65, 85, false);
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { GpuCoreOffsetMhz = 150 }, Nvidia(limits), Settings()).Success);
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { GpuFanPercent = 60 }, Nvidia(limits), Settings()).Success);
        Assert.True(OverclockSafetyValidator.Validate(new OverclockProfile { GpuCoreOffsetMhz = 90 }, Nvidia(limits), Settings()).Success);
    }

    [Fact]
    public void Cpu_changes_need_a_tool_and_sane_values()
    {
        var withCpu = new OverclockCapabilities(GpuVendor.Unknown, null, GpuControlMethod.None, null, "CPU", CpuControlMethod.IntelXtuCli, "xtu.exe", "Unknown", null, null, []);
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { CpuAllCoreRatio = 50 }, OverclockCapabilities.None(), Settings()).Success);
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { CpuVoltageOffsetMv = 300 }, withCpu, Settings()).Success);
        Assert.True(OverclockSafetyValidator.Validate(new OverclockProfile { CpuAllCoreRatio = 50, CpuVoltageOffsetMv = -50 }, withCpu, Settings()).Success);
    }

    [Fact]
    public void Clamp_brings_values_into_range()
    {
        var p = new OverclockProfile { GpuCoreOffsetMhz = 500, GpuMemoryOffsetMhz = -3000, GpuPowerLimitPercent = 200, GpuTempLimitC = 120, GpuFanPercent = 140, CpuVoltageOffsetMv = -400, CpuAllCoreRatio = 99 };
        var c = OverclockSafetyValidator.Clamp(p, Settings(), null);
        Assert.Equal(200, c.GpuCoreOffsetMhz);
        Assert.Equal(-1000, c.GpuMemoryOffsetMhz);
        Assert.Equal(115, c.GpuPowerLimitPercent);
        Assert.Equal(87, c.GpuTempLimitC);
        Assert.Equal(100, c.GpuFanPercent);
        Assert.Equal(-150, c.CpuVoltageOffsetMv);
        Assert.Equal(70, c.CpuAllCoreRatio);
        Assert.Equal(500, p.GpuCoreOffsetMhz); // original untouched
    }

    [Fact]
    public void Clamp_uses_the_tighter_of_user_and_hardware_limits()
    {
        var limits = new GpuLimits(-100, 100, -500, 500, 70, 105, 65, 85, false);
        var c = OverclockSafetyValidator.Clamp(new OverclockProfile { GpuCoreOffsetMhz = 180, GpuPowerLimitPercent = 110, GpuFanPercent = 50 }, Settings(), limits);
        Assert.Equal(100, c.GpuCoreOffsetMhz);
        Assert.Equal(105, c.GpuPowerLimitPercent);
        Assert.Null(c.GpuFanPercent);
    }
}

public class OverclockPlanParserTests
{
    private const string Plan = """
        {
          "summary": "Mild GPU tune",
          "steps": [
            { "order": 2, "title": "Memory", "rationale": "VRAM headroom", "target": { "gpuMemoryOffsetMhz": "500", "gpuPowerLimitPercent": 105 }, "stressSeconds": 600, "abortIf": "artifacts", "expectedGain": "3%" },
            { "order": 1, "title": "Core", "rationale": "safe start", "target": { "gpu_core_offset_mhz": 50 }, "stress_seconds": 300 }
          ],
          "warnings": ["Keep temps under 85C"],
          "assumptions": ["Stock cooler"],
          "disclaimer": "At your own risk."
        }
        """;

    [Fact]
    public void Parses_full_plan_and_orders_steps()
    {
        var plan = OverclockPlanParser.TryParse(Plan);
        Assert.NotNull(plan);
        Assert.Equal("Mild GPU tune", plan.Summary);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("Core", plan.Steps[0].Title);
        Assert.Equal(50, plan.Steps[0].Target.GpuCoreOffsetMhz);
        Assert.Equal(300, plan.Steps[0].StressSeconds);
        Assert.Equal(500, plan.Steps[1].Target.GpuMemoryOffsetMhz);
        Assert.Equal(105, plan.Steps[1].Target.GpuPowerLimitPercent);
        Assert.Single(plan.Warnings);
        Assert.Single(plan.Assumptions);
        Assert.Equal("At your own risk.", plan.Disclaimer);
    }

    [Fact]
    public void Accepts_fenced_json_with_prose()
    {
        var text = "Here is your plan:\n```json\n" + Plan + "\n```\nLet me know how it goes.";
        var plan = OverclockPlanParser.TryParse(text);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Steps.Count);
    }

    [Fact]
    public void Tolerates_missing_fields_and_defaults()
    {
        var plan = OverclockPlanParser.TryParse("{\"Summary\":\"x\",\"Steps\":[{\"Title\":\"only\"}]}");
        Assert.NotNull(plan);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(1, step.Order);
        Assert.Equal(300, step.StressSeconds);
        Assert.True(step.Target.IsStock);
        Assert.False(string.IsNullOrWhiteSpace(plan.Disclaimer));
    }

    [Fact]
    public void Rejects_garbage_and_empty()
    {
        Assert.Null(OverclockPlanParser.TryParse(null));
        Assert.Null(OverclockPlanParser.TryParse("no json here"));
        Assert.Null(OverclockPlanParser.TryParse("{ broken"));
        Assert.Null(OverclockPlanParser.TryParse("{\"unrelated\": 1}"));
        Assert.Null(OverclockPlanParser.TryParse("[1,2,3]"));
    }

    [Fact]
    public void Numbers_with_units_are_parsed()
    {
        var plan = OverclockPlanParser.TryParse("{\"summary\":\"u\",\"steps\":[{\"title\":\"t\",\"target\":{\"coreOffset\":\"+75 MHz\",\"tempLimit\":\"83 °C\",\"powerLimit\":\"110%\"}}]}");
        Assert.NotNull(plan);
        Assert.Equal(75, plan.Steps[0].Target.GpuCoreOffsetMhz);
        Assert.Equal(83, plan.Steps[0].Target.GpuTempLimitC);
        Assert.Equal(110, plan.Steps[0].Target.GpuPowerLimitPercent);
    }

    [Fact]
    public void ExtractJson_finds_balanced_object_with_strings_containing_braces()
    {
        var json = OverclockPlanParser.ExtractJson("text {\"a\":\"}{\",\"b\":{\"c\":1}} trailing }");
        Assert.Equal("{\"a\":\"}{\",\"b\":{\"c\":1}}", json);
    }
}
