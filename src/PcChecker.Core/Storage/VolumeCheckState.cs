using Microsoft.Win32;
using PcChecker.Core.Common;

namespace PcChecker.Core.Storage;

/// <summary>
/// Locale-independent answer to "is a file system check scheduled for this volume?". chkdsk prints its verdict in the
/// user's language, so instead of trusting English text alone the repair path also looks at what chkdsk actually
/// changed: the <c>BootExecute</c> list that runs autochk at startup (<c>autocheck autochk /r \??\C:</c>) and the
/// volume's dirty bit, which also makes autochk run at the next restart. Every lookup is guarded; null means unknown.
/// </summary>
public static class VolumeCheckState
{
    private const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string BootExecuteValue = "BootExecute";

    /// <summary>True when autochk is scheduled for <paramref name="driveLetter"/> or its dirty bit is set; false when neither; null when it could not be determined.</summary>
    public static bool? IsCheckScheduled(string driveLetter)
    {
        var letter = StorageCodes.NormalizeDriveLetter(driveLetter);
        if (letter is null || !PlatformGuard.IsWindows)
        {
            return null;
        }

        bool? bootExecute = IsInBootExecute(letter);
        if (bootExecute == true)
        {
            return true;
        }

        bool? dirty = IsVolumeDirty(letter);
        if (dirty == true)
        {
            return true;
        }

        return bootExecute is null && dirty is null ? null : false;
    }

    /// <summary>Pure string check used by <see cref="IsInBootExecute"/>: does any BootExecute entry run autochk against the volume?</summary>
    public static bool BootExecuteMentionsVolume(IEnumerable<string>? entries, string driveLetter)
    {
        var letter = StorageCodes.NormalizeDriveLetter(driveLetter);
        if (entries is null || letter is null)
        {
            return false;
        }

        string target = @"\??\" + letter;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            bool autochk = entry.Contains("autochk", StringComparison.OrdinalIgnoreCase);
            bool thisVolume = entry.Contains(target, StringComparison.OrdinalIgnoreCase);
            bool hasSwitch = entry.Contains(" /", StringComparison.Ordinal);
            // "autocheck autochk *" is the default entry: it only checks dirty volumes, so it is not a scheduled check.
            if (autochk && thisVolume && hasSwitch)
            {
                return true;
            }
        }

        return false;
    }

    private static bool? IsInBootExecute(string letter)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SessionManagerKey, writable: false);
            if (key?.GetValue(BootExecuteValue) is string[] entries)
            {
                return BootExecuteMentionsVolume(entries, letter);
            }

            return key?.GetValue(BootExecuteValue) is string single ? BootExecuteMentionsVolume([single], letter) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? IsVolumeDirty(string letter)
    {
        try
        {
            using var volume = RawDiskIo.OpenVolume(letter);
            return volume is null ? null : RawDiskIo.IsVolumeDirty(volume);
        }
        catch
        {
            return null;
        }
    }
}
