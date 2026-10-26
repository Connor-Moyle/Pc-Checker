using PcChecker.Core.Common;
using PcChecker.Core.Health;
using Xunit;

namespace PcChecker.Core.Tests;

public class DeviceProblemCodesTests
{
    [Fact]
    public void All_codes_1_to_54_are_documented()
    {
        for (int code = 1; code <= 54; code++)
        {
            Assert.True(DeviceProblemCodes.IsKnown(code), $"code {code} missing");
            var d = DeviceProblemCodes.Describe(code);
            Assert.Equal(code, d.Code);
            Assert.False(string.IsNullOrWhiteSpace(d.Title));
            Assert.False(string.IsNullOrWhiteSpace(d.Suggestion));
        }
    }

    [Fact]
    public void Zero_and_unknown_codes_have_fallbacks()
    {
        Assert.Contains("working properly", DeviceProblemCodes.Describe(0).Title);
        var unknown = DeviceProblemCodes.Describe(999);
        Assert.Contains("999", unknown.Title);
        Assert.False(DeviceProblemCodes.IsKnown(999));
    }

    [Fact]
    public void Well_known_codes_have_expected_text()
    {
        Assert.Contains("stopped this device", DeviceProblemCodes.Describe(43).Title);
        Assert.Contains("disabled", DeviceProblemCodes.Describe(22).Title);
        Assert.Contains("drivers for this device are not installed", DeviceProblemCodes.Describe(28).Title);
    }
}

public class HealthScoringTests
{
    private static HealthItem Item(HealthStatus s) => new("c", s, "t", "d", null, HealthItem.NoMetrics);

    [Fact]
    public void Empty_items_score_100()
    {
        Assert.Equal(100, HealthScoring.Score([]));
    }

    [Fact]
    public void Penalties_apply_per_status()
    {
        Assert.Equal(92, HealthScoring.Score([Item(HealthStatus.Warning)]));
        Assert.Equal(70, HealthScoring.Score([Item(HealthStatus.Critical)]));
        Assert.Equal(98, HealthScoring.Score([Item(HealthStatus.Unknown)]));
        Assert.Equal(100, HealthScoring.Score([Item(HealthStatus.Good), Item(HealthStatus.Good)]));
    }

    [Fact]
    public void Score_never_goes_below_zero()
    {
        var items = Enumerable.Repeat(Item(HealthStatus.Critical), 10).ToList();
        Assert.Equal(0, HealthScoring.Score(items));
    }

    [Theory]
    [InlineData(100, HealthStatus.Good)]
    [InlineData(80, HealthStatus.Good)]
    [InlineData(79, HealthStatus.Warning)]
    [InlineData(50, HealthStatus.Warning)]
    [InlineData(49, HealthStatus.Critical)]
    public void Status_from_score(int score, HealthStatus expected)
    {
        Assert.Equal(expected, HealthScoring.StatusFromScore(score));
    }

    [Fact]
    public void Worst_picks_the_most_severe()
    {
        Assert.Equal(HealthStatus.Critical, HealthScoring.Worst([HealthStatus.Good, HealthStatus.Critical, HealthStatus.Warning]));
        Assert.Equal(HealthStatus.Unknown, HealthScoring.Worst([HealthStatus.Good, HealthStatus.Unknown]));
        Assert.Equal(HealthStatus.Unknown, HealthScoring.Worst([]));
    }

    [Fact]
    public void Battery_wear_and_status()
    {
        Assert.Equal(25, HealthScoring.BatteryWear(40000, 30000));
        Assert.Null(HealthScoring.BatteryWear(0, 30000));
        Assert.Equal(HealthStatus.Warning, HealthScoring.BatteryStatus(25));
        Assert.Equal(HealthStatus.Critical, HealthScoring.BatteryStatus(45));
        Assert.Equal(HealthStatus.Good, HealthScoring.BatteryStatus(5));
        Assert.Equal(HealthStatus.Unknown, HealthScoring.BatteryStatus(null));
    }

    [Fact]
    public void Describe_labels_scores()
    {
        Assert.Equal("Excellent", HealthScoring.Describe(97));
        Assert.Equal("Poor", HealthScoring.Describe(10));
    }
}

public class SmartRulesTests
{
    private static DiskHealth Disk(long? reallocated = null, long? pending = null, long? uncorrectable = null, double? wear = null,
        double? temp = null, string? smart = "OK", long? hours = null, IReadOnlyList<SmartAttribute>? attrs = null) =>
        new("Model", "SN", "FW", 1_000_000_000_000, "SSD", HealthStatus.Unknown, smart, temp, hours, reallocated, pending, uncorrectable, wear, null, attrs ?? []);

    [Fact]
    public void Clean_disk_is_good()
    {
        var v = SmartRules.Evaluate(Disk());
        Assert.Equal(HealthStatus.Good, v.Status);
        Assert.Empty(v.Reasons);
    }

    [Fact]
    public void Reallocated_sectors_warn_then_critical()
    {
        Assert.Equal(HealthStatus.Warning, SmartRules.Evaluate(Disk(reallocated: 3)).Status);
        Assert.Equal(HealthStatus.Critical, SmartRules.Evaluate(Disk(reallocated: 80)).Status);
    }

    [Fact]
    public void Uncorrectable_errors_and_smart_failure_are_critical()
    {
        Assert.Equal(HealthStatus.Critical, SmartRules.Evaluate(Disk(uncorrectable: 1)).Status);
        Assert.Equal(HealthStatus.Critical, SmartRules.Evaluate(Disk(smart: "Pred Fail")).Status);
    }

    [Fact]
    public void Temperature_uses_thresholds()
    {
        Assert.Equal(HealthStatus.Warning, SmartRules.Evaluate(Disk(temp: 58)).Status);
        Assert.Equal(HealthStatus.Critical, SmartRules.Evaluate(Disk(temp: 70)).Status);
        Assert.Equal(HealthStatus.Good, SmartRules.Evaluate(Disk(temp: 58), diskWarnC: 60, diskCritC: 70).Status);
    }

    [Fact]
    public void Wear_and_failed_attributes()
    {
        Assert.Equal(HealthStatus.Warning, SmartRules.Evaluate(Disk(wear: 75)).Status);
        Assert.Equal(HealthStatus.Critical, SmartRules.Evaluate(Disk(wear: 95)).Status);
        var failed = Disk(attrs: [new SmartAttribute(5, "Reallocated", 10, 10, 36, 0, true)]);
        Assert.Equal(HealthStatus.Critical, SmartRules.Evaluate(failed).Status);
        var belowThreshold = Disk(attrs: [new SmartAttribute(1, "Raw Read Error", 20, 20, 44, 0, false)]);
        Assert.Equal(HealthStatus.Warning, SmartRules.Evaluate(belowThreshold).Status);
    }

    [Fact]
    public void Wear_from_attributes()
    {
        Assert.Equal(12, SmartRules.WearFromAttributes([new SmartAttribute(177, "Wear", 88, 88, 0, 0, false)]));
        Assert.Null(SmartRules.WearFromAttributes([new SmartAttribute(9, "Hours", 100, 100, 0, 1000, false)]));
    }
}
