using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>The local processor name, read once from the registry (null off Windows or when unreadable).</summary>
public static class LocalCpu
{
    private static readonly Lazy<string?> NameValue = new(ReadName);

    public static string? Name => NameValue.Value;

    private static string? ReadName()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            return ReadWindows();
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindows()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim();
    }
}

/// <summary>
/// Reads the facts <see cref="AmdPlatformRules"/> needs on Windows: AMD Chipset Software from the uninstall keys,
/// AMD platform devices from Win32_PnPSignedDriver, the amd3dvcacheSvc service and whether Xbox Game Bar is installed.
/// Read-only; every step is guarded so a blocked WMI class only leaves that fact unknown.
/// </summary>
public static class AmdPlatformProbe
{
    public static AmdPlatformFacts Read(string? cpuName, CancellationToken cancellationToken = default)
    {
        if (!PlatformGuard.IsWindows || !CpuTraits.Parse(cpuName).IsAmd)
        {
            return AmdPlatformFacts.None with { CpuName = cpuName };
        }

        return ReadWindows(cpuName, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static AmdPlatformFacts ReadWindows(string? cpuName, CancellationToken ct)
    {
        string? chipset = null;
        var devices = new List<PlatformDevice>();
        PlatformServiceState? service = null;
        bool? gameBar = null;

        try
        {
            chipset = FindUninstallVersion("AMD Chipset Software");
        }
        catch (Exception)
        {
            // Unknown: the rules treat a missing version together with the device list.
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"\\.\root\CIMV2",
                "SELECT DeviceName, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceName LIKE 'AMD %'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject row in results)
            {
                ct.ThrowIfCancellationRequested();
                using (row)
                {
                    if (row["DeviceName"] is string name && name.Length > 0)
                    {
                        devices.Add(new PlatformDevice(name, row["DriverProviderName"] as string));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Device list unknown.
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + AmdPlatformRules.VCacheServiceName);
            if (key is null)
            {
                service = new PlatformServiceState(false, false, false);
            }
            else
            {
                bool automatic = key.GetValue("Start") is int start && start == 2;
                bool? running = null;
                using var searcher = new ManagementObjectSearcher(@"\\.\root\CIMV2",
                    $"SELECT State FROM Win32_Service WHERE Name = '{AmdPlatformRules.VCacheServiceName}'");
                using var results = searcher.Get();
                foreach (ManagementBaseObject row in results)
                {
                    using (row)
                    {
                        running = string.Equals(row["State"] as string, "Running", StringComparison.OrdinalIgnoreCase);
                    }
                }

                service = new PlatformServiceState(true, running, automatic);
            }
        }
        catch (Exception)
        {
            // Service state unknown.
        }

        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages is not null)
            {
                gameBar = packages.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.XboxGamingOverlay_", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception)
        {
            // Unknown.
        }

        return new AmdPlatformFacts(cpuName, chipset, devices, service, gameBar);
    }

    [SupportedOSPlatform("windows")]
    private static string? FindUninstallVersion(string displayName)
    {
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var uninstall = Registry.LocalMachine.OpenSubKey(root);
            if (uninstall is null)
            {
                continue;
            }

            foreach (var sub in uninstall.GetSubKeyNames())
            {
                using var app = uninstall.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is string name && name.StartsWith(displayName, StringComparison.OrdinalIgnoreCase))
                {
                    return app.GetValue("DisplayVersion") as string ?? "installed";
                }
            }
        }

        return null;
    }
}
