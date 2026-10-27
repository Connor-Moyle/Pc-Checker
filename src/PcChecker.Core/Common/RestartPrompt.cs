namespace PcChecker.Core.Common;

/// <summary>
/// The one restart policy every "Restart now" button follows: the same countdown, the same shutdown.exe arguments and
/// the same wording, so Drivers, Windows Features, Memory, Startup, Optimize and Performance all behave alike.
/// A scheduled restart is cancelled with <c>shutdown /a</c> (<see cref="CancelArguments"/>).
/// </summary>
public static class RestartPrompt
{
    /// <summary>Seconds between confirming and Windows restarting; the app shows a countdown with Cancel meanwhile.</summary>
    public const int CountdownSeconds = 10;

    /// <summary>shutdown.exe arguments that abort a pending restart.</summary>
    public const string CancelArguments = "/a";

    /// <summary>shutdown.exe limits the /c comment to 512 characters.</summary>
    public const int MaxCommentLength = 500;

    private const string DefaultReason = "to finish the changes you made";

    /// <summary>shutdown.exe arguments for a normal restart after <paramref name="seconds"/>, with a readable comment.</summary>
    public static string RestartArguments(string? reason, int seconds = CountdownSeconds) =>
        $"/r /t {Math.Max(0, seconds)} /c \"{Comment(reason)}\"";

    /// <summary>The comment Windows shows in its own "you're about to be signed out" notice. Quotes and line breaks are removed.</summary>
    public static string Comment(string? reason)
    {
        string text = "PC Checker is restarting Windows " + Reason(reason) + ".";
        text = text.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > MaxCommentLength ? text[..MaxCommentLength] : text;
    }

    /// <summary>Text for the Danger confirmation shown before any restart is scheduled.</summary>
    public static string ConfirmMessage(string? reason, int seconds = CountdownSeconds) =>
        $"Windows will restart {Reason(reason)}. Save your work first.\n\n"
        + $"The restart starts {seconds} seconds after you confirm. Until then you can stop it with Cancel restart (or by running 'shutdown /a').";

    /// <summary>Label for the confirmation button, e.g. "Restart in 10 s".</summary>
    public static string ConfirmButtonText(int seconds = CountdownSeconds) => $"Restart in {seconds} s";

    private static string Reason(string? reason)
    {
        string text = string.IsNullOrWhiteSpace(reason) ? DefaultReason : reason.Trim().TrimEnd('.');
        return text.Length == 0 ? DefaultReason : text;
    }
}
