using PcChecker.Core.ContextMenus;
using Xunit;

namespace PcChecker.Core.Tests;

public class IndirectStringTests
{
    [Theory]
    [InlineData(@"@%SystemRoot%\system32\shell32.dll,-8506", @"%SystemRoot%\system32\shell32.dll", -8506)]
    [InlineData(@"@C:\Program Files\App\menu.dll,12", @"C:\Program Files\App\menu.dll", 12)]
    [InlineData(@"@""C:\Program Files\App\menu.dll"",-3;v1", @"C:\Program Files\App\menu.dll", -3)]
    public void Resource_references_split_into_file_and_id(string text, string file, int id)
    {
        Assert.True(IndirectString.TryParseResource(text, out var parsedFile, out var parsedId));
        Assert.Equal(file, parsedFile);
        Assert.Equal(id, parsedId);
    }

    [Theory]
    [InlineData("Open with Code")]
    [InlineData("@")]
    [InlineData(@"@shell32.dll")]
    [InlineData(@"@shell32.dll,abc")]
    [InlineData("@{Microsoft.Photos_2024.1.0_x64__8wekyb3d8bbwe?ms-resource://Microsoft.Photos/Resources/AppName}")]
    public void Other_text_is_not_a_resource_reference(string text)
    {
        Assert.False(IndirectString.TryParseResource(text, out _, out _));
    }

    [Fact]
    public void Package_resources_are_recognised_and_built()
    {
        Assert.True(IndirectString.IsPackageResource("@{Microsoft.WindowsNotepad_11.2401.0_x64__8wekyb3d8bbwe?ms-resource://Microsoft.WindowsNotepad/Resources/AppName}"));
        Assert.Equal("@{Full_1?ms-resource://Microsoft.WindowsNotepad/Resources/AppStoreName}",
            IndirectString.ForPackage("Full_1", "Microsoft.WindowsNotepad", "ms-resource:AppStoreName"));
        Assert.Equal("@{Full_1?ms-resource://Microsoft.WindowsNotepad/Strings/Name}",
            IndirectString.ForPackage("Full_1", "Microsoft.WindowsNotepad", "ms-resource:Strings/Name"));
        Assert.Null(IndirectString.ForPackage("Full_1", "Pkg", "Plain name"));
    }

    [Theory]
    [InlineData("&Open with Code", null, "Open with Code")]
    [InlineData("Copy && paste", null, "Copy & paste")]
    [InlineData(@"@shell32.dll,-8506", "Pin to &Start", "Pin to Start")]
    [InlineData(@"@shell32.dll,-8506", null, "Open With Code")]
    public void Display_text_resolves_cleans_and_falls_back_to_the_key_name(string raw, string? resolved, string expected)
    {
        Assert.Equal(expected, IndirectString.DisplayText(raw, resolved, "OpenWithCode"));
    }

    [Theory]
    [InlineData("git_shell", "git shell")]
    [InlineData("AddToPlaylistVLC", "Add To Playlist VLC")]
    [InlineData("{B298D29A-A6ED-11DE-BA8C-A68E55D89593}", "{B298D29A-A6ED-11DE-BA8C-A68E55D89593}")]
    public void Key_names_are_made_readable(string name, string expected)
    {
        Assert.Equal(expected, IndirectString.Humanize(name));
    }
}

public class ContextMenuRulesTests
{
    private const string Windows = @"C:\Windows";

    [Theory]
    [InlineData("*", "Files")]
    [InlineData("AllFilesystemObjects", "Files and folders")]
    [InlineData("Directory", "Folders")]
    [InlineData("Folder", "Folders")]
    [InlineData(@"Directory\Background", "Inside a folder")]
    [InlineData("Drive", "Drives")]
    [InlineData("DesktopBackground", "Desktop")]
    [InlineData(".TXT", ".txt files")]
    [InlineData(@"SystemFileAssociations\.mp4", ".mp4 files")]
    public void Class_keys_become_plain_locations(string classKey, string expected)
    {
        Assert.Equal(expected, ContextMenuRules.LocationOf(classKey));
    }

