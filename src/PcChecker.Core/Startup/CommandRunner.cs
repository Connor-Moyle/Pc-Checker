using System.Diagnostics;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Startup;

/// <summary>Output of a console tool such as bcdedit, sc or shutdown.</summary>
public sealed record CommandOutput(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>The most useful line for an error message.</summary>
    public string FirstLine
    {
        get
        {
            string text = string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        }
    }
}

/// <summary>Runs a Windows console tool without a window and captures its output. Never throws for a failed tool.</summary>
public static class CommandRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static async Task<CommandOutput> RunAsync(string fileName, string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new CommandOutput(-1, string.Empty, $"{fileName} is only available on Windows.", false);
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
        }
        catch (Exception ex)
        {
            return new CommandOutput(-1, string.Empty, ex.Message, false);
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

            if (!timedOut)
            {
                throw;
            }
        }

        int exit;
        try
        {
            exit = timedOut ? -2 : process.ExitCode;
        }
        catch
        {
            exit = -1;
        }

        return new CommandOutput(exit, stdout.ToString().TrimEnd(), stderr.ToString().TrimEnd(), timedOut);
    }

    /// <summary>Starts a tool and does not wait (used for shutdown /r which must outlive the app).</summary>
    public static OperationResult Launch(string fileName, string arguments)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail($"{fileName} is only available on Windows.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true });
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not run {fileName}: {ex.Message}", ex);
        }
    }
}
