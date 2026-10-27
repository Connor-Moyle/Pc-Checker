using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Sensors.PawnIo;

/// <summary>
/// Pure rules for the PawnIO driver: classifying what the probe saw, the winget and setup command lines, which download
/// hosts are trusted, picking the setup asset from a GitHub release and reading installer exit codes.
/// </summary>
/// <remarks>
/// LibreHardwareMonitorLib 0.9.6 reads the CPU and the motherboard Super I/O / embedded controller only through PawnIO:
/// it opens <c>\\?\GLOBALROOT\Device\PawnIO</c> and loads its own modules into it. When that open fails it silently reads
/// zeros, so no fan controller chip is found and only the graphics card fans (read through the graphics driver) appear.
/// Its own <c>PawnIo.IsInstalled</c> only reads the uninstall entry once per process, so it cannot tell "installed but not
/// started" apart from "running", and it stays false after an install in the same session. This probe checks the device.
/// </remarks>
public static partial class PawnIoRules
{
    /// <summary>The winget package id of the official PawnIO installer.</summary>
    public const string WingetPackageId = "namazso.PawnIO";

    /// <summary>The GitHub repository that hosts the official signed PawnIO releases.</summary>
    public const string ReleaseRepository = "namazso/PawnIO.Setup";

    /// <summary>The GitHub API address of the latest official release.</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/" + ReleaseRepository + "/releases/latest";

    /// <summary>The installer file name in every PawnIO.Setup release.</summary>
    public const string SetupAssetName = "PawnIO_setup.exe";

    /// <summary>The kernel device LibreHardwareMonitor opens.</summary>
    public const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    /// <summary>The driver service the installer registers.</summary>
    public const string ServiceName = "PawnIO";

    /// <summary>The uninstall entry the installer writes (its DisplayVersion is the installed version).</summary>
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary>
    /// The setup's documented silent switches (the same ones the winget manifest for namazso.PawnIO uses:
    /// <c>InstallerSwitches.Silent: -install -silent</c>).
    /// </summary>
    public static IReadOnlyList<string> SilentInstallSwitches { get; } = ["-install", "-silent"];

