using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Restore;
using PcChecker.Core.Sensors.PawnIo;

namespace PcChecker.Core.Lighting;

/// <summary>
/// The Windows detection pass: SetupAPI device IDs, HID attributes and usages, WMI memory part numbers and board maker,
/// the running process and service lists, and the PawnIO status. Every step is guarded; a failed step is reported in
/// <see cref="RgbScanResult.Problems"/> and the others still run. The two changes it can make (closing a vendor app for
/// this session, installing OpenRGB) go through the change guard after the page's confirmation.
/// </summary>
public sealed class WindowsRgbHardwareService : IRgbHardwareService
{
    private const string Module = "Lighting";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    private readonly IChangeGuard _guard;
    private readonly IPawnIoService _pawnIo;
    private readonly ILogger<WindowsRgbHardwareService> _logger;

    public WindowsRgbHardwareService(IChangeGuard guard, IPawnIoService pawnIo, ILogger<WindowsRgbHardwareService> logger)
    {
        _guard = guard;
        _pawnIo = pawnIo;
        _logger = logger;
    }

    public Task<RgbScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(RgbScanResult.Empty);
        }

        return Task.Run(() => ScanCore(cancellationToken), cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private RgbScanResult ScanCore(CancellationToken ct)
    {
        var problems = new List<string>();
        var devices = Step("USB and PCI devices", problems, () => RgbDeviceEnumerator.PresentDevices(ct)) ?? [];
        var (extraIds, lamps) = Step("HID devices", problems, () => RgbDeviceEnumerator.HidInterfaces(ct));
        var memory = Step("memory modules", problems, ReadMemory) ?? [];
        var board = Step("motherboard", problems, ReadBoard);
        ct.ThrowIfCancellationRequested();

        var input = new RgbScanInput(devices.Concat(extraIds ?? []).ToList(), lamps ?? [], memory, board?.Manufacturer, board?.Product);
        var findings = RgbDetectionRules.Build(input);
        bool smbus = devices.Any(d => RgbDetectionRules.IsSmbusController(d.HardwareIds));

        var conflicts = Step("running RGB apps", problems, DetectConflicts) ?? [];
        bool? smbusDriver = null;
        try
        {
            var status = _pawnIo.GetStatus();
            smbusDriver = status.State == PawnIoState.Unknown ? null : status.IsRunning;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PawnIO status check failed during the RGB scan.");
        }

        _logger.LogInformation("RGB scan: {Findings} RGB devices found ({Devices} devices checked), {Conflicts} vendor RGB apps running, SMBus {Smbus}.",
            findings.Count, devices.Count, conflicts.Count, smbus ? "present" : "not listed");
        return new RgbScanResult(findings, conflicts, smbus, smbusDriver, IsAdministrator(), problems);
    }

    private T? Step<T>(string what, List<string> problems, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RGB scan: could not read {What}.", what);
            problems.Add($"Could not read the {what}: {ex.Message}");
            return default;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<RgbMemoryModule> ReadMemory()
    {
        var list = new List<RgbMemoryModule>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceLocator, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
        using var results = searcher.Get();
        foreach (var item in results.Cast<ManagementObject>())
        {
            using (item)
            {
                list.Add(new RgbMemoryModule(item["DeviceLocator"] as string, item["Manufacturer"] as string, item["PartNumber"] as string));
            }
        }

        return list;
    }

    [SupportedOSPlatform("windows")]
    private static (string? Manufacturer, string? Product)? ReadBoard()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
        using var results = searcher.Get();
        foreach (var item in results.Cast<ManagementObject>())
        {
            using (item)
            {
                return (item["Manufacturer"] as string, item["Product"] as string);
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RgbConflict> DetectConflicts()
    {
        var names = new HashSet<string>(RgbConflictRules.Catalog.SelectMany(a => a.ProcessNames), StringComparer.OrdinalIgnoreCase);
        var processes = new List<RgbRunningProcess>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string normalized = RgbConflictRules.NormalizeProcessName(p.ProcessName);
                    if (!names.Contains(normalized))
                    {
                        continue;
                    }

                    string? path = null;
                    try
                    {
                        path = p.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Protected or 32/64-bit mismatch: the name is enough except for apps with a path hint.
                    }

                    processes.Add(new RgbRunningProcess(p.ProcessName, p.Id, path));
                }
                catch (InvalidOperationException)
                {
                    // Exited while we looked.
                }
            }
        }

        return RgbConflictRules.Detect(processes, RunningServices());
    }

    [SupportedOSPlatform("windows")]
    private static List<string> RunningServices()
    {
        var wanted = RgbConflictRules.Catalog.SelectMany(a => a.ServiceNames).ToList();
        var running = new List<string>();
        if (wanted.Count == 0)
        {
            return running;
        }

        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Service WHERE State = 'Running'");
        using var results = searcher.Get();
        foreach (var item in results.Cast<ManagementObject>())
        {
            using (item)
            {
                if (item["Name"] is string name && wanted.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    running.Add(name);
                }
            }
        }

        return running;
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Task<OperationResult> CloseForSessionAsync(RgbConflict conflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Apps can only be closed on Windows."));
        }

        // Only names from PC Checker's own catalog are ever stopped.
        if (!RgbConflictRules.Catalog.Contains(conflict.App))
        {
            return Task.FromResult(OperationResult.Fail("That app is not one PC Checker knows how to close safely."));
        }

        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, conflict.App.Product, Reversible: true,
            UndoHint: $"Open {conflict.App.Product} again from the Start menu, or restart the PC; its services start again with Windows.");
        return _guard.RunGuardedAsync($"Close {conflict.App.Product} for this session", options,
            ct => Task.Run(() => CloseCore(conflict, ct), ct), null, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private OperationResult CloseCore(RgbConflict conflict, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var service in conflict.Services.Where(s => conflict.App.ServiceNames.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (!StopService(service, out var why))
            {
                failures.Add($"service {service}: {why}");
            }
        }

        foreach (var process in conflict.Processes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var p = Process.GetProcessById(process.Pid);
                // The PID may have been reused since the scan: only end it when it is still the same program.
                if (!string.Equals(RgbConflictRules.NormalizeProcessName(p.ProcessName), RgbConflictRules.NormalizeProcessName(process.Name), StringComparison.Ordinal))
                {
                    continue;
                }

                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                // Already gone (closed with its service).
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                failures.Add($"{process.Name}: {ex.Message}");
            }
        }

        _logger.LogInformation("Closed {App} for this session ({Failures} problems).", conflict.App.Product, failures.Count);
        return failures.Count == 0
            ? OperationResult.Ok($"{conflict.App.Product} is closed until you restart the PC or open it again. Press Rescan to look for the devices it held.")
            : OperationResult.Fail($"{conflict.App.Product} was not fully closed ({string.Join("; ", failures)}). Close it from its tray icon instead.");
    }

    [SupportedOSPlatform("windows")]
    private static bool StopService(string name, out string why)
    {
        why = string.Empty;
        try
        {
            using var service = new ManagementObject($"Win32_Service.Name='{name.Replace("'", string.Empty, StringComparison.Ordinal)}'");
            var code = Convert.ToUInt32(service.InvokeMethod("StopService", null), System.Globalization.CultureInfo.InvariantCulture);
            // 0 = stopped, 5 = not running / cannot accept control now (already stopping).
            if (code is 0 or 5)
            {
                return true;
            }

            why = $"Windows answered {code}";
            return false;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException or FormatException)
        {
            why = ex.Message;
            return false;
        }
    }

    public async Task<bool> CanInstallOpenRgbAsync(CancellationToken cancellationToken) =>
        PlatformGuard.IsWindows && await new OemToolDetector(_logger).FindWingetAsync(cancellationToken).ConfigureAwait(false) is not null;

    public async Task<OperationResult> InstallOpenRgbAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("OpenRGB can only be installed on Windows.");
        }

        var winget = await new OemToolDetector(_logger).FindWingetAsync(cancellationToken).ConfigureAwait(false);
        if (winget is null)
        {
            return OperationResult.Fail("The Windows Package Manager (winget) was not found. Download OpenRGB from openrgb.org instead.");
        }

        var command = OpenRgbInstallRules.WingetInstall(winget);
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "OpenRGB", Reversible: true,
            UndoHint: "Settings → Apps → Installed apps → OpenRGB → Uninstall (or: winget uninstall " + OpenRgbInstallRules.WingetPackageId + ").");
        return await _guard.RunGuardedAsync("Install OpenRGB (RGB lighting control)", options, async ct =>
        {
            progress?.Report(new ProgressUpdate("Installing OpenRGB with winget", null, command.Display));
            _logger.LogInformation("Installing OpenRGB: {Command}", command.Display);
            var (exitCode, error) = await RunAsync(command, ct).ConfigureAwait(false);
            if (exitCode is not { } code)
            {
                return OperationResult.Fail("winget could not be run: " + error);
            }

            var (ok, reboot, problem) = OpenRgbInstallRules.InterpretWingetExit(code);
            return ok
                ? OperationResult.Ok(reboot ? "OpenRGB is installed. Restart the PC to finish." : "OpenRGB is installed. Press Start OpenRGB to run it with its SDK server.", reboot)
                : OperationResult.Fail(problem ?? "winget did not install OpenRGB.");
        }, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int? ExitCode, string? Error)> RunAsync(InstallCommand command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(command.FileName) ?? Environment.SystemDirectory,
        };
        foreach (var argument in command.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (null, "the program did not start.");
            }

            using var timeout = new CancellationTokenSource(InstallTimeout);
            try
            {
                // An install that has started is left to finish when the user cancels.
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, $"it did not finish within {InstallTimeout.TotalMinutes} minutes.");
            }

            return (process.ExitCode, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Could not start {Program}.", command.FileName);
            return (null, ex.Message);
        }
    }
}
