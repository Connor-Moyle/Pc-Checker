namespace PcChecker.Core.Assistant;

/// <summary>
/// Raised by an <see cref="IPcAssistantService"/> when a request fails. The message is already friendly enough to show
/// in the UI; <see cref="IsOffline"/> tells the page whether to suggest checking the connection.
/// </summary>
public sealed class AssistantException : Exception
{
    public AssistantException(string message, Exception? innerException = null, bool isOffline = false)
        : base(message, innerException)
    {
        IsOffline = isOffline;
    }

    public bool IsOffline { get; }
}
