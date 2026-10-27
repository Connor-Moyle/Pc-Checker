using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Sensors.PawnIo;

namespace PcChecker.Core.Lighting;

/// <summary>Pure rules for finding, starting and installing OpenRGB.</summary>
public static class OpenRgbInstallRules
{
    /// <summary>OpenRGB's package in the Windows Package Manager community repository.</summary>
    public const string WingetPackageId = "CalcProgrammer1.OpenRGB";

    /// <summary>
    /// winget install for all users (machine scope), so the program lands under Program Files where only
    /// administrators can change it. PC Checker runs as administrator and only starts OpenRGB from there.
    /// </summary>
    public static InstallCommand WingetInstall(string wingetPath) =>
        new(wingetPath,
        [
            "install", "--id", WingetPackageId, "-e", "--source", "winget", "--scope", "machine",
            "--accept-package-agreements", "--accept-source-agreements", "--silent", "--disable-interactivity",
        ]);

    /// <summary>What a winget exit code means for this install.</summary>
    public static (bool Success, bool RebootRequired, string? Problem) InterpretWingetExit(int exitCode) => exitCode switch
    {
        0 or PawnIoRules.WingetAlreadyInstalled => (true, false, null),
        PawnIoRules.WingetRebootToFinish or PawnIoRules.WingetRebootToInstall or PawnIoRules.RebootRequiredExitCode => (true, true, null),
        PawnIoRules.WingetNoPackageFound => (false, false, "winget could not find the OpenRGB package. Its sources may be out of date; download OpenRGB from openrgb.org instead."),
        _ => (false, false, $"winget stopped with code 0x{exitCode:X8}. Download OpenRGB from openrgb.org instead."),
    };

    /// <summary>
    /// True when PC Checker may start this OpenRGB.exe: PC Checker runs as administrator, so it only starts programs
    /// from folders a normal account cannot change (Program Files). A copy in Downloads or AppData is left to the user.
    /// </summary>
    public static bool MayStart(string? executablePath, IEnumerable<string?> protectedRoots) =>
        !string.IsNullOrWhiteSpace(executablePath)
        && executablePath[(executablePath.LastIndexOfAny(['\\', '/']) + 1)..].Equals("OpenRGB.exe", StringComparison.OrdinalIgnoreCase)
        && OemToolCatalog.IsInProtectedFolder(executablePath, protectedRoots);

    /// <summary>Why PC Checker will not start an OpenRGB copy from <paramref name="executablePath"/>.</summary>
    public static string StartRefusal(string executablePath) =>
        $"OpenRGB is in a folder your account can change ({Path.GetDirectoryName(executablePath)}). PC Checker runs as administrator, so it does not start programs from there. "
        + "Start OpenRGB yourself, open its SDK Server tab and press Start Server, then press Connect here. Installing OpenRGB from this page puts it in Program Files.";

    /// <summary>Where OpenRGB stands, from what the last connection attempt saw.</summary>
    /// <param name="connected">The SDK client is connected.</param>
    /// <param name="isLocalServer">The configured server is this PC (127.0.0.1, localhost, ::1).</param>
    /// <param name="installed">OpenRGB.exe was found.</param>
    /// <param name="processRunning">An OpenRGB process is running.</param>
    public static OpenRgbAvailability Availability(bool connected, bool isLocalServer, bool installed, bool processRunning)
    {
        if (connected)
        {
            return OpenRgbAvailability.Connected;
        }

        if (!isLocalServer)
        {
            return OpenRgbAvailability.Unreachable;
        }

        if (processRunning)
        {
            return OpenRgbAvailability.RunningWithoutServer;
        }

        return installed ? OpenRgbAvailability.InstalledNotRunning : OpenRgbAvailability.NotInstalled;
    }

    /// <summary>True for addresses that mean "this PC".</summary>
    public static bool IsLocalHost(string? host) =>
        string.IsNullOrWhiteSpace(host)
        || host.Trim() is "127.0.0.1" or "::1" or "[::1]"
        || host.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase);
}
