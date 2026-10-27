using System.Diagnostics;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Result of asking winget for available upgrades.</summary>
public sealed record WingetUpgradeResult(bool WingetAvailable, IReadOnlyList<WingetEntry> Upgrades, string? Note);

/// <summary>
/// Optional helper: when the Windows Package Manager (<c>winget</c>) is installed, lists programs with a newer
/// version available. Output is parsed by <see cref="WingetListParser"/>; nothing is installed or changed here.
/// </summary>
public static class WingetUpgradeChecker
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>True when winget can be started from this process.</summary>
    public static bool IsInstalled()
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (File.Exists(Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe")))
            {
                return true;
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            return path.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(dir => File.Exists(Path.Combine(dir.Trim(), "winget.exe")));
        }
        catch
        {
            return false;
        }
    }

    public static async Task<WingetUpgradeResult> CheckAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (!IsInstalled())
        {
            return new WingetUpgradeResult(false, [], "The Windows Package Manager (winget) is not installed, so upgrade checks are unavailable. It ships with the App Installer from the Microsoft Store.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = "upgrade --accept-source-agreements --disable-interactivity --include-unknown",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, _) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

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

                return new WingetUpgradeResult(true, [], "winget did not answer in time. Try again later.");
            }

            return Interpret(stdout.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WingetUpgradeResult(true, [], "winget could not be run: " + ex.Message);
        }
    }

    /// <summary>
    /// Turns <c>winget upgrade</c> output into a result. "Up to date" is only claimed when a table was read, or when
    /// winget said so in English; output that could not be read is reported as such instead.
    /// </summary>
    public static WingetUpgradeResult Interpret(string? output)
    {
        var entries = WingetListParser.TryParse(output);
        if (entries is null)
        {
            bool nothingToUpgrade = output is not null
                && (output.Contains("No installed package found", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("No applicable update", StringComparison.OrdinalIgnoreCase));
            return new WingetUpgradeResult(true, [], nothingToUpgrade
                ? "Everything winget knows about is up to date."
                : "winget's answer could not be read, so it is not known whether updates are available. Run \"winget upgrade\" in a terminal to see the list.");
        }

        var upgrades = WingetListParser.UpgradesOnly(entries);
        return new WingetUpgradeResult(true, upgrades, upgrades.Count == 0 ? "Everything winget knows about is up to date." : null);
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
