using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Win32;
using PcChecker.Core.Common;

namespace PcChecker.Core.Lighting;

/// <summary>Finds an OpenRGB installation and checks whether its SDK server is listening. Every Windows call is guarded.</summary>
public static class OpenRgbInstallLocator
{
    /// <summary>Well-known install folders, relative to the environment's program folders.</summary>
    public static IReadOnlyList<string> CandidatePaths()
    {
        var list = new List<string>();
        void Add(string? root, params string[] parts)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                list.Add(Path.Combine([root, .. parts]));
            }
        }

        Add(Environment.GetEnvironmentVariable("ProgramFiles"), "OpenRGB", "OpenRGB.exe");
        Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), "OpenRGB", "OpenRGB.exe");
        Add(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Programs", "OpenRGB", "OpenRGB.exe");
        Add(Environment.GetEnvironmentVariable("ProgramW6432"), "OpenRGB", "OpenRGB.exe");
        Add(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Microsoft", "WinGet", "Packages", "OpenRGB.OpenRGB_Microsoft.Winget.Source_8wekyb3d8bbwe", "OpenRGB.exe");
        // winget installs OpenRGB's zip as a "portable" package: machine scope under Program Files, user scope under AppData.
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("LOCALAPPDATA") is { } local ? Path.Combine(local, "Microsoft") : null })
        {
            foreach (var exe in WingetPortableCandidates(root))
            {
                list.Add(exe);
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Folders only administrators can change; PC Checker (running as administrator) only starts OpenRGB from these.</summary>
    public static IReadOnlyList<string?> ProtectedRoots() =>
    [
        Environment.GetEnvironmentVariable("ProgramFiles"),
        Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
        Environment.GetEnvironmentVariable("ProgramW6432"),
    ];

    /// <summary>OpenRGB.exe inside winget's portable package folders (CalcProgrammer1.OpenRGB_…), up to two folders deep.</summary>
    private static IEnumerable<string> WingetPortableCandidates(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            yield break;
        }

        string packages = Path.Combine(root, "WinGet", "Packages");
        List<string> folders;
        try
        {
            folders = Directory.Exists(packages)
                ? Directory.EnumerateDirectories(packages, OpenRgbInstallRules.WingetPackageId + "*").ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            folders = [];
        }

        foreach (var folder in folders)
        {
            yield return Path.Combine(folder, "OpenRGB.exe");
            List<string> subs;
            try
            {
                subs = Directory.EnumerateDirectories(folder).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var sub in subs)
            {
                yield return Path.Combine(sub, "OpenRGB.exe");
            }
        }
    }

    /// <summary>Full path to OpenRGB.exe, or null when it is not installed (or we are not on Windows).</summary>
    public static string? Find()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        foreach (var candidate in CandidatePaths())
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Inaccessible folder; keep looking.
            }
        }

        return FindInUninstallKeys() ?? FindOnPath();
    }

    /// <summary>Scans the uninstall registry keys for a DisplayName containing "OpenRGB" and returns its executable.</summary>
    public static string? FindInUninstallKeys()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        string[] keys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var key in keys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var uninstall = baseKey.OpenSubKey(key);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var sub in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(sub);
                        var name = app?.GetValue("DisplayName") as string;
                        if (name is null || !name.Contains("OpenRGB", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var location = app?.GetValue("InstallLocation") as string;
                        var exe = ResolveExecutable(location, app?.GetValue("DisplayIcon") as string);
                        if (exe is not null)
                        {
                            return exe;
                        }
                    }
                }
                catch
                {
                    // Registry access denied or hive missing: fall through to other strategies.
                }
            }
        }

        return null;
    }

    /// <summary>Picks the executable from an InstallLocation folder or a DisplayIcon path. Pure (only checks existence).</summary>
    public static string? ResolveExecutable(string? installLocation, string? displayIcon)
    {
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            var exe = Path.Combine(installLocation.Trim().Trim('"'), "OpenRGB.exe");
            if (SafeExists(exe))
            {
                return exe;
            }
        }

        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var icon = displayIcon.Trim();
            int comma = icon.LastIndexOf(',');
            if (comma > 0 && icon[(comma + 1)..].All(c => char.IsDigit(c) || c == '-'))
            {
                icon = icon[..comma];
            }

            icon = icon.Trim().Trim('"');
            if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && SafeExists(icon))
            {
                return icon;
            }
        }

        return null;
    }

    private static string? FindOnPath()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var exe = Path.Combine(dir.Trim(), "OpenRGB.exe");
                if (SafeExists(exe))
                {
                    return exe;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when something accepts TCP connections on host:port within the timeout.</summary>
    public static async Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when an OpenRGB process is running (by process name), best effort.</summary>
    public static bool IsProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("OpenRGB").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts OpenRGB with its SDK server enabled and minimized, only from a protected folder (Program Files).
    /// Returns the error text when it cannot or may not start.
    /// </summary>
    public static OperationResult StartServer(string executablePath, int port)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("OpenRGB can only be started on Windows.");
        }

        try
        {
            if (!OpenRgbInstallRules.MayStart(executablePath, ProtectedRoots()))
            {
                return OperationResult.Fail(OpenRgbInstallRules.StartRefusal(executablePath));
            }

            var psi = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            };
            psi.ArgumentList.Add("--server");
            psi.ArgumentList.Add("--server-port");
            psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--startminimized");
            using var started = Process.Start(psi);
            return OperationResult.Ok("OpenRGB is starting with its SDK server enabled.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"OpenRGB could not be started: {ex.Message}", ex);
        }
    }
}
