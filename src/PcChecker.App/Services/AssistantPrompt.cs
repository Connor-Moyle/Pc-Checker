namespace PcChecker.App.Services;

/// <summary>
/// Lets any page hand a question to the Ask Claude page before navigating to it: set <see cref="PendingQuestion"/>
/// (or call <see cref="Ask"/>), navigate, and the Assistant page sends it on arrival.
/// </summary>
public static class AssistantPrompt
{
    private static readonly object Gate = new();
    private static string? _pending;

    /// <summary>The question waiting to be sent, or null.</summary>
    public static string? PendingQuestion
    {
        get
        {
            lock (Gate)
            {
                return _pending;
            }
        }

        set
        {
            lock (Gate)
            {
                _pending = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
    }

    /// <summary>Queues a question for the Assistant page.</summary>
    public static void Ask(string question) => PendingQuestion = question;

    /// <summary>Builds a question about a finding ("Explain …") and queues it.</summary>
    public static void AskToExplain(string subject, string? details = null)
    {
        var text = string.IsNullOrWhiteSpace(details)
            ? $"Explain this finding from PC Checker in plain English and tell me whether I should do something about it: {subject}"
            : $"Explain this finding from PC Checker in plain English and tell me whether I should do something about it.\n\n{subject}\n{details}";
        PendingQuestion = text;
    }

    /// <summary>Returns the pending question and clears it.</summary>
    public static string? Take()
    {
        lock (Gate)
        {
            var value = _pending;
            _pending = null;
            return value;
        }
    }
}
