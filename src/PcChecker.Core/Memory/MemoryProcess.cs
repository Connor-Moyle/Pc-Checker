using System.Diagnostics;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Memory;

/// <summary>Result of running a command-line tool such as <c>bcdedit</c> or <c>sc</c>.</summary>
public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>The most useful line to show a user: stderr, then stdout, then the exit code.</summary>
    public string Detail => !string.IsNullOrWhiteSpace(StdErr) ? StdErr.Trim() : !string.IsNullOrWhiteSpace(StdOut) ? StdOut.Trim() : $"exit code {ExitCode}";
}

/// <summary>Runs a system tool without a window and captures its output. Never throws for a failing tool.</summary>
public static class MemoryProcess
{
    public static async Task<ProcessRunResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new ProcessRunResult(-1, string.Empty, $"{fileName} is only available on Windows.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
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
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return new ProcessRunResult(-1, string.Empty, ex.Message);
        }

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessRunResult(-2, stdout.ToString(), $"{fileName} did not finish in time.");
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        return new ProcessRunResult(exitCode, stdout.ToString(), stderr.ToString());
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
