using PcChecker.Core.Overclocking;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Assistant;

public sealed record AssistantMessage(string Role, string Text)
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    public static AssistantMessage User(string text) => new(UserRole, text);
    public static AssistantMessage Assistant(string text) => new(AssistantRole, text);
}

public sealed record AssistantContext(
    string? HardwareSummary,
    string? HealthSummary,
    string? PerformanceSummary,
    string? SensorSummary,
    string? SoftwareSummary)
{
    public bool IsEmpty => HardwareSummary is null && HealthSummary is null && PerformanceSummary is null && SensorSummary is null && SoftwareSummary is null;
}

public sealed record OverclockAdvisorRequest(
    string HardwareProfileJson,
    OverclockCapabilities Capabilities,
    OverclockSettings Limits,
    string? UserGoal,
    string? PreviousResults);
