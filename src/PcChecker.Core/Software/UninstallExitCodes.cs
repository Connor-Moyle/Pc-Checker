using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Pure mapping from uninstaller exit codes (Windows Installer and common setup tools) to a friendly result.</summary>
public static class UninstallExitCodes
{
    public const int Success = 0;
    public const int RebootRequired = 3010;
    public const int RebootInitiated = 1641;
    public const int NotInstalled = 1605;
    public const int UserCancelled = 1602;
    public const int FatalError = 1603;
    public const int AnotherInstallInProgress = 1618;
    public const int PackageNotFound = 1619;
    public const int AccessDenied = 5;
    public const int ControlCExit = unchecked((int)0xC000013A);

    /// <summary>Exit codes that mean the program is gone (possibly after a restart).</summary>
    public static bool IsSuccess(int exitCode) =>
        exitCode is Success or RebootRequired or RebootInitiated or NotInstalled;

    /// <summary>True when the installer asked for a restart to finish removing files.</summary>
    public static bool NeedsReboot(int exitCode) => exitCode is RebootRequired or RebootInitiated;

    /// <summary>Converts an exit code into an <see cref="OperationResult"/> with plain-English text.</summary>
    public static OperationResult Map(int exitCode, string programName)
    {
        var name = string.IsNullOrWhiteSpace(programName) ? "The program" : programName;
        return exitCode switch
        {
            Success => OperationResult.Ok($"{name} was uninstalled."),
            RebootRequired => OperationResult.Ok($"{name} was uninstalled. Restart the PC to finish removing files.", rebootRequired: true),
            RebootInitiated => OperationResult.Ok($"{name} was uninstalled and Windows is restarting to finish.", rebootRequired: true),
            NotInstalled => OperationResult.Ok($"{name} was already removed (the installer reported it is not installed)."),
            UserCancelled => OperationResult.Fail($"Uninstalling {name} was cancelled in the installer window."),
            FatalError => OperationResult.Fail($"The {name} uninstaller stopped with a fatal error (1603). Check the installer's own log or try again after a restart."),
            AnotherInstallInProgress => OperationResult.Fail("Another installation is already running. Wait for it to finish, then try again."),
            PackageNotFound => OperationResult.Fail($"The {name} installer package could not be found. Reinstalling and then uninstalling usually fixes this."),
            AccessDenied => OperationResult.Fail($"Windows refused access while uninstalling {name}. Run PC Checker as administrator."),
            ControlCExit => OperationResult.Fail($"Uninstalling {name} was interrupted."),
            _ => OperationResult.Fail($"The {name} uninstaller finished with exit code {exitCode}, which usually means it did not complete."),
        };
    }
}
