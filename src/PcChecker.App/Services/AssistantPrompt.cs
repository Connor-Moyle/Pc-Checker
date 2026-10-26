namespace PcChecker.App.Services;

/// <summary>
/// Hands a pre-written question from any page to the Ask Claude page. The sender calls <see cref="Set"/> and
/// navigates; the Assistant page calls <see cref="Take"/> when it loads (or listens to <see cref="Changed"/>).
/// </summary>
public static class AssistantPrompt
{
    private static readonly object Gate = new();
    private static string? _pending;

    /// <summary>Raised after <see cref="Set"/> so an already-open Assistant page can pick the prompt up immediately.</summary>
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

    public static bool HasPending => Pending is not null;

    public static void Set(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        lock (Gate)
        {
            _pending = prompt.Trim();
        }

        Changed?.Invoke(null, EventArgs.Empty);
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
