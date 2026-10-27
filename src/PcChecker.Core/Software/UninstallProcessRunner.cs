using System.Diagnostics;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;

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

        fileName = FromSystemFolder(fileName);
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

    /// <summary>How long to wait for an uninstaller started through the desktop to show up as a process.</summary>
    public static readonly TimeSpan UnelevatedStartWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Starts the uninstaller with the signed-in user's normal rights (through File Explorer, see
    /// <see cref="UnelevatedLauncher"/>) and waits until no process from that file is left running. The exit code
    /// cannot be read this way, so the caller checks whether the program is gone. Never falls back to starting it with
    /// administrator rights; an uninstaller that needs them asks Windows for them itself.
    /// </summary>
    public static async Task<OperationResult> RunUnelevatedAsync(string fileName, string arguments, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Uninstallers can only be run on Windows.");
        }

        fileName = FromSystemFolder(fileName);
        try
        {
            UnelevatedLauncher.Open(fileName, arguments ?? string.Empty, SafeDirectory(fileName));
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(
                $"The uninstaller could not be started without administrator rights ({ex.Message}). Uninstall it from Windows Settings → Apps → Installed apps instead.", ex);
        }

        progress?.Report(new ProgressUpdate("Waiting for the uninstaller window", null, fileName));

        // The desktop starts it a moment later; one that never shows up (it finished at once, or is still waiting at a
        // Windows permission prompt) is judged by whether the program is gone.
        var appearBy = DateTime.UtcNow + UnelevatedStartWindow;
        while (!IsRunning(fileName))
        {
            if (DateTime.UtcNow >= appearBy)
            {
                return OperationResult.Ok();
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (IsRunning(fileName))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return OperationResult.Fail("The uninstaller did not finish within the time limit. Close its window and scan again.");
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult.Ok();
    }

    /// <summary>A bare tool name (msiexec.exe, rundll32.exe) resolves to System32, never to the app's own folder.</summary>
    private static string FromSystemFolder(string fileName)
    {
        try
        {
            if (!Path.IsPathRooted(fileName) && fileName.IndexOfAny(['\\', '/']) < 0)
            {
                var system = Path.Combine(Environment.SystemDirectory,
                    fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe");
                if (File.Exists(system))
                {
                    return system;
                }
            }
        }
        catch
        {
            // Keep the name as given.
        }

        return fileName;
    }

    private static bool IsRunning(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        bool rooted = Path.IsPathRooted(fileName);
        bool found = false;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (found)
                {
                    continue;
                }

                try
                {
                    found = !rooted || string.Equals(process.MainModule?.FileName, fileName, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    // A process that cannot be inspected is not this uninstaller, which runs with the user's rights.
                }
            }
        }

        return found;
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
