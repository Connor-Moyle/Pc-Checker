namespace PcChecker.App.Services;

/// <summary>
/// Static holder that lets any page prefill a question for the Ask Claude page before navigating to it.
/// The sender assigns <see cref="PendingQuestion"/> and navigates; the Assistant page reads and clears it when it loads.
/// </summary>
public static class AssistantPrompt
{
    public static string? PendingQuestion { get; set; }
}
