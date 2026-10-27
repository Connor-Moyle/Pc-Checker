using PcChecker.Core.Startup;

namespace PcChecker.Core.ContextMenus;

/// <summary>
/// Pure rules for the right-click menu: which registry classes are read, where an entry shows, which program it runs,
/// and whether it belongs to Windows (read-only), a Microsoft app (Advanced mode) or another program.
/// </summary>
public static class ContextMenuRules
{
    /// <summary>The registry classes whose menus File Explorer shows, in display order.</summary>
    public static IReadOnlyList<string> ClassKeys { get; } =
        ["*", "AllFilesystemObjects", "Directory", "Directory\\Background", "Folder", "Drive", "DesktopBackground"];

    /// <summary>The 20 file types whose own menus are read as well.</summary>
    public static IReadOnlyList<string> CommonExtensions { get; } =
    [
        ".txt", ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".mp3", ".mp4", ".mkv",
        ".avi", ".doc", ".docx", ".xls", ".xlsx", ".pptx", ".zip", ".rar", ".7z", ".exe",
    ];

    /// <summary>
    /// Verbs that Windows itself registers. They are recognised by name as well as by file, because some (open, find,
    /// cmd) run a bare program name that has no folder.
    /// </summary>
    private static readonly HashSet<string> BuiltInVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewwindow", "opennewprocess", "opennewtab", "explore", "find", "edit", "print", "printto", "runas",
        "runasuser", "cmd", "powershell", "pintohome", "pintostartscreen", "pintohomefile", "properties", "play",
        "enqueue", "preview", "setdesktopwallpaper", "rotateleft", "rotateright", "Windows.Share",
        "UpdateEncryptionSettings", "UpdateEncryptionSettingsWork", "encrypt-bde", "encrypt-bde-elev", "manage-bde",
        "resume-bde", "resume-bde-elev", "unlock-bde", "format", "eject", "removeproperties", "install", "uninstall",
        "WSL", "ShowFileHistory", "BurnDisc", "change-passphrase", "change-pin", "pin", "unpin", "MapNetworkDrive",
        "DisconnectNetworkDrive", "Windows.ModernShare", "Windows.Copilot", "Display", "Personalize", "Gadgets",
    };

    /// <summary>Shell extension CLSIDs that are part of Windows (sharing, libraries, pinning, compressed folders…).</summary>
    private static readonly HashSet<string> BuiltInHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "{F81E9010-6EA4-11CE-A7FF-00AA003CA9F6}", // Sharing
        "{3DAD6C5D-2167-4CAE-9914-F99E41C12CFA}", // Library location
        "{E2BF9676-5F8F-435C-97EB-11607A5BEDF7}", // Modern sharing
        "{B455F46E-E4AF-4035-B0A4-CF18D2F6F28E}", // Pin to start
        "{470C0EBD-5D73-4D58-9CED-E91E22E23282}", // Pin to start (folders)
        "{90AA3A4E-1CBA-4233-B8BB-535773D48449}", // Pin to taskbar
        "{A2A9545D-A0C2-42B4-9708-A0B2BADD77C8}", // Start menu pin
        "{596AB062-B4D2-4215-9F74-E9109B0A8153}", // Previous versions
        "{09A47860-11B0-4DA5-AFA5-26D86198A780}", // Microsoft Defender
        "{7AD84985-87B4-4A16-BE58-8B72A5B390F7}", // Play to / Cast to device
        "{8A734961-C4AA-4741-AC1E-791ACEBF5B39}", // Shop for music online
        "{7BA4C740-9E81-11CF-99D3-00AA004AE837}", // Send To
        "{B8CDCB65-B1BF-4B42-9428-1DFDB7EE92AF}", // Compressed (zipped) folder
        "{D969A300-E7FF-11D0-A93B-00A0C90F2719}", // New menu
    };

    /// <summary>Hosts that run another file: the file in the arguments is the real program.</summary>
    private static readonly string[] HostPrograms = ["rundll32.exe", "wscript.exe", "cscript.exe", "mshta.exe", "regsvr32.exe"];

    /// <summary>
    /// Windows tools that programs and tweaks use to run their own commands ("Take ownership", "Open PowerShell here").
    /// A verb that runs one of them is a program's (or a tweak's) unless it is a known Windows verb or its text comes
    /// from a Windows file.
    /// </summary>
    private static readonly string[] GeneralTools =
        ["cmd.exe", "powershell.exe", "pwsh.exe", "explorer.exe", "msiexec.exe", "conhost.exe", "wt.exe", "reg.exe", "takeown.exe", "icacls.exe"];

    /// <summary>Plain words for where a class key's menu shows.</summary>
    public static string LocationOf(string classKey)
    {
        if (string.IsNullOrWhiteSpace(classKey))
        {
            return "Unknown";
        }

        string key = classKey.Trim();
        if (key.StartsWith("SystemFileAssociations\\", StringComparison.OrdinalIgnoreCase))
        {
            key = key["SystemFileAssociations\\".Length..];
        }

        if (key.StartsWith('.'))
        {
            return key.ToLowerInvariant() + " files";
        }

        return key.ToUpperInvariant() switch
        {
            "*" => "Files",
            "ALLFILESYSTEMOBJECTS" => "Files and folders",
            "DIRECTORY" or "FOLDER" => "Folders",
            "DIRECTORY\\BACKGROUND" => "Inside a folder",
            "DRIVE" => "Drives",
            "DESKTOPBACKGROUND" => "Desktop",
            _ => key,
        };
    }

    /// <summary>Sorted, distinct locations so "Folders" from Directory and Folder only shows once.</summary>
    public static IReadOnlyList<string> MergeLocations(IEnumerable<string> locations)
    {
        var order = new List<string> { "Files", "Files and folders", "Folders", "Inside a folder", "Desktop", "Drives" };
        return locations.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => order.IndexOf(l) is var i && i >= 0 ? i : order.Count)
            .ThenBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsBuiltInVerb(string? verbName) => !string.IsNullOrWhiteSpace(verbName) && BuiltInVerbs.Contains(verbName.Trim());

    public static bool IsBuiltInHandler(string? clsid) =>
        ShellExtensionBlockList.Normalize(clsid) is { } normalized && BuiltInHandlers.Contains(normalized);

    /// <summary>
    /// The CLSID of a ContextMenuHandlers entry: its default value, or the key name when the key itself is named after
    /// the CLSID. <paramref name="disabledByPrefix"/> is true when another tool turned it off by adding a leading '-'.
    /// </summary>
    public static string? HandlerClsid(string keyName, string? defaultValue, out bool disabledByPrefix)
    {
        disabledByPrefix = false;
        foreach (var candidate in new[] { defaultValue, keyName })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string text = candidate.Trim();
            if (text.StartsWith('-'))
            {
                if (ShellExtensionBlockList.Normalize(text[1..]) is { } off)
                {
                    disabledByPrefix = true;
                    return off;
                }
            }

            if (ShellExtensionBlockList.Normalize(text) is { } clsid)
            {
                return clsid;
            }
        }

        return null;
    }

    /// <summary>
    /// The program a menu command runs. For hosts such as rundll32 or wscript this is the DLL or script they are given.
    /// Bare names ("cmd.exe") are returned as they are; the caller resolves them against System32.
    /// </summary>
    public static string? ProgramOfCommand(string? command, Func<string, string?>? environment = null)
    {
        var exe = StartupCommandParser.ExtractExecutable(command, environment);
        if (exe is null)
        {
            return null;
        }

        string fileName = FileName(exe);
        if (HostPrograms.Any(h => fileName.Equals(h, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(h[..^4], StringComparison.OrdinalIgnoreCase)))
        {
            string args = StartupCommandParser.ExtractArguments(command, environment).Trim();
            while (args.StartsWith('/') || args.StartsWith("-", StringComparison.Ordinal))
            {
                int space = args.IndexOf(' ');
                args = space < 0 ? string.Empty : args[(space + 1)..].TrimStart();
            }

            if (args.Length > 0)
            {
                string target;
                if (args.StartsWith('"'))
                {
                    int close = args.IndexOf('"', 1);
                    target = close > 1 ? args[1..close] : args.Trim('"');
                }
                else
                {
                    int end = args.IndexOfAny([',', ' ']);
                    target = end > 0 ? args[..end] : args;
                }

                if (target.Contains('\\') || target.Contains('.'))
                {
                    return target.Trim();
                }
            }
        }

        return exe;
    }

    /// <summary>
    /// Who owns an entry. Windows: the file is in the Windows folder or Windows Defender's folders, it is signed with
    /// Windows' own certificate, or it is a known built-in verb or handler. Microsoft app: signed by Microsoft (or a
    /// Microsoft package) outside those folders, such as OneDrive, Office or Visual Studio Code. Everything else is a
    /// program.
    /// </summary>
    public static MenuEntryOwner ClassifyOwner(string? filePath, string? signer, string windowsDirectory,
        string? verbName = null, string? clsid = null, string? packagePublisher = null, string? menuText = null)
    {
        // A tweak that runs cmd, PowerShell, Explorer or msiexec: the file is Windows', the entry is not.
        if (IsGeneralTool(filePath) && clsid is null && !IsBuiltInVerb(verbName) && !IsWindowsResourceText(menuText, windowsDirectory))
        {
            return MenuEntryOwner.Program;
        }

        if (IsInWindowsFolder(filePath, windowsDirectory))
        {
            return MenuEntryOwner.Windows;
        }

        if (!string.IsNullOrWhiteSpace(signer) && signer.Trim().StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase))
        {
            return MenuEntryOwner.Windows;
        }

        bool rootedFile = !string.IsNullOrWhiteSpace(filePath) && IsRooted(filePath);
        if (!rootedFile && (IsBuiltInVerb(verbName) || IsBuiltInHandler(clsid)))
        {
            return MenuEntryOwner.Windows;
        }

        if (IsBuiltInHandler(clsid) && string.IsNullOrWhiteSpace(signer))
        {
            return MenuEntryOwner.Windows;
        }

        if (IsMicrosoftSigner(signer) || IsMicrosoftSigner(packagePublisher))
        {
            return MenuEntryOwner.MicrosoftApp;
        }

        return MenuEntryOwner.Program;
    }

    /// <summary>True when the file is cmd, PowerShell, Explorer, msiexec or a similar general Windows tool.</summary>
    public static bool IsGeneralTool(string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && GeneralTools.Any(t => FileName(filePath.Trim().Trim('"')).Equals(t, StringComparison.OrdinalIgnoreCase)
            || FileName(filePath.Trim().Trim('"')).Equals(t[..^4], StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when indirect menu text ("@%SystemRoot%\system32\themecpl.dll,-10", "@shell32.dll,-8506") is read from a file
    /// in the Windows folder, or from a bare file name, which Windows looks up in System32.
    /// </summary>
    public static bool IsWindowsResourceText(string? text, string windowsDirectory)
    {
        if (!IndirectString.TryParseResource(text, out string file, out _))
        {
            return false;
        }

        string path = file.Replace('/', '\\');
        foreach (var variable in new[] { "%SystemRoot%", "%windir%" })
        {
            if (path.StartsWith(variable, StringComparison.OrdinalIgnoreCase))
            {
                path = windowsDirectory.TrimEnd('\\') + path[variable.Length..];
            }
        }

        return !path.Contains('\\') || IsInWindowsFolder(path, windowsDirectory);
    }

    /// <summary>
    /// True when no program could be found for a command verb (no command, no handler and no program in a cascading
    /// menu), or only a bare file name whose menu text comes from Windows. Such verbs are Windows' own (for example
    /// the desktop's Display settings and Personalize) and are left alone.
    /// </summary>
    public static bool IsUnattributedVerb(string? filePath, string? menuText, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return true;
        }

        return !IsRooted(filePath) && IsWindowsResourceText(menuText, windowsDirectory);
    }

    /// <summary>A ProgID worth reading for a file type: a class name, not an extension or one of the classes read anyway.</summary>
    public static bool IsUsableProgId(string? progId)
    {
        if (string.IsNullOrWhiteSpace(progId))
        {
            return false;
        }

        string id = progId.Trim();
        return id.Length <= 200 && !id.StartsWith('.') && !id.Contains('*') && !id.Contains('/') && !id.StartsWith('\\')
            && !id.EndsWith('\\') && !ClassKeys.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True for "Microsoft Corporation", "CN=Microsoft Corporation, O=…" and "Microsoft Windows".</summary>
    public static bool IsMicrosoftSigner(string? signer)
    {
        if (string.IsNullOrWhiteSpace(signer))
        {
            return false;
        }

        string text = signer.Trim();
        if (text.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            text = text[3..];
        }

        return text.StartsWith("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for paths under the Windows folder (except the driver store, where hardware makers' DLLs live) or Windows
    /// Defender's program and data folders.
    /// </summary>
    public static bool IsInWindowsFolder(string? path, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsRooted(path))
        {
            return false;
        }

        string normalized = path.Trim().Trim('"').Replace('/', '\\');
        string windows = windowsDirectory.TrimEnd('\\') + "\\";
        if (normalized.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
        {
            // Graphics and other drivers install their menu DLLs in the driver store; those belong to the hardware maker.
            return !normalized.Contains("\\DriverStore\\", StringComparison.OrdinalIgnoreCase);
        }

        return normalized.Contains("\\Windows Defender\\", StringComparison.OrdinalIgnoreCase)
            && (normalized.Contains("\\Program Files", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\ProgramData\\Microsoft\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a change is allowed: never for Windows' entries; Microsoft apps only in Advanced mode; programs always.
    /// Returns the reason when it is not.
    /// </summary>
    public static string? WhyLocked(MenuEntry entry, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Owner == MenuEntryOwner.Windows)
        {
            return "Part of Windows. PC Checker shows it for reference and does not change it.";
        }

        if (entry.Owner == MenuEntryOwner.MicrosoftApp && !advancedMode)
        {
            return "Added by a Microsoft app. Turn on Advanced mode to change it.";
        }

        if (entry.DisabledElsewhere)
        {
            return "Another tool turned this off its own way. Turn it back on in that tool.";
        }

        if (entry.Kind is MenuEntryKind.ShellExtension or MenuEntryKind.AppExtension && entry.Clsid is null)
        {
            return "This extension has no class ID, so it cannot be blocked.";
        }

        return null;
    }

    /// <summary>A line describing the kind for the list ("Command", "Shell extension"…).</summary>
    public static string KindText(MenuEntryKind kind) => kind switch
    {
        MenuEntryKind.Command => "Command",
        MenuEntryKind.ShellExtension => "Shell extension",
        MenuEntryKind.SendTo => "Send to",
        _ => "Windows 11 app",
    };

    /// <summary>A verb counts as turned off when it has LegacyDisable; ProgrammaticAccessOnly verbs never show and are skipped.</summary>
    public static bool IsVerbHidden(IEnumerable<string> valueNames, out bool programmaticOnly)
    {
        var names = valueNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        programmaticOnly = names.Contains("ProgrammaticAccessOnly");
        return names.Contains("LegacyDisable");
    }

    private static bool IsRooted(string path)
    {
        string p = path.Trim().Trim('"');
        return (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) || p.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static string FileName(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
