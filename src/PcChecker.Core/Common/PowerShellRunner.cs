using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Common;

/// <summary>Result of a PowerShell invocation.</summary>
public sealed record PowerShellResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Runs Windows PowerShell commands non-interactively and captures their output.
/// Uses <c>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command</c>.
/// </summary>
public static partial class PowerShellRunner
{
    /// <summary>The first line Windows PowerShell writes when it serializes the error stream as CLIXML.</summary>
    public const string CliXmlMarker = "#< CLIXML";

    [GeneratedRegex(@"<S S=""Error"">(.*?)</S>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CliXmlErrorString();

    [GeneratedRegex(@"_x([0-9A-Fa-f]{4})_", RegexOptions.CultureInvariant)]
    private static partial Regex CliXmlCharEscape();

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Runs a command and captures stdout/stderr/exit code. Never throws for a non-zero exit code.</summary>
    public static async Task<PowerShellResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!PlatformGuard.IsWindows)
        {
            return new PowerShellResult(-1, string.Empty, "PowerShell is only available on Windows.", false);
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(WithUtf8OutputPrelude(command)));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new PowerShellResult(-1, string.Empty, ex.Message, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            TryKill(process);
            if (!timedOut)
            {
                throw;
            }
        }

        int exitCode = timedOut ? -2 : SafeExitCode(process);
        return new PowerShellResult(exitCode, stdout.ToString().TrimEnd(), DecodeCliXml(stderr.ToString()).TrimEnd(), timedOut);
    }

    /// <summary>
    /// Runs a command that produces objects, appends <c>| ConvertTo-Json -Depth 4 -Compress</c> and deserializes the result.
    /// Returns default when the command fails or the output is empty.
    /// </summary>
    public static async Task<T?> RunJsonAsync<T>(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync($"({command}) | ConvertTo-Json -Depth 4 -Compress", timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return default;
        }

        return ParseJson<T>(result.StdOut);
    }

    /// <summary>Deserializes JSON emitted by ConvertTo-Json, wrapping a single object in an array when a list is expected.</summary>
    public static T? ParseJson<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        json = json.Trim();
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // ConvertTo-Json returns a bare object for single results; retry as a one-element array.
            if (json.StartsWith('{') && typeof(T).IsArray || typeof(T).IsGenericType)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>("[" + json + "]", JsonOptions);
                }
                catch (JsonException)
                {
                    return default;
                }
            }

            return default;
        }
    }

    /// <summary>
    /// Prefixes a command so Windows PowerShell writes redirected stdout/stderr as UTF-8.
    /// Without this, powershell.exe emits the OEM code page and every non-ASCII character
    /// decoded with <see cref="Encoding.UTF8"/> would become U+FFFD.
    /// </summary>
    public static string WithUtf8OutputPrelude(string command) =>
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " + command;

    /// <summary>
    /// With <c>-EncodedCommand</c> and a redirected error stream, Windows PowerShell writes errors (and progress
    /// records) as a CLIXML blob after a <c>#&lt; CLIXML</c> line instead of plain text. Returns the error messages as
    /// plain lines, drops progress and other records, and keeps any plain text written before the blob. Text without
    /// the marker is returned unchanged.
    /// </summary>
    public static string DecodeCliXml(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return string.Empty;
        }

        int marker = stderr.IndexOf(CliXmlMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return stderr;
        }

        var text = new StringBuilder();
        foreach (Match match in CliXmlErrorString().Matches(stderr, marker))
        {
            var xmlDecoded = WebUtility.HtmlDecode(match.Groups[1].Value);
            text.Append(CliXmlCharEscape().Replace(xmlDecoded, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()));
        }

        var lines = (stderr[..marker] + "\n" + text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Escapes a string for use inside a single-quoted PowerShell literal.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
