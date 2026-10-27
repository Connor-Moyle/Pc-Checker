using System.Globalization;

namespace PcChecker.Core.Drivers;

/// <summary>Plain-English text for Windows Update Agent (WUA) result codes and common HRESULTs. Pure.</summary>
public static class WuaResultCodes
{
    public const int NotStarted = 0;
    public const int InProgress = 1;
    public const int Succeeded = 2;
    public const int SucceededWithErrors = 3;
    public const int Failed = 4;
    public const int Aborted = 5;

    /// <summary><c>WU_E_INVALID_CRITERIA</c>: the search string was rejected by the Windows Update Agent.</summary>
    public const int InvalidCriteria = unchecked((int)0x80240032);

    /// <summary>
    /// The Windows Update service id used to search the public driver catalog. It is registered on every PC, unlike the
    /// Microsoft Update service (<see cref="MicrosoftUpdateServiceId"/>), which only exists once "Receive updates for
    /// other Microsoft products" is turned on and otherwise makes the search fail with <c>WU_E_DS_UNKNOWNSERVICE</c>.
    /// </summary>
    public const string WindowsUpdateServiceId = "9482f4b4-e343-43b6-b170-9a65bc822c77";

    /// <summary>The Microsoft Update service id. Not used for searching: it is not registered on a default install.</summary>
    public const string MicrosoftUpdateServiceId = "7971f918-a847-4430-9279-4a52d1efe18d";

    private static readonly Dictionary<uint, string> KnownHResults = new()
    {
        [0x00000000] = "Success.",
        [0x80070005] = "Access denied. Run PC Checker as an administrator.",
        [0x80070422] = "The Windows Update service is disabled. Enable the 'Windows Update' service and try again.",
        [0x8007000E] = "Windows ran out of memory while processing the update.",
        [0x80240001] = "Windows Update Agent could not provide the service.",
        [0x80240004] = "The update object is not initialized.",
        [0x80240007] = "The update object is in an unexpected state.",
        [0x8024000B] = "The operation was cancelled.",
        [0x80240016] = "Another installation is already in progress. Wait for it to finish, then try again.",
        [0x80240017] = "This update does not apply to your computer.",
        [0x8024001E] = "The Windows Update service stopped during the operation.",
        [0x80240020] = "No interactive user is logged on; the update cannot be installed right now.",
        [0x80240022] = "All of the selected updates failed to install.",
        [0x80240024] = "There are no updates to process.",
        [0x80240032] = "Windows Update rejected the search criteria.",
        [0x8024002D] = "A required file from the update is missing or invalid.",
        [0x80240034] = "The update could not be downloaded. Check the Internet connection and try again.",
        [0x80240438] = "Windows Update could not reach the update servers. Check the Internet connection or proxy.",
        [0x80244022] = "The update server is unavailable (HTTP 503). Try again later.",
        [0x8024402C] = "The update server name could not be resolved. Check DNS and the Internet connection.",
        [0x8024401C] = "The connection to the update server timed out.",
        [0x80246007] = "The update has not been downloaded yet.",
        [0x80246008] = "The Background Intelligent Transfer Service (BITS) is not running.",
        [0x80248007] = "The update information is missing from the local data store.",
        [0x80248014] = "The update service PC Checker asked for is not registered with Windows Update.",
        [0x8024A000] = "Windows Update's automatic update agent is not running.",
        [0x8024A105] = "Windows Update needs to be restarted; reboot and try again.",
        [0x80070643] = "The installer reported a fatal error (0x80070643). Restart Windows and try again.",
        [0x800F0922] = "The update could not be installed because a required component is missing or Secure Boot blocked it.",
        [0x800F081F] = "Windows could not find files the update needs. Run 'DISM /Online /Cleanup-Image /RestoreHealth', then try again.",
        [0x800F0831] = "An earlier update this one builds on is missing or damaged. Install the latest servicing stack and cumulative update, then try again.",
        [0x800F0988] = "The component store needs a clean-up. Run 'DISM /Online /Cleanup-Image /StartComponentCleanup', restart and try again.",
        [0x80073712] = "Some Windows system files are damaged. Run 'DISM /Online /Cleanup-Image /RestoreHealth', then try again.",
        [0x80073701] = "A Windows component the update needs is missing. Run 'DISM /Online /Cleanup-Image /RestoreHealth', then try again.",
        [0x80070002] = "A file the update needs was not found. Restart Windows and try again.",
        [0x80070003] = "A folder the update needs was not found. Restart Windows and try again.",
        [0x8007000D] = "The downloaded update is damaged. It will be downloaded again on the next try.",
        [0x80070020] = "Another program, often antivirus, was using a file the update needs. Restart Windows and try again.",
        [0x80070070] = "There is not enough free disk space. Free up space on the Windows drive and try again.",
        [0x80070570] = "A file the update needs is damaged or unreadable. Restart Windows and try again; if it repeats, check the disk.",
        [0x800705B4] = "The update timed out. Restart Windows and try again.",
        [0x80070BC2] = "Windows needs a restart to finish an earlier update before this one can install.",
        [0x80070BC9] = "Windows needs a restart to finish an earlier update before this one can install.",
        [0x8024200B] = "The update's installer reported an error. Restart Windows and try again.",
        [0x8024200D] = "The update was only partly downloaded. It will be downloaded again on the next try.",
        [0x80242016] = "The update was interrupted after it installed and before Windows finished setting it up. Restart Windows.",
        [0x80240FFF] = "Windows Update ran into an unexpected error. Restart Windows and try again.",
        [0x8024401F] = "The update server returned an error. Try again later.",
        [0x8024402F] = "Windows Update could not read the update list it downloaded, often because a proxy or firewall changed it.",
        [0x80072EE2] = "The connection to the update server timed out. Check the Internet connection and try again.",
        [0x80072EE7] = "The update server name could not be resolved. Check DNS and the Internet connection.",
        [0x80072EFD] = "Windows Update could not connect to the update servers. Check the Internet connection, proxy or firewall.",
        [0x80072EFE] = "The connection to the update server was cut off. Check the Internet connection and try again.",
        [0x80072F8F] = "A secure connection to the update server failed. Check that the PC's date and time are correct.",
        [0x8007045B] = "Windows was shutting down while the update installed. Try again after the restart.",
        [0x8007371B] = "Some update files are missing or damaged. Run 'DISM /Online /Cleanup-Image /RestoreHealth', then try again.",
        [0xC1900101] = "The new Windows version was rolled back, usually because of a driver. Update your drivers, disconnect extra devices and try again.",
        [0xC1900200] = "This PC does not meet the requirements for this Windows version.",
        [0xC1900208] = "An app that is not compatible with this Windows version is installed. Remove or update it, then try again.",
        [0xC190020E] = "There is not enough free disk space for this Windows version. Free up space and try again.",
    };