    [Fact]
    public void Locations_merge_without_duplicates_in_a_fixed_order()
    {
        var merged = ContextMenuRules.MergeLocations(["Drives", "Folders", ".txt files", "Files", "Folders"]);
        Assert.Equal(["Files", "Folders", "Drives", ".txt files"], merged);
    }

    [Fact]
    public void Twenty_common_file_types_are_read()
    {
        Assert.Equal(20, ContextMenuRules.CommonExtensions.Count);
        Assert.All(ContextMenuRules.CommonExtensions, e => Assert.StartsWith(".", e));
        Assert.Equal(ContextMenuRules.CommonExtensions.Count, ContextMenuRules.CommonExtensions.Distinct().Count());
    }

    [Theory]
    [InlineData("7-Zip", "{23170F69-40C1-278A-1000-000100020000}", "{23170F69-40C1-278A-1000-000100020000}", false)]
    [InlineData("{b298d29a-a6ed-11de-ba8c-a68e55d89593}", null, "{B298D29A-A6ED-11DE-BA8C-A68E55D89593}", false)]
    [InlineData("WinRAR", "-{B41DB860-64E4-11D2-9906-E49FADC173CA}", "{B41DB860-64E4-11D2-9906-E49FADC173CA}", true)]
    [InlineData("Broken", "not a guid", null, false)]
    public void Handler_class_ids_come_from_the_value_or_the_key_name(string keyName, string? value, string? expected, bool prefixed)
    {
        Assert.Equal(expected, ContextMenuRules.HandlerClsid(keyName, value, out bool disabled));
        Assert.Equal(prefixed, disabled);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Microsoft VS Code\Code.exe"" ""%1""", @"C:\Program Files\Microsoft VS Code\Code.exe")]
    [InlineData(@"rundll32.exe ""C:\Program Files\Tool\menu.dll"",Run %1", @"C:\Program Files\Tool\menu.dll")]
    [InlineData(@"rundll32 C:\Tools\shellx.dll,Entry ""%1""", @"C:\Tools\shellx.dll")]
    [InlineData(@"wscript.exe //B ""C:\Scripts\send.vbs"" ""%1""", @"C:\Scripts\send.vbs")]
    [InlineData(@"cmd.exe /s /k pushd ""%V""", "cmd.exe")]
    public void The_program_of_a_command_skips_host_programs(string command, string expected)
    {
        Assert.Equal(expected, ContextMenuRules.ProgramOfCommand(command, _ => null));
    }

    [Fact]
    public void Verbs_with_LegacyDisable_are_hidden_and_programmatic_ones_are_skipped()
    {
        Assert.True(ContextMenuRules.IsVerbHidden(["", "Icon", "legacydisable"], out bool programmatic));
        Assert.False(programmatic);
        Assert.False(ContextMenuRules.IsVerbHidden(["MUIVerb", "ProgrammaticAccessOnly"], out programmatic));
        Assert.True(programmatic);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\shell32.dll", null, MenuEntryOwner.Windows)]
    [InlineData(@"C:\Program Files\Windows Defender\shellext.dll", null, MenuEntryOwner.Windows)]
    [InlineData(@"C:\Program Files\App\x.dll", "Microsoft Windows", MenuEntryOwner.Windows)]
    [InlineData(@"C:\Users\Alex\AppData\Local\Programs\Microsoft VS Code\Code.exe", "Microsoft Corporation", MenuEntryOwner.MicrosoftApp)]
    [InlineData(@"C:\Program Files\7-Zip\7-zip.dll", "Igor Pavlov", MenuEntryOwner.Program)]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\nv_disp.inf_amd64\nvshext.dll", "NVIDIA Corporation", MenuEntryOwner.Program)]
    [InlineData(@"C:\Windows2\tool.dll", null, MenuEntryOwner.Program)]
    public void Windows_entries_are_recognised_by_folder_and_signer(string file, string? signer, MenuEntryOwner expected)
    {
        Assert.Equal(expected, ContextMenuRules.ClassifyOwner(file, signer, Windows));
    }

    [Fact]
    public void Built_in_verbs_and_handlers_without_a_file_are_Windows_entries()
    {
        Assert.Equal(MenuEntryOwner.Windows, ContextMenuRules.ClassifyOwner("cmd.exe", null, Windows, verbName: "cmd"));
        Assert.Equal(MenuEntryOwner.Windows, ContextMenuRules.ClassifyOwner(null, null, Windows, clsid: "{f81e9010-6ea4-11ce-a7ff-00aa003ca9f6}"));
        Assert.Equal(MenuEntryOwner.Program, ContextMenuRules.ClassifyOwner(null, null, Windows, verbName: "VSCode"));

        // A program that reuses a built-in verb name with its own file is still that program's.
        Assert.Equal(MenuEntryOwner.Program, ContextMenuRules.ClassifyOwner(@"D:\Apps\Editor\edit.exe", "Some Vendor", Windows, verbName: "edit"));
    }

    [Fact]
    public void Locks_protect_Windows_entries_and_need_Advanced_mode_for_Microsoft_apps()
    {
        var windows = Entry(MenuEntryOwner.Windows);
        var microsoft = Entry(MenuEntryOwner.MicrosoftApp);
        var program = Entry(MenuEntryOwner.Program);

        Assert.NotNull(ContextMenuRules.WhyLocked(windows, advancedMode: true));
        Assert.NotNull(ContextMenuRules.WhyLocked(microsoft, advancedMode: false));
        Assert.Null(ContextMenuRules.WhyLocked(microsoft, advancedMode: true));
        Assert.Null(ContextMenuRules.WhyLocked(program, advancedMode: false));
        Assert.NotNull(ContextMenuRules.WhyLocked(program with { DisabledElsewhere = true }, advancedMode: true));
        Assert.NotNull(ContextMenuRules.WhyLocked(program with { Kind = MenuEntryKind.ShellExtension, Clsid = null }, advancedMode: true));
    }

    private static MenuEntry Entry(MenuEntryOwner owner) => new()
    {
        Id = "verb|x",
        Kind = MenuEntryKind.Command,
        Text = "X",
        KeyName = "x",
        Owner = owner,
    };
}

