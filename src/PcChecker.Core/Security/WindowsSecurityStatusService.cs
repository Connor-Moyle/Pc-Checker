using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.Restore;
using PcChecker.Core.WindowsFeatures;
using static PcChecker.Core.Security.SecurityWmi;

namespace PcChecker.Core.Security;

/// <summary>
/// Windows implementation of <see cref="ISecurityStatusService"/>. Reading uses WMI and the registry on a background
/// thread; Defender actions run Microsoft's own <c>MpCmdRun.exe</c> from Program Files; the firewall fix runs
/// <c>Set-NetFirewallProfile</c> and SMB 1.0 is turned off through the Windows Features service, each guarded.
/// </summary>
public sealed class WindowsSecurityStatusService : ISecurityStatusService
{
    private const string ModuleName = "Security";
    private static readonly string[] KnownProfiles = ["Domain", "Private", "Public"];
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromHours(8);

    private readonly IChangeGuard _guard;
    private readonly IWindowsFeatureService _features;
    private readonly ILogger<WindowsSecurityStatusService> _logger;

    public WindowsSecurityStatusService(IChangeGuard guard, IWindowsFeatureService features, ILogger<WindowsSecurityStatusService> logger)
    {
        _guard = guard;
        _features = features;
        _logger = logger;
    }

