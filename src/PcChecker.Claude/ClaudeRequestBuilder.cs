using PcChecker.Core.Assistant;

namespace PcChecker.Claude;

/// <summary>A normalized chat turn ready to become an SDK message parameter.</summary>
public sealed record ChatTurn(string Role, string Text)
{
    public bool IsUser => Role == AssistantMessage.UserRole;
}

/// <summary>
/// Pure preparation of chat history for the API: drops blank messages, merges consecutive messages with the same
/// role (the API requires alternation), makes sure the conversation starts with the user, and caps the length so
/// very long chats do not exceed the context window.
/// </summary>
public static class ClaudeRequestBuilder
{
    public const int MaxTokens = 8000;
    public const int DefaultMaxHistoryChars = 60_000;

    public static IReadOnlyList<ChatTurn> NormalizeHistory(IReadOnlyList<AssistantMessage>? history, int maxChars = DefaultMaxHistoryChars)
    {
        if (history is null || history.Count == 0)
        {
            return [];
        }

        var turns = new List<ChatTurn>();
        foreach (var message in history)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            var role = message.Role == AssistantMessage.AssistantRole ? AssistantMessage.AssistantRole : AssistantMessage.UserRole;
            var text = message.Text.Trim();
            if (turns.Count > 0 && turns[^1].Role == role)
            {
                turns[^1] = turns[^1] with { Text = turns[^1].Text + "\n\n" + text };
            }
            else
            {
                turns.Add(new ChatTurn(role, text));
            }
        }

        // The conversation must open with the user.
        while (turns.Count > 0 && !turns[0].IsUser)
        {
            turns.RemoveAt(0);
        }

        // Trim the oldest exchanges when the transcript is too long, keeping the last user turn and alternation intact.
        int total = turns.Sum(t => t.Text.Length);
        while (turns.Count > 2 && total > Math.Max(1000, maxChars))
        {
            total -= turns[0].Text.Length + turns[1].Text.Length;
            turns.RemoveRange(0, 2);
        }

        return turns;
    }

    /// <summary>True when the history can be sent: at least one turn and the last one is from the user.</summary>
    public static bool CanSend(IReadOnlyList<ChatTurn> turns) => turns.Count > 0 && turns[^1].IsUser;
}