public class ShellExtensionBlockListTests
{
    private const string Clsid = "{23170F69-40C1-278A-1000-000100020000}";

    [Theory]
    [InlineData("{23170f69-40c1-278a-1000-000100020000}", Clsid)]
    [InlineData("23170F69-40C1-278A-1000-000100020000", Clsid)]
    [InlineData(" " + Clsid + " ", Clsid)]
    [InlineData("7-Zip", null)]
    [InlineData("", null)]
    public void Class_ids_are_normalised(string text, string? expected)
    {
        Assert.Equal(expected, ShellExtensionBlockList.Normalize(text));
    }

    [Fact]
    public void Blocked_lists_match_regardless_of_case_and_braces()
    {
        Assert.True(ShellExtensionBlockList.IsBlocked(Clsid, ["{23170f69-40c1-278a-1000-000100020000}"], []));
        Assert.True(ShellExtensionBlockList.IsBlocked(Clsid, [], ["23170F69-40C1-278A-1000-000100020000"]));
        Assert.False(ShellExtensionBlockList.IsBlocked(Clsid, ["{00000000-0000-0000-0000-000000000000}", "junk"], []));
        Assert.Equal("{23170f69-40c1-278a-1000-000100020000}",
            ShellExtensionBlockList.MatchingName(["other", "{23170f69-40c1-278a-1000-000100020000}"], Clsid));
    }

    [Fact]
    public void Turning_off_adds_for_the_user_only_when_not_blocked_anywhere()
    {
        Assert.Equal(BlockListAction.AddForUser, ShellExtensionBlockList.Plan(Clsid, turnOn: false, [], []).Action);
        Assert.Equal(BlockListAction.None, ShellExtensionBlockList.Plan(Clsid, turnOn: false, [Clsid], []).Action);
        Assert.Equal(BlockListAction.None, ShellExtensionBlockList.Plan(Clsid, turnOn: false, [], [Clsid]).Action);
    }