    public Task<SecurityFacts> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => WindowsSecurityReader.Read(DateTimeOffset.Now, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<ThreatDetection>> GetRecentThreatsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<ThreatDetection>>(() =>
        {
            var detections = Query(Defender, "SELECT ThreatID, InitialDetectionTime, ThreatStatusID, Resources, ProcessName FROM MSFT_MpThreatDetection");
            if (detections is null)
            {
                return [];
            }

            cancellationToken.ThrowIfCancellationRequested();
            var threats = (Query(Defender, "SELECT ThreatID, ThreatName, SeverityID FROM MSFT_MpThreat") ?? [])
                .Where(r => Long(r, "ThreatID") is not null)
                .GroupBy(r => Long(r, "ThreatID")!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            return detections
                .Select(d =>
                {
                    long id = Long(d, "ThreatID") ?? 0;
                    threats.TryGetValue(id, out var threat);
                    string? resource = d.TryGetValue("Resources", out var r) && r is string[] { Length: > 0 } list ? list[0] : null;
                    return new ThreatDetection(id,
                        (threat is null ? null : Str(threat, "ThreatName")) ?? $"Threat {id}",
                        threat is null ? 0 : Int(threat, "SeverityID") ?? 0,
                        Int(d, "ThreatStatusID") ?? 0,
                        Date(d, "InitialDetectionTime"),
                        DefenderText.CleanResource(resource),
                        Str(d, "ProcessName"));
                })
                .OrderByDescending(t => t.DetectedAt ?? DateTimeOffset.MinValue)
                .Take(50)
                .ToList();
        }, cancellationToken);

    public Task<OperationResult> UpdateDefinitionsAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var options = new ChangeOptions(false, false, ModuleName, "Microsoft Defender definitions", Reversible: false,
            UndoHint: "Not needed: newer definitions only add protection.");
        return _guard.RunGuardedAsync("Update Microsoft Defender definitions", options, async ct =>
        {
            progress?.Report(new ProgressUpdate("Downloading the newest definitions…", null));
            var (exitCode, timedOut) = await RunMpCmdRunAsync(["-SignatureUpdate"], UpdateTimeout, null, ct).ConfigureAwait(false);
            if (timedOut)
            {
                return OperationResult.Fail("The definition update took too long and was stopped. Try again from Windows Security.");
            }

            return exitCode == 0
                ? OperationResult.Ok("Microsoft Defender's definitions are up to date.")
                : OperationResult.Fail($"The definition update did not finish (code {exitCode}). Check the internet connection, or update from Windows Security.");
        }, progress, cancellationToken);
    }

    public async Task<OperationResult> RunScanAsync(DefenderScanType type, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string name = type == DefenderScanType.Full ? "Full scan" : "Quick scan";
        try
        {
            var (exitCode, timedOut) = await RunMpCmdRunAsync(["-Scan", "-ScanType", ((int)type).ToString(System.Globalization.CultureInfo.InvariantCulture)],
                ScanTimeout, elapsed => progress?.Report(new ProgressUpdate($"{name} running… {(int)elapsed.TotalMinutes} min {elapsed.Seconds} s", null)),
                cancellationToken).ConfigureAwait(false);
            if (timedOut)
            {
                return OperationResult.Fail($"The {name.ToLowerInvariant()} took too long and was stopped.");
            }

            return exitCode == 0
                ? OperationResult.Ok($"{name} finished. Microsoft Defender found nothing that needs you.")
                : OperationResult.Fail($"{name} finished with code {exitCode}. Open Protection history to see what Microsoft Defender found or could not do.");
        }
        catch (OperationCanceledException)
        {
            // Stop the scan itself, not only our wait for it.
            await RunMpCmdRunAsync(["-Scan", "-Cancel"], TimeSpan.FromSeconds(30), null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<OperationResult> TurnOnFirewallAsync(IReadOnlyList<string> profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var chosen = KnownProfiles.Where(k => profiles.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (chosen.Count == 0)
        {
            return Task.FromResult(OperationResult.Fail("No firewall profile to turn on."));
        }

        string list = string.Join(",", chosen);
        var options = ChangeOptions.For(ModuleName, $"Windows Firewall ({list})", reversible: false,
            undoHint: "Not offered: turning the firewall off is only possible in Windows Security.");
        return _guard.RunGuardedAsync($"Turn on Windows Firewall for {list}", options, async ct =>
        {
            var result = await PowerShellRunner.RunAsync($"Set-NetFirewallProfile -Profile {list} -Enabled True -ErrorAction Stop", TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            return result.Success
                ? OperationResult.Ok($"Windows Firewall is on again for {list}.")
                : OperationResult.Fail("Windows Firewall could not be turned on. " + PowerShellRunner.DecodeCliXml(result.StdErr).Trim());
        }, null, cancellationToken);
    }

    public Task<OperationResult> TurnOffSmb1Async(CancellationToken cancellationToken)
    {
        var feature = new WindowsFeature("SMB1Protocol", "SMB 1.0/CIFS File Sharing Support", "Very old file-sharing protocol.",
            FeatureKind.OptionalFeature, FeatureState.Enabled, RiskLevel.Advanced, false,
            "Keep disabled: SMB1 is insecure. Enable only for an ancient device that cannot be updated.", null, false);
        return _features.DisableAsync(feature, cancellationToken);
    }

    public OperationResult OpenSetting(string link)
    {
        if (!SecurityLinks.IsAllowed(link))
        {
            return OperationResult.Fail("That setting cannot be opened from here.");
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (link.StartsWith("tool:", StringComparison.Ordinal) && SecurityLinks.ToolPath(link, windows) is { } tool)
        {
            return ShellLauncher.Open(tool.Path, tool.Arguments);
        }

        return ShellLauncher.Open(link);
    }

    private static string MpCmdRunPath()
    {
        string programFiles = Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } p
            ? p
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(programFiles, "Windows Defender", "MpCmdRun.exe");
    }

    private async Task<(int ExitCode, bool TimedOut)> RunMpCmdRunAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        Action<TimeSpan>? tick, CancellationToken cancellationToken)
    {
        string path = MpCmdRunPath();
        if (!PlatformGuard.IsWindows || !File.Exists(path))
        {
            throw new InvalidOperationException("Microsoft Defender's command-line tool was not found on this PC.");
        }

        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("MpCmdRun.exe could not be started.");
        _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var watch = Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            while (!process.HasExited)
            {
                tick?.Invoke(watch.Elapsed);
                try
                {
                    await process.WaitForExitAsync(linked.Token).WaitAsync(TimeSpan.FromSeconds(1), linked.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Report progress once a second.
                }
            }

            return (process.ExitCode, false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (-1, true);
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogDebug(ex, "MpCmdRun.exe had already exited.");
        }
    }
}
