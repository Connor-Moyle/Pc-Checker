using System.Diagnostics;
using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Starts an uninstaller, waits for it (cancellation kills it) and returns the exit code.</summary>
public static class UninstallProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Runs the command and returns its exit code, or an explanatory failure when it cannot start.</summary>
    public static async Task<OperationResult<int>> RunAsync(string fileName, string arguments, bool quiet, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult<int>.Fail("Uninstallers can only be run on Windows.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = quiet,
            WorkingDirectory = SafeDirectory(fileName),
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return OperationResult<int>.Fail($"The uninstaller could not be started ({fileName}): {ex.Message}", ex);
        }

        progress?.Report(new ProgressUpdate(quiet ? "Uninstalling in the background" : "Waiting for the uninstaller window", null, fileName));

        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultTimeout);
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

            return OperationResult<int>.Fail("The uninstaller did not finish within the time limit and was stopped.");
        }

        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (Exception ex)
        {
            return OperationResult<int>.Fail("The uninstaller finished but its result could not be read: " + ex.Message, ex);
        }

        return OperationResult<int>.Ok(code, $"Exit code {code}");
    }

    private static string? SafeDirectory(string fileName)
    {
        try
        {
            var dir = Path.GetDirectoryName(fileName);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
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
