using System.Net;
using System.Net.Sockets;
using Anthropic.Exceptions;

namespace PcChecker.Claude;

/// <summary>Maps SDK and network exceptions to short, friendly messages. Pure; unit-tested.</summary>
public static class AssistantErrorMapper
{
    public const string ApiKeyRejected = "Your API key was rejected (401). Check the key under Ask Claude settings, or create a new one at console.anthropic.com.";
    public const string RateLimited = "Claude is rate limited right now (429). Wait a moment and try again, or pick a lower effort level.";
    public const string Offline = "Could not reach Claude. Check your internet connection, VPN or firewall and try again.";
    public const string TimedOut = "Claude took too long to answer and the request timed out. Try again, or ask a shorter question.";
    public const string Cancelled = "Stopped.";

    /// <summary>Returns a friendly message for <paramref name="exception"/>.</summary>
    public static string Describe(Exception? exception)
    {
        switch (exception)
        {
            case null:
                return "Something went wrong talking to Claude.";
            case AnthropicUnauthorizedException:
                return ApiKeyRejected;
            case AnthropicRateLimitException:
                return RateLimited;
            case Anthropic4xxException e4:
                return Describe4xx(e4);
            case Anthropic5xxException e5:
                return $"Claude's servers had a problem ({(int)e5.StatusCode}). This is usually temporary; try again in a minute.";
            case AnthropicApiException api:
                return $"Claude returned an error ({(int)api.StatusCode}): {Trim(api.Message)}";
            case AnthropicIOException:
                return Offline;
            case TaskCanceledException { CancellationToken.IsCancellationRequested: false }:
                return TimedOut;
            case OperationCanceledException:
                return Cancelled;
            case HttpRequestException http:
                return IsOffline(http) ? Offline : $"Network error talking to Claude: {Trim(http.Message)}";
            case SocketException:
                return Offline;
            case AnthropicException other:
                return other.InnerException is { } inner && inner is not AnthropicException ? Describe(inner) : $"Claude error: {Trim(other.Message)}";
            default:
                return exception.InnerException is { } nested && nested != exception && (nested is HttpRequestException or SocketException or AnthropicException)
                    ? Describe(nested)
                    : $"Something went wrong talking to Claude: {Trim(exception.Message)}";
        }
    }

    /// <summary>True when the exception looks like a connectivity problem rather than an API answer.</summary>
    public static bool IsOffline(Exception? exception) => exception switch
    {
        null => false,
        AnthropicIOException => true,
        SocketException => true,
        HttpRequestException http => http.StatusCode is null || http.InnerException is SocketException,
        _ => exception.InnerException is { } inner && inner != exception && IsOffline(inner),
    };

    private static string Describe4xx(Anthropic4xxException e)
    {
        int code = (int)e.StatusCode;
        return e.StatusCode switch
        {
            HttpStatusCode.BadRequest => $"Claude rejected the request (400). The model id in settings may be wrong, or the message was too long: {Trim(e.Message)}",
            HttpStatusCode.PaymentRequired => "Your Anthropic account has no credit (402). Add credit at console.anthropic.com and try again.",
            HttpStatusCode.Forbidden => "This API key is not allowed to use that model (403). Pick another model or check the key's permissions.",
            HttpStatusCode.NotFound => "The selected model was not found (404). Pick another model under Ask Claude settings.",
            HttpStatusCode.RequestEntityTooLarge => "The request was too large (413). Lower the context size in settings or clear the chat.",
            _ => $"Claude returned an error ({code}): {Trim(e.Message)}",
        };
    }

    private static string Trim(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "no details were given.";
        }

        var text = message.Trim();
        return text.Length > 240 ? text[..240] + "…" : text;
    }
}
