namespace PcChecker.Core.Assistant;

/// <summary>One Claude model the user can pick on the Ask Claude page.</summary>
public sealed record AssistantModelOption(string Id, string Name, string Description);

/// <summary>Curated model list, suggested questions and other small constants shared by the page and the service.</summary>
public static class AssistantModelCatalog
{
    public const string DefaultModelId = "claude-opus-5-5";

    /// <summary>Models offered in the UI. Any other id stored in settings is still honoured.</summary>
    public static IReadOnlyList<AssistantModelOption> Models { get; } =
    [
        new(DefaultModelId, "Claude Opus 5.5", "Best answers and the most careful advice. Slowest and costs the most per question."),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Fast and capable; a good everyday choice."),
        new("claude-haiku-5-5", "Claude Haiku 5.5", "Quickest and cheapest. Fine for short questions."),
    ];

    /// <summary>Questions offered as one-click chips when the chat is empty.</summary>
    public static IReadOnlyList<string> SuggestedQuestions { get; } =
    [
        "Why is my PC slow?",
        "Is my disk healthy?",
        "What should I update first?",
        "Explain my health report",
        "Are my temperatures normal?",
        "Which startup programs can I turn off?",
    ];

    /// <summary>Returns the catalog entry for <paramref name="id"/>, or a synthetic entry so unknown ids still display.</summary>
    public static AssistantModelOption Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Models[0];
        }

        var known = Models.FirstOrDefault(m => m.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        return known ?? new AssistantModelOption(id.Trim(), id.Trim(), "Custom model id from settings.");
    }

    /// <summary>True when <paramref name="id"/> looks like a usable model id (lowercase letters, digits, dots and dashes).</summary>
    public static bool IsPlausibleModelId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');
}
