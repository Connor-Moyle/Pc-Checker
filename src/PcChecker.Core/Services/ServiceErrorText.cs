namespace PcChecker.Core.Services;

/// <summary>
/// Turns the Win32 error codes the Service Control Manager returns into plain words, so a failed action never shows
/// a bare "error 1051". Pure mapping, unit-tested.
/// </summary>
public static class ServiceErrorText
{
    /// <summary>A plain explanation for <paramref name="code"/>, with the code kept at the end for support.</summary>
    public static string Describe(int code)
    {
        string text = code switch
        {
            5 => "Windows did not allow this. PC Checker needs administrator rights, and some services refuse changes even then.",
            1051 => "Other running services depend on this one. Stop them first, or use Restart, which handles them for you.",
            1052 => "This service does not accept that request.",
            1053 => "The service did not respond in time.",
            1055 => "The service database is locked by another program. Try again in a moment.",
            1056 => "The service is already running.",
            1058 => "The service is disabled. Set its start type to Manual or Automatic first.",
            1059 => "These services depend on each other in a loop, so Windows cannot start them.",
            1060 => "This service no longer exists.",
            1061 => "The service cannot accept requests right now. Wait a moment and try again.",
            1062 => "The service is not running.",
            1065 => "The service database does not exist.",
            1068 => "A service this one depends on could not start.",
            1069 => "The service could not sign in with its account.",
            1072 => "This service is marked for removal and will disappear after a restart.",
            1075 => "A service this one depends on does not exist.",
            1077 => "The service has not been started since the last boot.",
            _ => "Windows refused the request.",
        };

        return $"{text} (code {code})";
    }
}
