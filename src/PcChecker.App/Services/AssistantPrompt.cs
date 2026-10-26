namespace PcChecker.App.Services;

/// <summary>
/// Static holder that lets any page prefill a question for the Ask Claude page before navigating to it.
/// The sender assigns <see cref="PendingQuestion"/> (or calls <see cref="Set"/>) and navigates; the Assistant
/// page reads and clears it when it loads (<see cref="Take"/>) or reacts to <see cref="Changed"/> when it is
/// already open.
/// </summary>
public static class AssistantPrompt
{
    private static readonly object Gate = new();
    private static string? _pendingQuestion;

    /// <summary>Raised after the question changes so an already-open Assistant page can pick it up immediately.</summary>
    public static event EventHandler? Changed;

    /// <summary>The question waiting to be sent, or null when there is none. Assigning raises <see cref="Changed"/>.</summary>
    public static string? PendingQuestion
    {
        get
        {
            lock (Gate)
            {
                return _pendingQuestion;
            }
        }
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            lock (Gate)
            {
                _pendingQuestion = trimmed;
            }

            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool HasPending => PendingQuestion is not null;

    /// <summary>Stores a non-empty question; equivalent to assigning <see cref="PendingQuestion"/>.</summary>
    public static void Set(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        PendingQuestion = question;
    }

    /// <summary>Returns the waiting question and clears it, without raising <see cref="Changed"/>.</summary>
    public static string? Take()
    {
        lock (Gate)
        {
            var question = _pendingQuestion;
            _pendingQuestion = null;
            return question;
        }
    }
}
