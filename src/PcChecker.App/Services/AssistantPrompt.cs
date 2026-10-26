namespace PcChecker.App.Services;

/// <summary>
/// Hands a pre-written question from any page to the Ask Claude page. A sender calls <see cref="Set"/> (or
/// <see cref="Ask"/> / <see cref="AskToExplain"/>) and navigates; the Assistant page calls <see cref="Take"/> when it
/// loads, or listens to <see cref="Changed"/> when it is already open.
/// </summary>
public static class AssistantPrompt
{
    private static readonly object Gate = new();
    private static string? _pending;

    /// <summary>Raised after a prompt is queued so an already-open Assistant page can pick it up immediately.</summary>
    public static event EventHandler? Changed;

    /// <summary>The waiting prompt, or null when there is none. Reading does not clear it; use <see cref="Take"/> for that.</summary>
    public static string? Pending
    {
        get
        {
            lock (Gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Same as <see cref="Pending"/>; setting it queues (or clears, when blank) the question.</summary>
    public static string? PendingQuestion
    {
        get => Pending;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Clear();
            }
            else
            {
                Set(value);
            }
        }
    }

    public static bool HasPending => Pending is not null;

    /// <summary>Queues a question for the Assistant page and notifies listeners.</summary>
    public static void Set(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        lock (Gate)
        {
            _pending = prompt.Trim();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Queues a question for the Assistant page.</summary>
    public static void Ask(string question) => Set(question);

    /// <summary>Builds a question about a finding ("Explain …") and queues it.</summary>
    public static void AskToExplain(string subject, string? details = null)
    {
        var text = string.IsNullOrWhiteSpace(details)
            ? $"Explain this finding from PC Checker in plain English and tell me whether I should do something about it: {subject}"
            : $"Explain this finding from PC Checker in plain English and tell me whether I should do something about it.\n\n{subject}\n{details}";
        Set(text);
    }

    /// <summary>Returns the waiting prompt and clears it.</summary>
    public static string? Take()
    {
        lock (Gate)
        {
            var prompt = _pending;
            _pending = null;
            return prompt;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _pending = null;
        }
    }
}
