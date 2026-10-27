namespace PcChecker.Core.ContextMenus;

/// <summary>
/// Rules for the Send to folder (<c>%APPDATA%\Microsoft\Windows\SendTo</c>): which files show in the menu, which ones
/// Windows put there, and where a turned-off item is kept.
/// </summary>
public static class SendToRules
{
    /// <summary>Folder (under PC Checker's data folder) where turned-off Send to items are kept.</summary>
    public const string BackupFolderName = "sendto-off";

    /// <summary>Extensions of Windows' own Send to items (zip folder, desktop shortcut, Documents, mail, fax).</summary>
    private static readonly HashSet<string> WindowsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ZFSendToTarget", ".DeskLink", ".mydocs", ".MAPIMail",
    };

    /// <summary>Names of Windows' own shortcuts (Bluetooth, fax) in English; others are recognised by their target.</summary>
    private static readonly HashSet<string> WindowsShortcutNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bluetooth device.lnk", "Bluetooth File Transfer.lnk", "Fax Recipient.lnk", "Fax recipient.lnk",
    };

    /// <summary>The text shown for a Send to file: its name without the extension.</summary>
    public static string DisplayName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        string name = fileName.Trim();
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>False for desktop.ini and hidden or system files, which the menu does not show.</summary>
    public static bool IsShown(string fileName, FileAttributes attributes) =>
        !string.IsNullOrWhiteSpace(fileName)
        && !fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        && (attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

    /// <summary>True for Windows' own items: their special extensions, their shortcuts, or a target in the Windows folder.</summary>
    public static bool IsWindowsItem(string fileName, string? shortcutTarget, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string extension = Path.GetExtension(fileName);
        if (WindowsExtensions.Contains(extension) || WindowsShortcutNames.Contains(fileName.Trim()))
        {
            return true;
        }

        return ContextMenuRules.IsInWindowsFolder(shortcutTarget, windowsDirectory);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="original"/> or a name <see cref="FreeName"/> made from
    /// it ("Tool (2).lnk" for "Tool.lnk"), so undo still finds a Send to item that had to be renamed when it was moved.
    /// </summary>
    public static bool IsFreeNameOf(string? candidate, string? original)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        if (candidate.Equals(original, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(original);
        string extension = Path.GetExtension(original);
        if (!candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            || !candidate.StartsWith(stem + " (", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string middle = candidate[(stem.Length + 2)..^extension.Length];
        return middle.Length > 1 && middle.EndsWith(')') && middle[..^1].All(char.IsAsciiHexDigit);
    }

    /// <summary>A free file name in the backup folder: "name.lnk", then "name (2).lnk" and so on.</summary>
    public static string FreeName(string fileName, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (!exists(fileName))
        {
            return fileName;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{stem} ({i}){extension}";
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }
}
