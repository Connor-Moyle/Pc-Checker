using System.Diagnostics;
using System.Globalization;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Storage;

/// <summary>Outcome of a console tool run (chkdsk, PowerShell).</summary>
public sealed record CommandRunResult(int ExitCode, string Output, bool TimedOut, string? StartError)
{
    public bool Started => StartError is null;
}

/// <summary>
/// Runs a console tool, streams every output line to a callback as it appears (so chkdsk progress shows live),
/// optionally feeds it standard input, and kills it on cancellation or timeout. Never throws for a failing tool.
/// </summary>
public static class StorageCommandRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(6);

    /// <param name="outputEncoding">How the tool's output is decoded; null keeps the process default. Console tools such as chkdsk write in the OEM code page (<see cref="OemEncoding"/>).</param>
    public static async Task<CommandRunResult> RunAsync(string fileName, string arguments, string? standardInput, Action<string>? onLine,
        TimeSpan? timeout, CancellationToken cancellationToken, Encoding? outputEncoding = null)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new CommandRunResult(-1, string.Empty, false, "Windows tools are only available on Windows.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true,
        };
        if (outputEncoding is not null)
        {
            psi.StandardOutputEncoding = outputEncoding;
            psi.StandardErrorEncoding = outputEncoding;
            if (standardInput is not null)
            {
                // Answers such as a localized "yes" letter must reach the tool in the code page it reads and prints in.
                psi.StandardInputEncoding = outputEncoding;
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var gate = new object();

        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                output.AppendLine(line);
            }

            try
            {
                onLine?.Invoke(line);
            }
            catch
            {
                // A UI callback must never break the tool run.
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandRunResult(-1, string.Empty, false, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (standardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch
            {
                // The tool may have exited before reading its input; that is fine.
            }
        }

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

        int exitCode;
        try
        {
            exitCode = timedOut ? -2 : process.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        string text;
        lock (gate)
        {
            text = output.ToString().TrimEnd();
        }

        return new CommandRunResult(exitCode, text, timedOut, null);
    }

    /// <summary>Runs a PowerShell command line with verbose output redirected into the normal stream so it can be shown live. Output is UTF-8 on both ends.</summary>
    public static Task<CommandRunResult> RunPowerShellAsync(string command, Action<string>? onLine, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " + command));
        return RunAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", null, onLine, timeout, cancellationToken, Encoding.UTF8);
    }

    private static Encoding? _oem;

    /// <summary>
    /// The OEM code page console tools write in (for example 437 or 850 in the US and Western Europe, 866 for Cyrillic);
    /// null when it cannot be determined, in which case the process default is used.
    /// </summary>
    public static Encoding? OemEncoding()
    {
        if (_oem is not null)
        {
            return _oem;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            return _oem = Encoding.GetEncoding(codePage);
        }
        catch
        {
            return null;
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
