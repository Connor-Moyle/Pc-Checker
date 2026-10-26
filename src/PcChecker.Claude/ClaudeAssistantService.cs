// TODO(assistant): assistant module — Anthropic SDK streaming chat, overclock advisor, explain.
using System.Runtime.CompilerServices;
using Anthropic;
using PcChecker.Core.Assistant;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Settings;

namespace PcChecker.Claude;

/// <summary>Claude-backed <see cref="IPcAssistantService"/> built on the official Anthropic SDK.</summary>
public sealed class ClaudeAssistantService : IPcAssistantService
{
    private const string NotConfigured = "Not configured";

    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;

    public ClaudeAssistantService(ISettingsService settings, ISecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public bool IsConfigured => false;

    public Task<OperationResult> TestConnectionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotConfigured));

    public async IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<AssistantMessage> history, AssistantContext? context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return "The assistant is not configured yet. Add your Anthropic API key in Settings.";
    }

    public Task<OperationResult<OverclockPlan>> CreateOverclockPlanAsync(OverclockAdvisorRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<OverclockPlan>.Fail(NotConfigured));

    public Task<OperationResult<string>> ExplainAsync(string subject, string details, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<string>.Fail(NotConfigured));

    /// <summary>Creates an SDK client from the protected key in settings, or null when no key is stored.</summary>
    private AnthropicClient? TryCreateClient()
    {
        var protectedKey = _settings.Current.Claude.ProtectedApiKey;
        if (string.IsNullOrWhiteSpace(protectedKey))
        {
            return null;
        }

        try
        {
            var key = _secrets.Unprotect(protectedKey);
            return string.IsNullOrWhiteSpace(key) ? null : new AnthropicClient { ApiKey = key };
        }
        catch
        {
            return null;
        }
    }
}
