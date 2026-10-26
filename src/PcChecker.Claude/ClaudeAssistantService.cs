using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Anthropic.Models.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Assistant;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Settings;

namespace PcChecker.Claude;

/// <summary>
/// Claude-backed <see cref="IPcAssistantService"/> built on the official Anthropic SDK. Uses the beta Messages
/// endpoint so the server-side refusal fallback can be enabled; chat answers stream token by token, the overclock
/// advisor asks for structured JSON that is parsed tolerantly and clamped to the user's limits.
/// </summary>
public sealed class ClaudeAssistantService : IPcAssistantService, IDisposable
{
    public const string EnvironmentKeyVariable = "ANTHROPIC_API_KEY";
    private const string NotConfiguredText = "The assistant is not configured yet. Add your Anthropic API key under Ask Claude settings.";

    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly ILogger<ClaudeAssistantService> _logger;
    private readonly object _gate = new();
    private AnthropicClient? _client;
    private string? _clientKey;
    private string? _cachedProtectedKey;
    private string? _cachedPlainKey;

    public ClaudeAssistantService(ISettingsService settings, ISecretStore secrets, ILogger<ClaudeAssistantService>? logger = null)
    {
        _settings = settings;
        _secrets = secrets;
        _logger = logger ?? NullLogger<ClaudeAssistantService>.Instance;
    }

    public bool IsConfigured => ResolveApiKey() is not null;

    /// <summary>True when the key comes from the <c>ANTHROPIC_API_KEY</c> environment variable rather than settings.</summary>
    public bool UsesEnvironmentKey => ResolveStoredKey() is null && ResolveEnvironmentKey() is not null;

    public async Task<OperationResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var key = ResolveApiKey();
        if (key is null)
        {
            return OperationResult.Fail(NotConfiguredText);
        }