    /// <summary>Hosts a PawnIO download may come from or be redirected through (HTTPS only).</summary>
    public static IReadOnlyList<string> TrustedDownloadHosts { get; } =
    [
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    /// <summary>Exit code an installer returns when it worked but Windows must restart (ERROR_SUCCESS_REBOOT_REQUIRED).</summary>
    public const int RebootRequiredExitCode = 3010;

    /// <summary>winget: the package is already installed (APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED).</summary>
    public const int WingetAlreadyInstalled = unchecked((int)0x8A150061);

    /// <summary>winget: installed, a restart finishes it (APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_REQUIRED_TO_FINISH).</summary>
    public const int WingetRebootToFinish = unchecked((int)0x8A150109);

    /// <summary>winget: installed, a restart is needed to install (APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_REQUIRED_TO_INSTALL).</summary>
    public const int WingetRebootToInstall = unchecked((int)0x8A15010A);

    /// <summary>winget: no package matched the id (APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND).</summary>
    public const int WingetNoPackageFound = unchecked((int)0x8A150014);

    /// <summary>
    /// Decides the state. The device open is definitive: when it opens, PawnIO runs whatever the registry says. Without
    /// it, an uninstall entry or a driver service means "installed but not running"; neither means "not installed".
    /// </summary>
    public static PawnIoState Classify(bool isWindows, string? uninstallVersion, bool? serviceRegistered, bool? deviceOpens)
    {
        if (!isWindows)
        {
            return PawnIoState.NotSupported;
        }

        if (deviceOpens == true)
        {
            return PawnIoState.Running;
        }

        bool installed = !string.IsNullOrWhiteSpace(uninstallVersion) || serviceRegistered == true;
        if (deviceOpens == false)
        {
            return installed ? PawnIoState.InstalledNotRunning : PawnIoState.NotInstalled;
        }

        // The device check said nothing either way.
        if (installed)
        {
            return PawnIoState.InstalledNotRunning;
        }

        return serviceRegistered == false && uninstallVersion is null ? PawnIoState.NotInstalled : PawnIoState.Unknown;
    }

    /// <summary>
    /// What a failed <c>CreateFile</c> on the PawnIO device means: "file/path not found" = not running, "access denied"
    /// or "sharing violation" = the device exists (it runs), anything else = cannot tell.
    /// </summary>
    public static bool? DeviceOpenResult(int win32Error) => win32Error switch
    {
        0 => true,
        2 or 3 => false, // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
        5 or 32 => true, // ERROR_ACCESS_DENIED, ERROR_SHARING_VIOLATION: the device is there
        _ => null,
    };

    public static string Describe(PawnIoStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        string version = string.IsNullOrWhiteSpace(status.Version) ? string.Empty : $" (version {status.Version})";
        return status.State switch
        {
            PawnIoState.Running => $"PawnIO is installed and running{version}.",
            PawnIoState.InstalledNotRunning => $"PawnIO is installed{version} but its driver is not running. Restart the PC; if it still does not start, reinstall PawnIO and check that Memory integrity or antivirus software is not blocking it.",
            PawnIoState.NotInstalled => "PawnIO is not installed. The CPU and the motherboard sensor chip (CPU and case fan headers) cannot be read without it.",
            PawnIoState.NotSupported => "PawnIO only applies to Windows.",
            _ => "Could not check whether PawnIO is installed.",
        };
    }

    /// <summary>The winget command for a silent, non-interactive install of the official package.</summary>
    public static InstallCommand WingetInstall(string wingetPath) =>
        new(wingetPath,
        [
            "install", "--id", WingetPackageId, "-e", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements", "--silent", "--disable-interactivity",
        ]);

    /// <summary>The setup command: the documented silent switches when they are known, otherwise the interactive setup.</summary>
    public static InstallCommand SetupInstall(string setupPath, bool silent = true) =>
        new(setupPath, silent ? SilentInstallSwitches : []);

    /// <summary>True for an HTTPS address on one of the <see cref="TrustedDownloadHosts"/> (no user info, default port).</summary>
    public static bool IsTrustedDownload(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort
        && TrustedDownloadHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds <see cref="SetupAssetName"/> in a GitHub "release" JSON document. The download address must be a trusted
    /// HTTPS address inside the official repository's releases. Returns null for anything else (drafts too).
    /// </summary>
    public static PawnIoReleaseAsset? FindSetupAsset(string? releaseJson)
    {
        if (string.IsNullOrWhiteSpace(releaseJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(releaseJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object
                    || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !string.Equals(name.GetString(), SetupAssetName, StringComparison.OrdinalIgnoreCase)
                    || !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
                {
                    continue;
                }

                if (!IsOfficialReleaseDownload(uri))
                {
                    return null;
                }

                long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                return new PawnIoReleaseAsset(tag, SetupAssetName, uri, size);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>True for <c>https://github.com/namazso/PawnIO.Setup/releases/download/…/PawnIO_setup.exe</c>.</summary>
    public static bool IsOfficialReleaseDownload(Uri uri) =>
        IsTrustedDownload(uri)
        && uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/" + ReleaseRepository + "/releases/download/", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.EndsWith("/" + SetupAssetName, StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.Contains("/../", StringComparison.Ordinal);

    /// <summary>Reads a winget exit code. Null message = success.</summary>
    public static (bool Success, bool RebootRequired, string? Problem) InterpretWingetExit(int exitCode) => exitCode switch
    {
        0 => (true, false, null),
        WingetAlreadyInstalled => (true, false, null),
        RebootRequiredExitCode or WingetRebootToFinish or WingetRebootToInstall => (true, true, null),
        WingetNoPackageFound => (false, false, "winget could not find the PawnIO package. Its sources may be out of date."),
        _ => (false, false, $"winget stopped with code 0x{exitCode:X8}."),
    };

    /// <summary>Reads PawnIO_setup.exe's exit code (DOS error codes since 2.2.0; 3010 = restart needed).</summary>
    public static (bool Success, bool RebootRequired, string? Problem) InterpretSetupExit(int exitCode) => exitCode switch
    {
        0 => (true, false, null),
        RebootRequiredExitCode => (true, true, null),
        1223 => (false, false, "The PawnIO setup was cancelled."), // ERROR_CANCELLED
        _ => (false, false, $"The PawnIO setup stopped with code {exitCode}."),
    };

    /// <summary>
    /// Picks the newest x64/arm64 App Installer folder (the real winget.exe lives in it) from folder names under
    /// <c>%ProgramFiles%\WindowsApps</c>, e.g. <c>Microsoft.DesktopAppInstaller_1.24.25200.0_x64__8wekyb3d8bbwe</c>.
    /// </summary>
    public static string? PickWingetFolder(IEnumerable<string> folderNames, string architecture)
    {
        ArgumentNullException.ThrowIfNull(folderNames);
        string? best = null;
        Version? bestVersion = null;
        foreach (var name in folderNames)
        {
            var m = AppInstallerFolder().Match(name ?? string.Empty);
            if (!m.Success || !Version.TryParse(m.Groups["v"].Value, out var version))
            {
                continue;
            }

            var arch = m.Groups["a"].Value;
            if (!arch.Equals(architecture, StringComparison.OrdinalIgnoreCase) && !arch.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (bestVersion is null || version > bestVersion)
            {
                best = name;
                bestVersion = version;
            }
        }

        return best;
    }

    [GeneratedRegex(@"^Microsoft\.DesktopAppInstaller_(?<v>\d+(\.\d+){1,3})_(?<a>[a-z0-9]+)__8wekyb3d8bbwe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppInstallerFolder();
}
