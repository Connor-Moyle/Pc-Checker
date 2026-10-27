using System.Globalization;
using PcChecker.Core.Drivers;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>What a run of wusa.exe or dism.exe ended with, decided from its exit code alone.</summary>
public enum UpdateToolOutcome
{
    Success,
    SuccessRestartNeeded,
    NotInstalled,
    NotApplicable,
    NotRemovable,
    AccessDenied,
    Busy,
    RestartFirst,
    Cancelled,
    InvalidArguments,
    TimedOut,
    Failed,
}

/// <summary>The meaning of one exit code, with a plain-English message.</summary>
public sealed record UpdateToolResult(UpdateToolOutcome Outcome, int ExitCode, string Message)
{
    public bool Success => Outcome is UpdateToolOutcome.Success or UpdateToolOutcome.SuccessRestartNeeded;

    public bool RestartNeeded => Outcome is UpdateToolOutcome.SuccessRestartNeeded or UpdateToolOutcome.RestartFirst;

    /// <summary>The exit code as Windows prints it: hex for HRESULTs, decimal for small Win32 codes.</summary>
    public string CodeText => UpdateToolExitCodes.FormatCode(ExitCode);
}

/// <summary>
/// Maps the exit codes of <c>wusa.exe</c> and <c>dism.exe</c> to outcomes and plain-English messages. Both tools print
/// their text in the Windows display language, so PC Checker decides from the exit code only, never from the output.
/// Pure.
/// </summary>
public static class UpdateToolExitCodes
{
    /// <summary>ERROR_SUCCESS_REBOOT_REQUIRED.</summary>
    public const int RestartRequired = 3010;

    /// <summary>ERROR_SUCCESS_REBOOT_INITIATED.</summary>
    public const int RestartInitiated = 1641;

    /// <summary>WU_S_NOT_INSTALLED (0x00240007): wusa's "this update is not installed on this computer".</summary>
    public const int WusaNotInstalled = 2359303;

    /// <summary>WU_S_ALREADY_INSTALLED (0x00240006).</summary>
    public const int WusaAlreadyInstalled = 2359302;

    /// <summary>WU_S_REBOOT_REQUIRED (0x00240005).</summary>
    public const int WusaRestartRequired = 2359301;

    /// <summary>ERROR_NOT_FOUND (1168) as an HRESULT: "Element not found".</summary>
    public const int NotFound = unchecked((int)0x80070490);

    /// <summary>WU_E_NOT_APPLICABLE: the update does not apply to this PC.</summary>
    public const int NotApplicable = unchecked((int)0x80240017);

    /// <summary>CBS_E_CANNOT_UNINSTALL: the package is permanent.</summary>
    public const int CannotUninstall = unchecked((int)0x800F0825);

    /// <summary>CBS_E_PENDING: earlier changes wait for a restart.</summary>
    public const int ServicingPending = unchecked((int)0x800F0806);

    /// <summary>The exit code <c>ProcessResult</c>-style runners use for a run that was stopped after its time limit.</summary>
    public const int TimedOutCode = -2;

    /// <summary>Maps an exit code. <paramref name="tool"/> ("wusa" or "DISM") only appears in the message.</summary>
    public static UpdateToolResult Map(int exitCode, string tool = "The tool")
    {
        uint code = unchecked((uint)exitCode);
        return code switch
        {
            0 => new(UpdateToolOutcome.Success, exitCode, "Done."),
            3010 or 3011 or 1641 or 2359301 or 0x80070BC2 or 0x80070BC3 or 0x80070669 =>
                new(UpdateToolOutcome.SuccessRestartNeeded, exitCode, "Done. Restart Windows to finish."),
            2359303 or 1168 or 0x80070490 or 0x800F080C =>
                new(UpdateToolOutcome.NotInstalled, exitCode, "Windows says this is not installed (any more), so there was nothing to remove. A restart may still be waiting to finish an earlier change."),
            2359302 => new(UpdateToolOutcome.NotApplicable, exitCode, "Windows says this update is already installed."),
            0x80240017 => new(UpdateToolOutcome.NotApplicable, exitCode, "This update does not apply to this PC."),
            0x800F0825 =>
                new(UpdateToolOutcome.NotRemovable, exitCode, "Windows marks this update as permanent, so it cannot be removed."),
            5 or 740 or 0x80070005 or 0x800702E4 =>
                new(UpdateToolOutcome.AccessDenied, exitCode, "Access was denied. PC Checker must run as an administrator for this."),
            1618 or 0x80070652 or 0x80240016 =>
                new(UpdateToolOutcome.Busy, exitCode, "Another installation is running. Wait for it to finish (or restart), then try again."),
            0x800F0806 or 3017 or 0x80070BC9 or 0x8024A105 =>
                new(UpdateToolOutcome.RestartFirst, exitCode, "Windows has changes waiting for a restart. Restart, then try again."),
            1223 or 0x800704C7 or 0x8024000B =>
                new(UpdateToolOutcome.Cancelled, exitCode, "The operation was cancelled."),
            87 or 0x80070057 =>
                new(UpdateToolOutcome.InvalidArguments, exitCode, $"{tool} did not accept the request on this version of Windows."),
            0xFFFFFFFE => new(UpdateToolOutcome.TimedOut, exitCode, $"{tool} did not finish in time and was stopped."),
            _ => new(UpdateToolOutcome.Failed, exitCode, Unknown(exitCode, tool)),
        };
    }

    /// <summary>"3010" for small Win32 codes, "0x800F0825" for HRESULTs and wusa's 0x0024xxxx codes.</summary>
    public static string FormatCode(int exitCode)
    {
        uint code = unchecked((uint)exitCode);
        return code is > 0 and < 0x10000 ? code.ToString(CultureInfo.InvariantCulture) : "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static string Unknown(int exitCode, string tool)
    {
        if (exitCode < 0 && WuaResultCodes.IsKnown(exitCode))
        {
            return WuaResultCodes.Explain(exitCode);
        }

        return $"{tool} stopped with error {FormatCode(exitCode)}. Restart Windows and try again, or use Settings → Windows Update.";
    }
}
