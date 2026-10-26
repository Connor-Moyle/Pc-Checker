using System.Text.RegularExpressions;

namespace PcChecker.Core.Startup;

/// <summary>
/// Pulls the executable path out of a startup command line ("C:\App\app.exe" --flag, C:\App\app.exe /x,
/// %LOCALAPPDATA%\App\app.exe, rundll32.exe lib.dll,Entry …). Pure: environment variables are expanded through a
/// caller-supplied resolver so the logic is testable anywhere.
/// </summary>
public static partial class StartupCommandParser
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat", ".cmd", ".lnk", ".scr", ".vbs", ".js", ".ps1", ".msc"];

    /// <summary>Returns the executable path (environment variables expanded) or null when nothing usable is found.</summary>
    public static string? ExtractExecutable(string? command, Func<string, string?>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string text = ExpandEnvironment(command.Trim(), environment);

        // Quoted path first: "C:\Program Files\App\app.exe" args
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            if (close > 1)
            {
                return CleanPath(text[1..close]);
            }

            return CleanPath(text.Trim('"'));
        }

        // Unquoted: walk tokens until one ends with a known extension (handles "C:\Program Files\App\app.exe -x"
        // only when the path has no spaces; spaces without quotes are ambiguous, so prefer the first extension hit).
        int idx = IndexOfExecutableExtension(text);
        if (idx >= 0)
        {
            return CleanPath(text[..idx]);
        }

        // No extension at all: take the first whitespace-separated token.
        int space = text.IndexOf(' ');
        return CleanPath(space > 0 ? text[..space] : text);
    }

    /// <summary>Everything after the executable (arguments), or an empty string.</summary>
    public static string ExtractArguments(string? command, Func<string, string?>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        string text = ExpandEnvironment(command.Trim(), environment);
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            return close > 1 ? text[(close + 1)..].Trim() : string.Empty;
        }

        int idx = IndexOfExecutableExtension(text);
        if (idx >= 0)
        {
            return text[idx..].Trim();
        }

        int space = text.IndexOf(' ');
        return space > 0 ? text[(space + 1)..].Trim() : string.Empty;
    }

    /// <summary>Expands %NAME% tokens using <paramref name="environment"/> (defaults to the process environment). Unknown names are kept.</summary>
    public static string ExpandEnvironment(string text, Func<string, string?>? environment = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('%'))
        {
            return text;
        }

        environment ??= Environment.GetEnvironmentVariable;
        return EnvironmentToken().Replace(text, m =>
        {
            string? value = environment(m.Groups[1].Value);
            return value ?? m.Value;
        });
    }

    /// <summary>File name without directory, or the command itself when it has no path.</summary>
    public static string FileNameOf(string? command, Func<string, string?>? environment = null)
    {
        var exe = ExtractExecutable(command, environment);
        if (exe is null)
        {
            return command?.Trim() ?? string.Empty;
        }

        int slash = exe.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? exe[(slash + 1)..] : exe;
    }

    /// <summary>True when the path has an extension we treat as a program (exe, com, bat, cmd, lnk, scr, scripts).</summary>
    public static bool LooksLikeExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && ExecutableExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static int IndexOfExecutableExtension(string text)
    {
        int best = -1;
        foreach (var ext in ExecutableExtensions)
        {
            int i = 0;
            while ((i = text.IndexOf(ext, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = i + ext.Length;
                bool boundary = end == text.Length || text[end] == ' ' || text[end] == '"' || text[end] == ',';
                if (boundary && (best < 0 || end < best))
                {
                    best = end;
                }

                i = end;
            }
        }

        return best;
    }

    private static string? CleanPath(string path)
    {
        path = path.Trim().Trim('"').Trim();
        return path.Length == 0 ? null : path;
    }

    [GeneratedRegex("%([A-Za-z0-9_()]+)%")]
    private static partial Regex EnvironmentToken();
}
