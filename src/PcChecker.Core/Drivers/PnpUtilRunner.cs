using System.Diagnostics;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>Outcome of one <c>pnputil.exe</c> run.</summary>
public sealed record PnpUtilResult(int ExitCode, string Output, bool TimedOut, string? StartError)
{
    public bool Started => StartError is null;
}

/// <summary>
/// Runs <c>pnputil.exe</c> (the Windows driver store tool) and streams its output. Never throws for a failed run;
/// returns <see cref="PnpUtilResult.StartError"/> when the tool could not be launched at all.
/// </summary>
public static class PnpUtilRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Full path to pnputil.exe in the system folder.</summary>
    public static string ExecutablePath => Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    /// <summary>
    /// Runs pnputil with <paramref name="arguments"/>. Each output line is passed to <paramref name="onLine"/> as it
    /// arrives (progress), and the full output is returned at the end.
    /// </summary>
    public static async Task<PnpUtilResult> RunAsync(string arguments, Action<string>? onLine = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments);
        if (!PlatformGuard.IsWindows)
        {
            return new PnpUtilResult(-1, string.Empty, false, "pnputil is only available on Windows.");
        }

        var exe = ExecutablePath;
        if (!File.Exists(exe))
        {
            return new PnpUtilResult(-1, string.Empty, false, $"pnputil.exe was not found at {exe}.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var gate = new object();
        void Capture(string? data)
        {
            if (data is null)
            {
                return;
            }

            lock (gate)
            {
                output.AppendLine(data);
            }

            try
            {
                onLine?.Invoke(data);
            }
            catch
            {
                // Progress callbacks must never break the run.
            }
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new PnpUtilResult(-1, string.Empty, false, ex.Message);
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

        return new PnpUtilResult(exitCode, text, timedOut, null);
    }

    /// <summary>Quotes a path for the pnputil command line.</summary>
    public static string Quote(string path) => "\"" + path.Trim().TrimEnd('\\') + "\"";

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
