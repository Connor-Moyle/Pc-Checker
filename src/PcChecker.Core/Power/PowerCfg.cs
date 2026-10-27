using System.Diagnostics;
using System.Text;
using PcChecker.Core.Common;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Power;

/// <summary>Result of one <c>powercfg</c> call.</summary>
public sealed record PowerCfgResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Runs <c>powercfg.exe</c> without a window and captures its output (decoded from the OEM code page). Never throws for a failed call.</summary>
public static class PowerCfg
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static Task<PowerCfgResult> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments);
        return RunCoreAsync(psi => psi.Arguments = arguments, cancellationToken);
    }

    /// <summary>
    /// Runs powercfg with each argument quoted for it (<see cref="ProcessStartInfo.ArgumentList"/>), for arguments
    /// that hold paths.
    /// </summary>
    public static Task<PowerCfgResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new ArgumentException("At least one argument is required.", nameof(arguments));
        }

        return RunCoreAsync(psi =>
        {
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }
        }, cancellationToken);
    }

    /// <summary>powercfg.exe in System32, so the elevated app never starts a copy that sits in its own folder.</summary>
    public static string ExecutablePath =>
        PlatformGuard.IsWindows ? Path.Combine(Environment.SystemDirectory, "powercfg.exe") : "powercfg.exe";

    private static async Task<PowerCfgResult> RunCoreAsync(Action<ProcessStartInfo> setArguments, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return new PowerCfgResult(-1, string.Empty, "powercfg is only available on Windows.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,

            // powercfg writes in the console (OEM) code page, not UTF-8: decoding it as UTF-8 garbles every
            // non-ASCII plan name ("Höchstleistung", Cyrillic names) on non-English Windows.
            StandardOutputEncoding = StorageCommandRunner.OemEncoding(),
            StandardErrorEncoding = StorageCommandRunner.OemEncoding(),
        };
        setArguments(psi);

        try
        {
            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // Already gone.
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new PowerCfgResult(-2, stdout.ToString(), "powercfg did not respond in time.");
            }

            return new PowerCfgResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PowerCfgResult(-1, string.Empty, ex.Message);
        }
    }
}
