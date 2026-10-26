using System.Diagnostics;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Performance;

/// <summary>Result of running a command-line tool.</summary>
public sealed record CommandResult(int ExitCode, string Output, string Error, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>Output and error text joined, trimmed, for messages.</summary>
    public string Combined => string.Join(Environment.NewLine, new[] { Output, Error }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

    /// <summary>The last non-empty line of output or error text, handy for one-line summaries.</summary>
    public string LastLine
    {
        get
        {
            var lines = Combined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? string.Empty : lines[^1];
        }
    }

    public static CommandResult NotWindows { get; } = new(-1, string.Empty, "This command is only available on Windows.", false);
}

/// <summary>
/// Runs Windows command-line tools (powercfg, fsutil, sc, netsh, DISM, sfc, defrag) without a console window,
/// capturing output line by line. Never throws for a failed command; returns a <see cref="CommandResult"/>.
/// </summary>
public static class ExternalCommand
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public static async Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null, Action<string>? onOutputLine = null, Encoding? outputEncoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!PlatformGuard.IsWindows)
        {
            return CommandResult.NotWindows;
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        if (outputEncoding is not null)
        {
            psi.StandardOutputEncoding = outputEncoding;
            psi.StandardErrorEncoding = outputEncoding;
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);
            if (onOutputLine is not null)
            {
                foreach (var piece in SplitProgressLine(e.Data))
                {
                    onOutputLine(piece);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stderr.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, string.Empty, $"Could not start {fileName}: {ex.Message}", false);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // Some tools close stdin themselves.
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
        return new CommandResult(exitCode, Clean(stdout.ToString()), Clean(stderr.ToString()), timedOut);
    }

    /// <summary>Splits carriage-return progress updates (DISM, sfc) into separate lines and drops empty ones.</summary>
    public static IEnumerable<string> SplitProgressLine(string line)
    {
        foreach (var piece in line.Split('\r'))
        {
            var text = piece.Replace("\0", string.Empty).Trim();
            if (text.Length > 0)
            {
                yield return text;
            }
        }
    }

    private static string Clean(string text) => text.Replace("\0", string.Empty).TrimEnd();

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