        var modelId = ResolveModelId();
        try
        {
            var client = GetClient(key);
            var info = await client.Models.Retrieve(modelId, new ModelRetrieveParams(), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Claude connection test succeeded for model {Model}.", modelId);
            var name = string.IsNullOrWhiteSpace(info.DisplayName) ? info.ID : info.DisplayName;
            return OperationResult.Ok($"Connected. {name} is ready to answer.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Claude connection test failed ({Type}).", ex.GetType().Name);
            return OperationResult.Fail(AssistantErrorMapper.Describe(ex), ex);
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<AssistantMessage> history, AssistantContext? context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = ResolveApiKey();
        if (key is null)
        {
            throw new AssistantException(NotConfiguredText);
        }

        var turns = ClaudeRequestBuilder.NormalizeHistory(history);
        if (!ClaudeRequestBuilder.CanSend(turns))
        {
            throw new AssistantException("Type a question first.");
        }

        var parameters = BuildParams(AssistantPrompts.BuildChatSystemText(context), turns, structured: false);
        var client = GetClient(key);
        _logger.LogInformation("Claude chat request: {Turns} turns, model {Model}, context attached: {HasContext}.",
            turns.Count, ResolveModelId(), context is { IsEmpty: false });

        var stream = client.Beta.Messages.CreateStreaming(parameters, cancellationToken);
        var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        long outputTokens = 0;
        try
        {
            while (true)
            {
                string? chunk = null;
                bool done = false;
                AssistantException? failure = null;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        done = true;
                    }
                    else
                    {
                        chunk = ExtractChunk(enumerator.Current, ref outputTokens);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Claude chat request failed ({Type}).", ex.GetType().Name);
                    failure = new AssistantException(AssistantErrorMapper.Describe(ex), ex, AssistantErrorMapper.IsOffline(ex));
                }

                if (failure is not null)
                {
                    throw failure;
                }

                if (done)
                {
                    _logger.LogInformation("Claude chat answer finished ({Tokens} output tokens).", outputTokens);
                    yield break;
                }

                if (!string.IsNullOrEmpty(chunk))
                {
                    yield return chunk;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<OperationResult<OverclockPlan>> CreateOverclockPlanAsync(OverclockAdvisorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = ResolveApiKey();
        if (key is null)
        {
            return OperationResult<OverclockPlan>.Fail(NotConfiguredText);
        }

        var turns = new[] { new ChatTurn(AssistantMessage.UserRole, AssistantPrompts.BuildOverclockRequest(request)) };
        var parameters = BuildParams(AssistantPrompts.OverclockAdvisorSystem, turns, structured: true);
        string text;
        try
        {
            var client = GetClient(key);
            _logger.LogInformation("Claude overclock advisor request for {Gpu}/{Cpu}.", request.Capabilities.GpuName ?? "no GPU", request.Capabilities.CpuName ?? "no CPU");
            var message = await client.Beta.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
            if (IsRefusal(message.StopReason?.Raw()))
            {
                return OperationResult<OverclockPlan>.Fail(AssistantPrompts.RefusalMessage(message.StopDetails?.Explanation));
            }

            text = CollectText(message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude overclock advisor request failed ({Type}).", ex.GetType().Name);
            return OperationResult<OverclockPlan>.Fail(AssistantErrorMapper.Describe(ex), ex);
        }

        var plan = OverclockPlanParser.TryParse(text);
        if (plan is null)
        {
            _logger.LogWarning("Claude overclock advisor answer could not be parsed ({Length} chars).", text.Length);
            return OperationResult<OverclockPlan>.Fail("Claude's answer could not be read as a plan. Try again, or ask with a more specific goal.");
        }

        return OperationResult<OverclockPlan>.Ok(ClampPlan(plan, request));
    }

    public async Task<OperationResult<string>> ExplainAsync(string subject, string details, CancellationToken cancellationToken)
    {
        var key = ResolveApiKey();
        if (key is null)
        {
            return OperationResult<string>.Fail(NotConfiguredText);
        }

        var turns = new[] { new ChatTurn(AssistantMessage.UserRole, AssistantPrompts.BuildExplainPrompt(subject, details)) };
        var parameters = BuildParams(AssistantPrompts.System, turns, structured: false, maxTokens: 1200);
        try
        {
            var client = GetClient(key);
            _logger.LogInformation("Claude explain request ({Subject}).", subject);
            var message = await client.Beta.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
            if (IsRefusal(message.StopReason?.Raw()))
            {
                return OperationResult<string>.Fail(AssistantPrompts.RefusalMessage(message.StopDetails?.Explanation));
            }

            var text = CollectText(message).Trim();
            return text.Length == 0
                ? OperationResult<string>.Fail("Claude sent an empty answer. Try again.")
                : OperationResult<string>.Ok(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude explain request failed ({Type}).", ex.GetType().Name);
            return OperationResult<string>.Fail(AssistantErrorMapper.Describe(ex), ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
            _clientKey = null;
        }
    }

    /// <summary>Clamps every step into the user's and hardware's limits; adds a warning when anything changed.</summary>
    public static OverclockPlan ClampPlan(OverclockPlan plan, OverclockAdvisorRequest request)
    {
        bool anyClamped = false;
        var steps = new List<OverclockStep>(plan.Steps.Count);
        foreach (var step in plan.Steps.OrderBy(s => s.Order))
        {
            var clamped = OverclockSafetyValidator.Clamp(step.Target, request.Limits, request.Capabilities.GpuLimits);
            clamped.Name = step.Target.Name;
            if (!SameValues(clamped, step.Target))
            {
                anyClamped = true;
            }

            steps.Add(step with { Target = clamped, StressSeconds = Math.Max(step.StressSeconds, 60) });
        }

        var warnings = plan.Warnings.ToList();
        if (anyClamped)
        {
            warnings.Insert(0, "Some values suggested by Claude were above your safety limits and have been reduced to fit them.");
        }

        return plan with { Steps = steps, Warnings = warnings };
    }

    private static bool SameValues(OverclockProfile a, OverclockProfile b) =>
        a.GpuCoreOffsetMhz == b.GpuCoreOffsetMhz && a.GpuMemoryOffsetMhz == b.GpuMemoryOffsetMhz && a.GpuPowerLimitPercent == b.GpuPowerLimitPercent
        && a.GpuTempLimitC == b.GpuTempLimitC && a.GpuFanPercent == b.GpuFanPercent && a.CpuAllCoreRatio == b.CpuAllCoreRatio
        && a.CpuVoltageOffsetMv == b.CpuVoltageOffsetMv && a.CpuPowerLimitW == b.CpuPowerLimitW;

    private static bool IsRefusal(string? stopReason) => string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase);

    private static string CollectText(BetaMessage message)
    {
        if (message.Content is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var text) && !string.IsNullOrEmpty(text.Text))
            {
                parts.Add(text.Text);
            }
        }

        return string.Concat(parts);
    }

    /// <summary>Pulls the text (or a refusal notice) out of one stream event.</summary>
    private static string? ExtractChunk(BetaRawMessageStreamEvent streamEvent, ref long outputTokens)
    {
        if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
        {
            return blockDelta.Delta is not null && blockDelta.Delta.TryPickText(out var text) ? text.Text : null;
        }

        if (streamEvent.TryPickDelta(out var messageDelta))
        {
            if (messageDelta.Usage is not null)
            {
                outputTokens = messageDelta.Usage.OutputTokens;
            }

            var reason = messageDelta.Delta?.StopReason?.Raw();
            if (IsRefusal(reason))
            {
                return "\n\n" + AssistantPrompts.RefusalMessage(messageDelta.Delta?.StopDetails?.Explanation);
            }

            if (string.Equals(reason, "max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                return "\n\n(The answer was cut short because it reached the length limit. Ask a follow-up to continue.)";
            }
        }

        return null;
    }

    private MessageCreateParams BuildParams(string systemText, IReadOnlyList<ChatTurn> turns, bool structured, int maxTokens = ClaudeRequestBuilder.MaxTokens)
    {
        var claude = _settings.Current.Claude;
        var output = structured
            ? new BetaOutputConfig { Effort = MapEffort(claude.Effort), Format = new BetaJsonOutputFormat { Schema = OverclockPlanSchema.AsDictionary() } }
            : new BetaOutputConfig { Effort = MapEffort(claude.Effort) };

        var messages = new List<BetaMessageParam>(turns.Count);
        foreach (var turn in turns)
        {
            messages.Add(turn.IsUser ? new BetaUserMessageParam(turn.Text) : new BetaAssistantMessageParam(turn.Text));
        }

        return new MessageCreateParams
        {
            Model = ResolveModelId(),
            MaxTokens = maxTokens,
            System = new List<BetaTextBlockParam> { new(systemText) { CacheControl = new BetaCacheControlEphemeral() } },
            Messages = messages,
            OutputConfig = output,
            // Server-side fallback: when the chosen model refuses, the API retries with its default fallback model.
            Betas = new List<Anthropic.Core.ApiEnum<string, AnthropicBeta>> { AnthropicBeta.ServerSideFallback2026_07_01 },
            Fallbacks = new Default(),
        };
    }

    private static Effort MapEffort(AssistantEffort effort) => effort switch
    {
        AssistantEffort.Low => Effort.Low,
        AssistantEffort.High => Effort.High,
        _ => Effort.Medium,
    };

    private string ResolveModelId()
    {
        var model = _settings.Current.Claude.Model;
        return AssistantModelCatalog.IsPlausibleModelId(model) ? model.Trim() : AssistantModelCatalog.DefaultModelId;
    }

    private string? ResolveApiKey() => ResolveStoredKey() ?? ResolveEnvironmentKey();

    private static string? ResolveEnvironmentKey()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentKeyVariable);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    private string? ResolveStoredKey()
    {
        var protectedKey = _settings.Current.Claude.ProtectedApiKey;
        if (string.IsNullOrWhiteSpace(protectedKey))
        {
            return null;
        }

        lock (_gate)
        {
            if (_cachedProtectedKey == protectedKey)
            {
                return _cachedPlainKey;
            }
        }

        string? plain = null;
        try
        {
            var key = _secrets.Unprotect(protectedKey);
            plain = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
        catch (Exception ex)
        {
            // A key protected by another Windows user or machine cannot be read; treat as not configured.
            _logger.LogWarning(ex, "The stored API key could not be decrypted; it will be ignored.");
        }

        lock (_gate)
        {
            _cachedProtectedKey = protectedKey;
            _cachedPlainKey = plain;
        }

        return plain;
    }

    /// <summary>Returns a client for <paramref name="key"/>, rebuilding it when the key changed.</summary>
    private AnthropicClient GetClient(string key)
    {
        lock (_gate)
        {
            if (_client is null || !string.Equals(_clientKey, key, StringComparison.Ordinal))
            {
                _client?.Dispose();
                _client = new AnthropicClient
                {
                    ApiKey = key,
                    MaxRetries = 2,
                    Timeout = TimeSpan.FromMinutes(5),
                };
                _clientKey = key;
            }

            return _client;
        }
    }
}