    /// <summary>Text for an <c>OperationResultCode</c> value (0..5).</summary>
    public static string Describe(int resultCode) => resultCode switch
    {
        NotStarted => "Not started",
        InProgress => "In progress",
        Succeeded => "Succeeded",
        SucceededWithErrors => "Succeeded with errors",
        Failed => "Failed",
        Aborted => "Aborted",
        _ => $"Unknown result ({resultCode})",
    };

    /// <summary>True when the result code means the operation worked (fully or partially).</summary>
    public static bool IsSuccess(int resultCode) => resultCode is Succeeded or SucceededWithErrors;

    /// <summary>Formats an HRESULT as "0x80240022 — explanation" (or just the hex when unknown).</summary>
    public static string DescribeHResult(int hresult)
    {
        uint code = unchecked((uint)hresult);
        var hex = "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
        return KnownHResults.TryGetValue(code, out var text) ? $"{hex} — {text}" : hex;
    }

    /// <summary>
    /// The plain-English reason for an HRESULT, followed by the code ("… (error 0x80070070)"). An unknown code gets
    /// the code and a hint to search for it.
    /// </summary>
    public static string Explain(int hresult)
    {
        uint code = unchecked((uint)hresult);
        var hex = "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
        return KnownHResults.TryGetValue(code, out var text)
            ? $"{text} (error {hex})"
            : $"Windows Update error {hex}. Search for this code on Microsoft's support site for help.";
    }

    /// <summary>True when the HRESULT has a dedicated explanation.</summary>
    public static bool IsKnown(int hresult) => KnownHResults.ContainsKey(unchecked((uint)hresult));

    /// <summary>Builds a one-line summary for an install/download result plus optional HRESULT.</summary>
    public static string Summarize(int resultCode, int? hresult)
    {
        var text = Describe(resultCode);
        if (hresult is { } hr && hr != 0 && !IsSuccess(resultCode))
        {
            text += " (" + DescribeHResult(hr) + ")";
        }

        return text;
    }
}