    [Fact]
    public void Turning_on_removes_it_from_every_list_that_holds_it()
    {
        var both = ShellExtensionBlockList.Plan(Clsid, turnOn: true, [Clsid], [Clsid.ToLowerInvariant()]);
        Assert.Equal(BlockListAction.Remove, both.Action);
        Assert.True(both.InUserList);
        Assert.True(both.InMachineList);

        var machineOnly = ShellExtensionBlockList.Plan(Clsid, turnOn: true, [], [Clsid]);
        Assert.Equal(BlockListAction.Remove, machineOnly.Action);
        Assert.False(machineOnly.InUserList);

        Assert.Equal(BlockListAction.None, ShellExtensionBlockList.Plan(Clsid, turnOn: true, [], []).Action);
    }
}

public class PackagedMenuAndSendToTests
{
    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
                 xmlns:desktop5="http://schemas.microsoft.com/appx/manifest/desktop/windows10/5"
                 xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10">
          <Identity Name="Microsoft.WindowsNotepad" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" Version="11.2401.0.0" />
          <Properties>
            <DisplayName>ms-resource:AppStoreName</DisplayName>
            <PublisherDisplayName>Microsoft Corporation</PublisherDisplayName>
          </Properties>
          <Applications>
            <Application Id="App">
              <Extensions>
                <desktop4:Extension Category="windows.fileExplorerContextMenus">
                  <desktop4:FileExplorerContextMenus>
                    <desktop5:ItemType Type="*">
                      <desktop5:Verb Id="EditInNotepad" Clsid="CFF39BF0-2E6B-4B53-A4A8-25E4D2D0BDD0" />
                    </desktop5:ItemType>
                    <desktop5:ItemType Type="Directory">
                      <desktop5:Verb Id="Broken" Clsid="not-a-guid" />
                    </desktop5:ItemType>
                  </desktop4:FileExplorerContextMenus>
                </desktop4:Extension>
                <com:Extension Category="windows.comServer">
                  <com:ComServer>
                    <com:SurrogateServer DisplayName="Notepad menu">
                      <com:Class Id="CFF39BF0-2E6B-4B53-A4A8-25E4D2D0BDD0" Path="NotepadExplorerCommand.dll" ThreadingModel="STA" />
                    </com:SurrogateServer>
                  </com:ComServer>
                </com:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void Manifest_verbs_are_read_with_their_COM_file()
    {
        var parsed = PackagedMenuManifestParser.Parse(Manifest);
        Assert.NotNull(parsed);
        Assert.Equal("Microsoft.WindowsNotepad", parsed!.PackageName);
        Assert.Equal("ms-resource:AppStoreName", parsed.DisplayName);
        var verb = Assert.Single(parsed.Verbs);
        Assert.Equal("*", verb.ItemType);
        Assert.Equal("EditInNotepad", verb.VerbId);
        Assert.Equal("{CFF39BF0-2E6B-4B53-A4A8-25E4D2D0BDD0}", verb.Clsid);
        Assert.Equal("NotepadExplorerCommand.dll", verb.ComPath);
    }

    [Fact]
    public void Broken_or_empty_manifests_give_nothing()
    {
        Assert.Null(PackagedMenuManifestParser.Parse(null));
        Assert.Null(PackagedMenuManifestParser.Parse("<Package><Identity"));
        Assert.Null(PackagedMenuManifestParser.Parse("<Package><Properties /></Package>"));
        Assert.Empty(PackagedMenuManifestParser.Parse("<Package><Identity Name=\"A.B\" /></Package>")!.Verbs);
    }

    [Theory]
    [InlineData("Compressed (zipped) folder.ZFSendToTarget", null, true)]
    [InlineData("Desktop (create shortcut).DeskLink", null, true)]
    [InlineData("Documents.mydocs", null, true)]
    [InlineData("Bluetooth device.lnk", @"C:\Windows\System32\fsquirt.exe", true)]
    [InlineData("Blåtand.lnk", @"C:\Windows\System32\fsquirt.exe", true)]
    [InlineData("Notepad++.lnk", @"C:\Program Files\Notepad++\notepad++.exe", false)]
    public void Windows_own_send_to_items_are_recognised(string fileName, string? target, bool expected)
    {
        Assert.Equal(expected, SendToRules.IsWindowsItem(fileName, target, @"C:\Windows"));
    }

