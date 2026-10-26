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

    /// <summary>The Windows Update service id used to search the public driver catalog.</summary>
    public const string WindowsUpdateServiceId = "7971f918-a847-4430-9279-4a52d1efe18d";

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
        [0x8024002D] = "A required file from the update is missing or invalid.",
        [0x80240034] = "The update could not be downloaded. Check the Internet connection and try again.",
        [0x80240438] = "Windows Update could not reach the update servers. Check the Internet connection or proxy.",
        [0x80244022] = "The update server is unavailable (HTTP 503). Try again later.",
        [0x8024402C] = "The update server name could not be resolved. Check DNS and the Internet connection.",
        [0x8024401C] = "The connection to the update server timed out.",
        [0x80246007] = "The update has not been downloaded yet.",
        [0x80246008] = "The Background Intelligent Transfer Service (BITS) is not running.",
        [0x80248007] = "The update information is missing from the local data store.",
        [0x8024A000] = "Windows Update's automatic update agent is not running.",
        [0x8024A105] = "Windows Update needs to be restarted; reboot and try again.",
        [0x80070643] = "The installer reported a fatal error (0x80070643). Restart Windows and try again.",
        [0x800F0922] = "The update could not be installed because a required component is missing or Secure Boot blocked it.",
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
