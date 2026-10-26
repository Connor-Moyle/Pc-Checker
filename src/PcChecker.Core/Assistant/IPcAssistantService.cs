using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;

namespace PcChecker.Core.Assistant;

/// <summary>Chat and advisor features backed by Claude.</summary>
public interface IPcAssistantService
{
    bool IsConfigured { get; }
    Task<OperationResult> TestConnectionAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<AssistantMessage> history, AssistantContext? context, CancellationToken cancellationToken);
    Task<OperationResult<OverclockPlan>> CreateOverclockPlanAsync(OverclockAdvisorRequest request, CancellationToken cancellationToken);
    Task<OperationResult<string>> ExplainAsync(string subject, string details, CancellationToken cancellationToken);
}