    [Fact]
    public void Send_to_names_and_backup_names()
    {
        Assert.Equal("Notepad++", SendToRules.DisplayName("Notepad++.lnk"));
        Assert.False(SendToRules.IsShown("desktop.ini", FileAttributes.Normal));
        Assert.False(SendToRules.IsShown("x.lnk", FileAttributes.Hidden));
        Assert.True(SendToRules.IsShown("x.lnk", FileAttributes.Archive));

        var taken = new HashSet<string> { "Tool.lnk", "Tool (2).lnk" };
        Assert.Equal("Tool (3).lnk", SendToRules.FreeName("Tool.lnk", taken.Contains));
        Assert.Equal("New.lnk", SendToRules.FreeName("New.lnk", taken.Contains));
    }

    [Fact]
    public void Classic_menu_switch_needs_an_empty_default_value_on_Windows_11()
    {
        Assert.False(ClassicMenuSwitch.IsSupported(19045));
        Assert.True(ClassicMenuSwitch.IsSupported(22631));
        Assert.True(ClassicMenuSwitch.IsClassic(true, string.Empty));
        Assert.False(ClassicMenuSwitch.IsClassic(true, null));
        Assert.False(ClassicMenuSwitch.IsClassic(false, null));
        Assert.True(ClassicMenuSwitch.IsForeignRegistration(true, @"C:\Something\handler.dll"));
        Assert.False(ClassicMenuSwitch.IsForeignRegistration(true, string.Empty));
    }

    [Fact]
    public async Task Filters_hide_Windows_entries_unless_asked_and_match_places_and_text()
    {
        var entries = (await new DemoContextMenuService().ScanAsync(null, CancellationToken.None)).Entries;

        var everything = ContextMenuFilter.Apply(entries, null, MenuFilter.All, showWindowsEntries: false);
        Assert.DoesNotContain(everything, e => e.Owner == MenuEntryOwner.Windows);
        Assert.Contains(ContextMenuFilter.Apply(entries, null, MenuFilter.All, showWindowsEntries: true), e => e.Owner == MenuEntryOwner.Windows);

        Assert.All(ContextMenuFilter.Apply(entries, null, MenuFilter.TurnedOff, false), e => Assert.False(e.IsEnabled));
        Assert.All(ContextMenuFilter.Apply(entries, null, MenuFilter.SendTo, false), e => Assert.Equal(MenuEntryKind.SendTo, e.Kind));
        Assert.All(ContextMenuFilter.Apply(entries, null, MenuFilter.NotWorking, false), e => Assert.True(e.IsBroken));
        Assert.Contains(ContextMenuFilter.Apply(entries, null, MenuFilter.DesktopAndDrives, false), e => e.Text == "NVIDIA Control Panel");
        Assert.Contains(ContextMenuFilter.Apply(entries, null, MenuFilter.Files, false), e => e.Locations.Contains(".mp4 files"));

        var vlc = ContextMenuFilter.Apply(entries, "videolan", MenuFilter.All, false);
        Assert.Equal(2, vlc.Count);
    }

    [Fact]
    public async Task Demo_menu_has_programs_and_Windows_entries_and_refuses_Windows_changes()
    {
        var demo = new DemoContextMenuService();
        var scan = await demo.ScanAsync(null, CancellationToken.None);
        Assert.Contains(scan.Entries, e => e.Owner == MenuEntryOwner.Program);
        Assert.Contains(scan.Entries, e => e.Owner == MenuEntryOwner.Windows);
        Assert.Contains(scan.Entries, e => e.Kind == MenuEntryKind.SendTo);
        Assert.Contains(scan.Entries, e => e.Kind == MenuEntryKind.AppExtension);
        Assert.Equal(scan.Entries.Count, scan.Entries.Select(e => e.Id).Distinct().Count());

        var windowsEntry = scan.Entries.First(e => e.Owner == MenuEntryOwner.Windows);
        Assert.False((await demo.SetEnabledAsync(windowsEntry, false, CancellationToken.None)).Success);

        var program = scan.Entries.First(e => e.Owner == MenuEntryOwner.Program && e.IsEnabled);
        Assert.True((await demo.SetEnabledAsync(program, false, CancellationToken.None)).Success);
        var again = await demo.ScanAsync(null, CancellationToken.None);
        Assert.False(again.Entries.Single(e => e.Id == program.Id).IsEnabled);
    }
}
