using PcChecker.Core.Common;

namespace PcChecker.Core.ContextMenus;

/// <summary>Sample right-click menu of a gaming desktop with common programs installed. Changes stay in memory.</summary>
public sealed class DemoContextMenuService : IContextMenuService
{
    private readonly object _gate = new();
    private readonly List<MenuEntry> _entries = Build();
    private bool _classic;

    public string SendToFolder => @"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\SendTo";

    public async Task<MenuScanResult> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading the right-click menu", 30, "Files"));
        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Checking shell extensions", 75, null));
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return new MenuScanResult(_entries.ToList(), [], DateTimeOffset.Now, new ClassicMenuState(true, _classic));
        }
    }

    public async Task<OperationResult> SetEnabledAsync(MenuEntry entry, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        if (ContextMenuRules.WhyLocked(entry, advancedMode: true) is { } locked)
        {
            return OperationResult.Fail(locked);
        }

        lock (_gate)
        {
            int index = _entries.FindIndex(e => e.Id == entry.Id);
            if (index < 0)
            {
                return OperationResult.Fail("That entry is no longer there.");
            }

            _entries[index] = _entries[index] with { IsEnabled = enabled };
        }

        return OperationResult.Ok(enabled
            ? $"\"{entry.Text}\" is back in the right-click menu (demo). Restart File Explorer to see it."
            : $"\"{entry.Text}\" is turned off (demo). Nothing was deleted; turn it back on here at any time.");
    }

    public async Task<OperationResult> SetClassicMenuAsync(bool classic, CancellationToken cancellationToken)
    {
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        _classic = classic;
        return OperationResult.Ok(classic
            ? "The full right-click menu is on (demo). Restart File Explorer to see it."
            : "The Windows 11 compact menu is back (demo). Restart File Explorer to see it.");
    }

    public async Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("File Explorer restarted (demo).");
    }

    private static List<MenuEntry> Build()
    {
        const string Local = @"C:\Users\Alex\AppData\Local";
        static MenuEntryKey Hklm(string path) => new(MenuEntryScope.Machine, path);
        static MenuEntryKey Hkcu(string path) => new(MenuEntryScope.User, path);

        return
        [
            Verb("VSCode", "Open with Code", $@"{Local}\Programs\Microsoft VS Code\Code.exe", "Microsoft Corporation", MenuEntryOwner.MicrosoftApp,
                ["Files", "Folders", "Inside a folder"], true,
                Hkcu(@"Software\Classes\*\shell\VSCode"), Hkcu(@"Software\Classes\Directory\shell\VSCode"), Hkcu(@"Software\Classes\Directory\Background\shell\VSCode")),
            Verb("git_shell", "Open Git Bash here", @"C:\Program Files\Git\git-bash.exe", "Johannes Schindelin", MenuEntryOwner.Program,
                ["Folders", "Inside a folder"], true,
                Hklm(@"Software\Classes\Directory\shell\git_shell"), Hklm(@"Software\Classes\Directory\Background\shell\git_shell")),
            Verb("git_gui", "Open Git GUI here", @"C:\Program Files\Git\cmd\git-gui.exe", "Johannes Schindelin", MenuEntryOwner.Program,
                ["Folders", "Inside a folder"], false,
                Hklm(@"Software\Classes\Directory\shell\git_gui"), Hklm(@"Software\Classes\Directory\Background\shell\git_gui")),
            Verb("AddToPlaylistVLC", "Add to VLC media player's Playlist", @"C:\Program Files\VideoLAN\VLC\vlc.exe", "VideoLAN", MenuEntryOwner.Program,
                ["Folders", ".mp3 files", ".mp4 files", ".mkv files"], true,
                Hklm(@"Software\Classes\Directory\shell\AddToPlaylistVLC")),
            Verb("PlayWithVLC", "Play with VLC media player", @"C:\Program Files\VideoLAN\VLC\vlc.exe", "VideoLAN", MenuEntryOwner.Program,
                ["Folders", ".mp3 files", ".mp4 files", ".mkv files"], true,
                Hklm(@"Software\Classes\Directory\shell\PlayWithVLC")),
            Verb("OldEditor", "Edit with Old Editor", @"C:\Program Files (x86)\OldEditor\oldedit.exe", null, MenuEntryOwner.Program,
                ["Files"], true, Hklm(@"Software\Classes\*\shell\OldEditor")) with
            {
                IsBroken = true,
                Note = "The program it runs is missing, so this entry does nothing.",
            },
            Handler("7-Zip", "{23170F69-40C1-278A-1000-000100020000}", @"C:\Program Files\7-Zip\7-zip.dll", "Igor Pavlov", MenuEntryOwner.Program,
                ["Files", "Folders", "Drives"], true),
            Handler("Edit with Notepad++", "{B298D29A-A6ED-11DE-BA8C-A68E55D89593}", @"C:\Program Files\Notepad++\NppShell.dll", "Notepad++", MenuEntryOwner.Program,
                ["Files"], true),
            Handler("Scan with Malwarebytes", "{57CE581A-0CB6-4266-9CA0-19364C90A0B3}", @"C:\Program Files\Malwarebytes\Anti-Malware\mbshlext.dll", "Malwarebytes Inc.", MenuEntryOwner.Program,
                ["Files and folders"], false),
            Handler("NVIDIA Control Panel", "{3D1975AF-48C6-4F8E-A182-BE0E08FA86A9}", @"C:\Windows\System32\DriverStore\FileRepository\nv_dispsig.inf_amd64_8b2a\nvshext.dll", "NVIDIA Corporation", MenuEntryOwner.Program,
                ["Desktop"], true),
            Handler("Share with Dropbox", "{FB314ED9-A251-47B7-93E1-CDD82E34AF8B}", @"C:\Program Files (x86)\Dropbox\Client\DropboxExt64.dll", "Dropbox, Inc.", MenuEntryOwner.Program,
                ["Files and folders"], true),
            AppExtension("EditInNotepad", "Edit in Notepad", "{CFF39BF0-2E6B-4B53-A4A8-25E4D2D0BDD0}", "Notepad", MenuEntryOwner.MicrosoftApp, ["Files"], true),
            AppExtension("EditWithClipchamp", "Edit with Clipchamp", "{8AB635F8-9A67-4698-AB99-784AD929F217}", "Clipchamp", MenuEntryOwner.MicrosoftApp, [".mp4 files", ".mkv files"], true),
            AppExtension("PowerRename", "Rename with PowerRename", "{1861E28B-A1F0-4EF4-A1FE-4C8CA88E2174}", "PowerToys", MenuEntryOwner.MicrosoftApp, ["Files and folders"], true),
            SendTo("Notepad++.lnk", @"C:\Program Files\Notepad++\notepad++.exe", "Notepad++", MenuEntryOwner.Program, true),
            SendTo("FTP Uploader.lnk", @"C:\Program Files (x86)\FTP Uploader\upload.exe", null, MenuEntryOwner.Program, true) with
            {
                IsBroken = true,
                Note = "The program it opens is missing, so this item does nothing.",
            },
            SendTo("Compressed (zipped) folder.ZFSendToTarget", null, "Microsoft Windows", MenuEntryOwner.Windows, true),
            SendTo("Desktop (create shortcut).DeskLink", null, "Microsoft Windows", MenuEntryOwner.Windows, true),
            SendTo("Documents.mydocs", null, "Microsoft Windows", MenuEntryOwner.Windows, true),
            SendTo("Bluetooth device.lnk", @"C:\Windows\System32\fsquirt.exe", "Microsoft Windows", MenuEntryOwner.Windows, true),
            Verb("opennewwindow", "Open in new window", @"C:\Windows\explorer.exe", "Microsoft Windows", MenuEntryOwner.Windows,
                ["Folders"], true, Hklm(@"Software\Classes\Folder\shell\opennewwindow")),
            Verb("pintohome", "Pin to Quick access", @"C:\Windows\System32\shell32.dll", "Microsoft Windows", MenuEntryOwner.Windows,
                ["Folders"], true, Hklm(@"Software\Classes\Folder\shell\pintohome")),
            Handler("Sharing", "{F81E9010-6EA4-11CE-A7FF-00AA003CA9F6}", @"C:\Windows\System32\ntshrui.dll", "Microsoft Windows", MenuEntryOwner.Windows,
                ["Folders", "Drives"], true),
            Handler("Restore previous versions", "{596AB062-B4D2-4215-9F74-E9109B0A8153}", @"C:\Windows\System32\twext.dll", "Microsoft Windows", MenuEntryOwner.Windows,
                ["Files and folders", "Drives"], true),
            Handler("Scan with Microsoft Defender", "{09A47860-11B0-4DA5-AFA5-26D86198A780}", @"C:\Program Files\Windows Defender\shellext.dll", "Microsoft Windows", MenuEntryOwner.Windows,
                ["Files and folders"], true),
            Handler("Cast to Device", "{7AD84985-87B4-4A16-BE58-8B72A5B390F7}", @"C:\Windows\System32\playtomenu.dll", "Microsoft Windows", MenuEntryOwner.Windows,
                [".mp3 files", ".mp4 files"], true),
            AppExtension("OpenTerminal", "Open in Terminal", "{9F156763-7844-4DC4-B2B1-901F640F5155}", "Terminal", MenuEntryOwner.MicrosoftApp, ["Folders", "Inside a folder"], true),
        ];
    }

    private static MenuEntry Verb(string name, string text, string file, string? publisher, MenuEntryOwner owner, string[] locations, bool enabled, params MenuEntryKey[] keys) => new()
    {
        Id = "verb|" + name.ToLowerInvariant(),
        Kind = MenuEntryKind.Command,
        Text = text,
        KeyName = name,
        Keys = keys,
        Command = $"\"{file}\" \"%1\"",
        FilePath = file,
        Publisher = publisher,
        Locations = locations,
        IsEnabled = enabled,
        Owner = owner,
    };

    private static MenuEntry Handler(string text, string clsid, string file, string publisher, MenuEntryOwner owner, string[] locations, bool enabled) => new()
    {
        Id = "shellex|" + clsid,
        Kind = MenuEntryKind.ShellExtension,
        Text = text,
        KeyName = text,
        Clsid = clsid,
        Keys = [new MenuEntryKey(MenuEntryScope.Machine, $@"Software\Classes\*\shellex\ContextMenuHandlers\{text}")],
        FilePath = file,
        Publisher = publisher,
        Locations = locations,
        IsEnabled = enabled,
        Owner = owner,
    };

    private static MenuEntry AppExtension(string verbId, string text, string clsid, string app, MenuEntryOwner owner, string[] locations, bool enabled) => new()
    {
        Id = "appx|" + clsid,
        Kind = MenuEntryKind.AppExtension,
        Text = text,
        KeyName = verbId,
        Clsid = clsid,
        FilePath = @"C:\Program Files\WindowsApps",
        Publisher = app,
        Locations = locations,
        IsEnabled = enabled,
        Owner = owner,
    };

    private static MenuEntry SendTo(string fileName, string? target, string? publisher, MenuEntryOwner owner, bool enabled) => new()
    {
        Id = "sendto|" + fileName.ToLowerInvariant(),
        Kind = MenuEntryKind.SendTo,
        Text = SendToRules.DisplayName(fileName),
        KeyName = fileName,
        FilePath = target ?? @"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\SendTo\" + fileName,
        Publisher = publisher,
        Locations = ["Send to"],
        IsEnabled = enabled,
        Owner = owner,
    };
}
