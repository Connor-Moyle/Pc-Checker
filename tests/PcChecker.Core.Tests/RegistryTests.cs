using System.Text;
using PcChecker.Core.Optimization;
using PcChecker.Core.RegistryHealth;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>A fake machine for <see cref="RegistryPathTools"/>: a fixed C: and D:, a removable E:, a network Z:.</summary>
internal static class FakeMachine
{
    public static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["windir"] = @"C:\Windows",
        ["SystemDrive"] = "C:",
        ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramW6432"] = @"C:\Program Files",
        ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
        ["CommonProgramFiles"] = @"C:\Program Files\Common Files",
        ["CommonProgramFiles(x86)"] = @"C:\Program Files (x86)\Common Files",
        ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
        ["PATH"] = @"C:\Windows\System32;C:\Tools\bin;%LOCALAPPDATA%\Programs\Go\bin",
    };

    public static PathProbe Probe(params string[] existing)
    {
        var files = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in existing)
        {
            for (var d = RegistryPathTools.DirectoryName(f); d is not null && d.Length > 3; d = RegistryPathTools.DirectoryName(d))
            {
                dirs.Add(d);
            }
        }

        return new PathProbe
        {
            Environment = name => Env.TryGetValue(name, out var v) ? v : null,
            FileExists = files.Contains,
            DirectoryExists = p => dirs.Contains(p.TrimEnd('\\')),
            Drive = letter => letter switch
            {
                'C' or 'D' => DriveState.Fixed,
                'E' => DriveState.Removable,
                'Z' => DriveState.Network,
                _ => DriveState.Missing,
            },
        };
    }

    public static string? Get(string name) => Env.TryGetValue(name, out var v) ? v : null;
}

public class RegistryPathToolsTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", @"C:\Program Files\App\app.exe")]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" \"%1\"", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Tools\tool.exe /S /quiet", @"C:\Tools\tool.exe")]
    [InlineData(@"C:\Program Files\Some App\app.exe %1", @"C:\Program Files\Some App\app.exe")]
    [InlineData(@"C:\Apps\viewer.exe,0", @"C:\Apps\viewer.exe")]
    [InlineData("\"C:\\Apps\\viewer.exe\",1", @"C:\Apps\viewer.exe")]
    [InlineData(@"C:\NoExtension\binary", @"C:\NoExtension\binary")]
    [InlineData("cmd /c \"C:\\x y\\a.bat\"", "cmd")]
    public void ExtractPath_handles_quotes_arguments_and_placeholders(string value, string expected)
    {
        Assert.Equal(expected, RegistryPathTools.ExtractPath(value, FakeMachine.Get));
    }

    [Fact]
    public void ExtractPath_expands_environment_variables_and_keeps_unknown_ones()
    {
        Assert.Equal(@"C:\Users\me\AppData\Local\App\app.exe", RegistryPathTools.ExtractPath(@"%LOCALAPPDATA%\App\app.exe -x", FakeMachine.Get));
        Assert.Equal(@"C:\Program Files (x86)\Old\old.exe", RegistryPathTools.ExtractPath("\"%ProgramFiles(x86)%\\Old\\old.exe\"", FakeMachine.Get));
        Assert.Equal(@"%NOPE%\x.exe", RegistryPathTools.ExtractPath(@"%NOPE%\x.exe", FakeMachine.Get));
    }

    [Theory]
    [InlineData(@"rundll32.exe C:\Program Files\Vendor\helper.dll,Start", @"C:\Program Files\Vendor\helper.dll")]
    [InlineData("rundll32 \"C:\\Vendor Dir\\x.dll\",Entry arg", @"C:\Vendor Dir\x.dll")]
    [InlineData(@"%SystemRoot%\System32\rundll32.exe C:\Vendor\y.dll,Run", @"C:\Vendor\y.dll")]
    public void ExtractPath_returns_the_dll_hosted_by_rundll32(string value, string expected)
    {
        Assert.Equal(expected, RegistryPathTools.ExtractPath(value, FakeMachine.Get));
    }

    [Fact]
    public void ExtractPath_reads_resource_strings_and_kernel_paths()
    {
        Assert.Equal(@"C:\Windows\system32\shell32.dll", RegistryPathTools.ExtractPath(@"@%SystemRoot%\system32\shell32.dll,-21787", FakeMachine.Get));
        Assert.Equal("shell32.dll", RegistryPathTools.ExtractPath("@shell32.dll,-123", FakeMachine.Get));
        Assert.Null(RegistryPathTools.ExtractPath("@{Microsoft.Windows.Photos_8wekyb3d8bbwe?ms-resource://Photos/Files/x}", FakeMachine.Get));
        Assert.Equal(@"C:\Windows\System32\svchost.exe", RegistryPathTools.ExtractPath(@"\SystemRoot\System32\svchost.exe -k netsvcs", FakeMachine.Get));
        Assert.Equal(@"C:\Drivers\x.sys", RegistryPathTools.ExtractPath(@"\??\C:\Drivers\x.sys", FakeMachine.Get));
        Assert.Null(RegistryPathTools.ExtractPath("   ", FakeMachine.Get));
    }

    [Fact]
    public void Evaluate_flags_only_missing_files_on_local_fixed_drives()
    {
        var probe = FakeMachine.Probe(@"C:\Apps\present.exe");
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate(@"C:\Apps\present.exe", probe));
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate(@"C:\Apps", probe));
        Assert.Equal(TargetState.Missing, RegistryPathTools.Evaluate(@"C:\Apps\gone.exe", probe));
        Assert.Equal(TargetState.Missing, RegistryPathTools.Evaluate(@"D:\Games\old\game.exe", probe));
    }

    [Theory]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData(@"//server/share/app.exe")]
    [InlineData(@"E:\PortableApps\app.exe")]
    [InlineData(@"Z:\Mapped\app.exe")]
    [InlineData(@"Q:\Unplugged\app.exe")]
    [InlineData(@"%UNKNOWNVAR%\app.exe")]
    [InlineData(@"subfolder\app.exe")]
    [InlineData(@"C:\bad|name.exe")]
    public void Evaluate_never_flags_network_removable_missing_drive_or_unprovable_paths(string path)
    {
        Assert.Equal(TargetState.Unknown, RegistryPathTools.Evaluate(path, FakeMachine.Probe()));
    }

    [Fact]
    public void Evaluate_resolves_bare_names_through_system_folders_and_path()
    {
        var probe = FakeMachine.Probe(@"C:\Windows\System32\notepad.exe", @"C:\Windows\SysWOW64\legacy32.dll", @"C:\Tools\bin\tool.exe",
            @"C:\Users\me\AppData\Local\Programs\Go\bin\go.exe");
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate("notepad.exe", probe));
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate("legacy32.dll", probe));
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate("tool.exe", probe));
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate("go", probe));

        // A bare name that cannot be found is not proof of anything.
        Assert.Equal(TargetState.Unknown, RegistryPathTools.Evaluate("nowhere.exe", probe));
    }

    [Fact]
    public void Evaluate_checks_both_program_files_folders_and_both_system_folders()
    {
        var probe = FakeMachine.Probe(@"C:\Program Files (x86)\Vendor\app.exe", @"C:\Program Files\Other\tool.exe",
            @"C:\Windows\SysWOW64\only32.dll", @"C:\Program Files (x86)\Common Files\Vendor\shared.dll");

        // Registered with the 64-bit folder but installed as 32-bit (and the other way round): not missing.
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate(@"C:\Program Files\Vendor\app.exe", probe));
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate(@"%ProgramFiles%\Vendor\app.exe", probe));
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate(@"C:\Program Files (x86)\Other\tool.exe", probe));
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate(@"C:\Windows\System32\only32.dll", probe));
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate(@"C:\Program Files\Common Files\Vendor\shared.dll", probe));
        Assert.Equal(TargetState.Missing, RegistryPathTools.Evaluate(@"C:\Program Files\Gone\gone.exe", probe));
    }

    [Fact]
    public void Wow64Alternatives_map_each_folder_to_its_counterpart()
    {
        Assert.Equal([@"C:\Program Files (x86)\A\a.exe"], RegistryPathTools.Wow64Alternatives(@"C:\Program Files\A\a.exe", FakeMachine.Get));
        Assert.Equal([@"C:\Program Files\A\a.exe"], RegistryPathTools.Wow64Alternatives(@"C:\Program Files (x86)\A\a.exe", FakeMachine.Get));
        Assert.Equal([@"C:\Windows\System32\x.dll"], RegistryPathTools.Wow64Alternatives(@"C:\Windows\Sysnative\x.dll", FakeMachine.Get));
        Assert.Empty(RegistryPathTools.Wow64Alternatives(@"C:\Games\x.exe", FakeMachine.Get));
    }

    [Theory]
    [InlineData(@"C:\Tools\app.exe.FriendlyAppName", @"C:\Tools\app.exe")]
    [InlineData(@"C:\Tools\app.exe.ApplicationCompany", @"C:\Tools\app.exe")]
    [InlineData(@"C:\Old\setup.exe", @"C:\Old\setup.exe")]
    [InlineData("@shell32.dll,-1234", null)]
    [InlineData("LangID", null)]
    public void MuiCacheTarget_strips_the_suffix(string valueName, string? expected)
    {
        Assert.Equal(expected, RegistryPathTools.MuiCacheTarget(valueName));
    }

    [Fact]
    public void Service_image_paths_resolve_system_relative_forms()
    {
        Assert.Equal(@"C:\Windows\system32\drivers\x.sys", RegistryPathTools.ResolveServiceImagePath(@"system32\drivers\x.sys", FakeMachine.Get));
        Assert.Equal(@"C:\Program Files\Vendor\svc.exe", RegistryPathTools.ResolveServiceImagePath("\"C:\\Program Files\\Vendor\\svc.exe\" -service", FakeMachine.Get));
    }

    [Fact]
    public void Msi_product_codes_are_packed_like_windows_installer()
    {
        Assert.Equal("87654321BA09FEDC2143658709BADCFE", RegistryPathTools.PackMsiProductCode("{12345678-90AB-CDEF-1234-567890ABCDEF}"));
        Assert.Null(RegistryPathTools.PackMsiProductCode("WinRAR archiver"));
    }

    [Fact]
    public void Hive_names_and_full_key_names()
    {
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\X", RegistryPathTools.FullKeyName("HKLM", @"\SOFTWARE\X\"));
        Assert.Equal(@"HKEY_CURRENT_USER\Software", RegistryPathTools.FullKeyName("hkey_current_user", "Software"));
        Assert.Equal("HKCR", RegistryPathTools.NormalizeHive("HKEY_CLASSES_ROOT"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Common Files\Vendor\config.xml")]
    [InlineData(@"C:\Program Files\Vendor App\launcher.jar")]
    [InlineData(@"C:\Program Files\Vendor App\Data Files\")]
    [InlineData(@"C:\Program Files\Vendor App\tool --quiet")]
    [InlineData(@"C:\Program Files\My.com Tools\app.exe /min")]
    [InlineData(@"rundll32.exe C:\Program Files\Vendor App\helper.dll")]
    public void ResolveCommandPath_never_reports_existing_targets_behind_unquoted_spaces(string value)
    {
        var probe = FakeMachine.Probe(
            @"C:\Program Files\Common Files\Vendor\config.xml",
            @"C:\Program Files\Vendor App\launcher.jar",
            @"C:\Program Files\Vendor App\Data Files\readme.txt",
            @"C:\Program Files\Vendor App\tool.exe",
            @"C:\Program Files\My.com Tools\app.exe",
            @"C:\Program Files\Vendor App\helper.dll");

        var target = RegistryPathTools.ResolveCommandPath(value, probe);
        Assert.NotEqual(TargetState.Missing, RegistryPathTools.Evaluate(target, probe));
        Assert.NotEqual(@"C:\Program", target);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Gone Vendor\config.xml", @"C:\Program Files\Gone Vendor\config.xml")]
    [InlineData(@"C:\Program Files\Gone Vendor\Data Files\", @"C:\Program Files\Gone Vendor\Data Files\")]
    [InlineData(@"C:\Program Files\Gone Vendor\app.exe --tray", @"C:\Program Files\Gone Vendor\app.exe")]
    [InlineData("\"C:\\Program Files\\Gone Vendor\\app.exe\" \"%1\"", @"C:\Program Files\Gone Vendor\app.exe")]
    public void ResolveCommandPath_reports_whole_paths_when_nothing_exists(string value, string expected)
    {
        // C:\Program Files exists as a folder: an intermediate folder never counts as the program.
        var probe = FakeMachine.Probe(@"C:\Program Files\Other\other.exe");
        var target = RegistryPathTools.ResolveCommandPath(value, probe);
        Assert.Equal(expected, target);
        Assert.Equal(TargetState.Missing, RegistryPathTools.Evaluate(target, probe));
    }

    [Fact]
    public void ResolveCommandPath_leaves_unprovable_values_alone()
    {
        var probe = FakeMachine.Probe();
        Assert.Null(RegistryPathTools.ResolveCommandPath(@"C:\Program Files\Gone\tool --file=%1", probe));
        Assert.Equal(TargetState.Unknown, RegistryPathTools.Evaluate(RegistryPathTools.ResolveCommandPath(@"E:\Portable Apps\x", probe), probe));
        Assert.Null(RegistryPathTools.ResolveCommandPath("  ", probe));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Common Files\Vendor\config.xml", @"C:\Program Files\Common Files\Vendor\config.xml")]
    [InlineData(@"C:\Program Files\Common Files\Vendor\", @"C:\Program Files\Common Files\Vendor\")]
    [InlineData(@"%ProgramFiles%\Vendor App\shared.dll", @"C:\Program Files\Vendor App\shared.dll")]
    [InlineData("relative.dll", null)]
    [InlineData(@"\\server\share\x.dll", null)]
    public void PlainPath_keeps_spaces_and_needs_a_drive(string value, string? expected)
    {
        Assert.Equal(expected, RegistryPathTools.PlainPath(value, FakeMachine.Get));
    }

    [Theory]
    [InlineData("cba", "b", "ca")]
    [InlineData("cba", "B", "ca")]
    [InlineData("cba", "d", "cba")]
    [InlineData("a", "a", "")]
    [InlineData("cba", "MRUList", "cba")]
    public void RemoveFromMruList_drops_only_the_removed_letter(string mru, string entry, string expected)
    {
        Assert.Equal(expected, RegistryIssueRules.RemoveFromMruList(mru, entry));
    }

    [Fact]
    public void Shared_dll_style_names_with_spaces_are_present_when_the_file_or_folder_exists()
    {
        var probe = FakeMachine.Probe(@"C:\Program Files\Common Files\Vendor\config.xml");
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate(RegistryPathTools.PlainPath(@"C:\Program Files\Common Files\Vendor\config.xml", FakeMachine.Get), probe));
        Assert.Equal(TargetState.Present, RegistryPathTools.Evaluate(RegistryPathTools.PlainPath(@"C:\Program Files\Common Files\Vendor\", FakeMachine.Get), probe));
        Assert.Equal(TargetState.Missing, RegistryPathTools.Evaluate(RegistryPathTools.PlainPath(@"C:\Program Files\Common Files\Gone\x.xml", FakeMachine.Get), probe));
    }
}

public class RegExportBuilderTests
{
    [Fact]
    public void Build_writes_the_header_and_key_sections()
    {
        var text = RegExportBuilder.Build([new RegExportKey(@"HKEY_CURRENT_USER\Software\Test", [new RegExportValue(null, RegValueType.String, "default")])]);
        Assert.StartsWith("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Test]\r\n", text, StringComparison.Ordinal);
        Assert.Contains("@=\"default\"\r\n", text, StringComparison.Ordinal);
        Assert.Equal([@"HKEY_CURRENT_USER\Software\Test"], RegExportBuilder.KeyNames(text));
    }

    [Fact]
    public void Strings_and_names_escape_backslashes_and_quotes()
    {
        var line = RegExportBuilder.FormatValue(new RegExportValue("C:\\Apps\\a \"b\".exe", RegValueType.String, "\"C:\\Apps\\a.exe\" \"%1\""));
        Assert.Equal("\"C:\\\\Apps\\\\a \\\"b\\\".exe\"=\"\\\"C:\\\\Apps\\\\a.exe\\\" \\\"%1\\\"\"", line);
    }

    [Fact]
    public void Dword_and_qword_use_regedit_formats()
    {
        Assert.Equal("\"Count\"=dword:0000001a", RegExportBuilder.FormatValue(new RegExportValue("Count", RegValueType.DWord, 26)));
        Assert.Equal("\"Neg\"=dword:ffffffff", RegExportBuilder.FormatValue(new RegExportValue("Neg", RegValueType.DWord, -1)));
        Assert.Equal("\"Big\"=hex(b):1a,00,00,00,01,00,00,00", RegExportBuilder.FormatValue(new RegExportValue("Big", RegValueType.QWord, 0x1_0000_001AL)));
    }

    [Fact]
    public void Expand_and_multi_strings_are_utf16_hex_with_terminators()
    {
        Assert.Equal("\"P\"=hex(2):25,00,41,00,25,00,00,00", RegExportBuilder.FormatValue(new RegExportValue("P", RegValueType.ExpandString, "%A%")));
        Assert.Equal("\"M\"=hex(7):61,00,00,00,62,00,00,00,00,00", RegExportBuilder.FormatValue(new RegExportValue("M", RegValueType.MultiString, new[] { "a", "b" })));
        Assert.Equal("\"E\"=hex(7):00,00", RegExportBuilder.FormatValue(new RegExportValue("E", RegValueType.MultiString, Array.Empty<string>())));
    }

    [Fact]
    public void Binary_and_none_values_and_multiline_strings_use_hex()
    {
        Assert.Equal("\"B\"=hex:01,ab,ff", RegExportBuilder.FormatValue(new RegExportValue("B", RegValueType.Binary, new byte[] { 1, 0xAB, 0xFF })));
        Assert.Equal("\"Empty\"=hex:", RegExportBuilder.FormatValue(new RegExportValue("Empty", RegValueType.Binary, Array.Empty<byte>())));
        Assert.Equal("\"N\"=hex(0):", RegExportBuilder.FormatValue(new RegExportValue("N", RegValueType.None, null)));
        Assert.StartsWith("\"S\"=hex(1):61,00,0a,00", RegExportBuilder.FormatValue(new RegExportValue("S", RegValueType.String, "a\nb")), StringComparison.Ordinal);
    }

    [Fact]
    public void Long_hex_values_wrap_at_80_characters_like_regedit()
    {
        var line = RegExportBuilder.FormatValue(new RegExportValue("Data", RegValueType.Binary, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()));
        var lines = line.Split("\r\n");
        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 80, $"Line too long ({l.Length}): {l}"));
        Assert.All(lines[..^1], l => Assert.EndsWith(",\\", l, StringComparison.Ordinal));
        Assert.All(lines[1..], l => Assert.StartsWith("  ", l, StringComparison.Ordinal));
        var bytes = string.Concat(lines.Select(l => l.TrimEnd('\\').Trim())).Split(':')[1].Split(',');
        Assert.Equal(100, bytes.Length);
    }

    [Fact]
    public void Merge_keeps_one_header_and_every_section()
    {
        string a = "\uFEFFWindows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\A]\r\n\"x\"=\"1\"\r\n\r\n";
        string b = "Windows Registry Editor Version 5.00\n\n[HKEY_LOCAL_MACHINE\\B]\n@=\"2\"\n";
        var merged = RegExportBuilder.Merge([a, "", b]);
        Assert.Equal(1, merged.Split(RegExportBuilder.Header).Length - 1);
        Assert.DoesNotContain('\uFEFF', merged);
        Assert.Equal([@"HKEY_CURRENT_USER\A", @"HKEY_LOCAL_MACHINE\B"], RegExportBuilder.KeyNames(merged));
        Assert.Contains("\"x\"=\"1\"\r\n\r\n[HKEY_LOCAL_MACHINE\\B]\r\n@=\"2\"", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\r", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Merged_text_round_trips_through_utf16()
    {
        var merged = RegExportBuilder.Merge([RegExportBuilder.Build([new RegExportKey(@"HKEY_CURRENT_USER\Ü", [new RegExportValue("ä", RegValueType.String, "ß")])])]);
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(merged)).ToArray();
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        Assert.Equal(merged, reader.ReadToEnd());
    }
}

public class ProtectedRegistryPathsTests
{
    [Theory]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\SomeService")]
    [InlineData("HKLM", @"SAM\SAM\Domains")]
    [InlineData("HKLM", @"SECURITY\Policy")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows Defender\Exclusions")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\app.exe")]
    [InlineData("HKU", @".DEFAULT\Software\Vendor")]
    [InlineData("HKCU", "")]
    public void Sensitive_locations_are_protected(string hive, string key)
    {
        Assert.True(ProtectedRegistryPaths.IsProtected(hive, key));
        Assert.NotNull(ProtectedRegistryPaths.Reason(hive, key));
        Assert.False(ProtectedRegistryPaths.CanDeleteKey(hive, key));
        Assert.False(ProtectedRegistryPaths.CanDeleteValue(hive, key, "x"));
    }

    [Theory]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OldGameLauncher")]
    [InlineData("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{12345678-90AB-CDEF-1234-567890ABCDEF}")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiscordPTB")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\old.exe")]
    [InlineData("HKCU", @"Software\Classes\Applications\old.exe")]
    public void Uninstall_and_app_path_keys_may_be_removed(string hive, string key)
    {
        Assert.False(ProtectedRegistryPaths.IsProtected(hive, key));
        Assert.True(ProtectedRegistryPaths.CanDeleteKey(hive, key));
    }

    [Theory]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData("HKCU", @"Software\Classes\CLSID")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData("HKCR", "CLSID")]
    [InlineData("HKLM", "SOFTWARE")]
    public void Container_keys_themselves_are_never_deleted(string hive, string key)
    {
        Assert.False(ProtectedRegistryPaths.CanDeleteKey(hive, key));
    }

    [Fact]
    public void Values_inside_containers_may_be_removed()
    {
        Assert.True(ProtectedRegistryPaths.CanDeleteValue("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Old Launcher"));
        Assert.True(ProtectedRegistryPaths.CanDeleteValue("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", "Old Font (TrueType)"));
        Assert.False(ProtectedRegistryPaths.CanDeleteValue("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", null));
    }
}

public class RegistryIssueRulesTests
{
    [Fact]
    public void Every_category_has_rules_in_display_order()
    {
        Assert.Equal(Enum.GetValues<RegistryIssueCategory>(), RegistryIssueRules.All.Select(i => i.Category));
        Assert.All(RegistryIssueRules.All, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Name));
            Assert.False(string.IsNullOrWhiteSpace(i.Description));
            Assert.DoesNotContain("error", i.Description, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData(RegistryIssueCategory.UninstallEntries, RiskLevel.Safe, true, false, true)]
    [InlineData(RegistryIssueCategory.AppPaths, RiskLevel.Safe, true, false, true)]
    [InlineData(RegistryIssueCategory.SharedDlls, RiskLevel.Moderate, true, false, false)]
    [InlineData(RegistryIssueCategory.StartupEntries, RiskLevel.Safe, true, false, false)]
    [InlineData(RegistryIssueCategory.ComObjects, RiskLevel.Advanced, true, true, true)]
    [InlineData(RegistryIssueCategory.MuiCache, RiskLevel.Safe, true, false, false)]
    [InlineData(RegistryIssueCategory.Fonts, RiskLevel.Moderate, true, false, false)]
    [InlineData(RegistryIssueCategory.HelpFiles, RiskLevel.Safe, true, false, false)]
    [InlineData(RegistryIssueCategory.InstallerFolders, RiskLevel.Moderate, true, true, false)]
    [InlineData(RegistryIssueCategory.ServicesMissingImage, RiskLevel.Advanced, false, false, false)]
    [InlineData(RegistryIssueCategory.ContextMenuHandlers, RiskLevel.Advanced, true, true, true)]
    public void Category_rules_match_the_policy(RegistryIssueCategory category, RiskLevel risk, bool safeToFix, bool advancedOnly, bool deletesKey)
    {
        var info = RegistryIssueRules.Info(category);
        Assert.Equal(risk, info.Risk);
        Assert.Equal(safeToFix, info.SafeToFix);
        Assert.Equal(advancedOnly, info.AdvancedOnly);
        Assert.Equal(deletesKey, info.FixDeletesKey);
    }

    [Fact]
    public void File_associations_are_advanced_only_in_hklm_and_open_with_entries_delete_a_value()
    {
        var hklm = RegistryIssueRules.Create(RegistryIssueCategory.FileAssociations, "HKLM", @"SOFTWARE\Classes\OldApp.Document", null, "p", @"C:\x.exe");
        var hkcu = RegistryIssueRules.Create(RegistryIssueCategory.FileAssociations, "HKCU", @"Software\Classes\OldApp.Document", null, "p", @"C:\x.exe");
        var openWith = RegistryIssueRules.Create(RegistryIssueCategory.FileAssociations, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mkv\OpenWithList", "c", "p", @"C:\x.exe");
        Assert.True(hklm.AdvancedOnly);
        Assert.False(hkcu.AdvancedOnly);
        Assert.True(hkcu.FixDeletesKey && hkcu.SafeToFix);
        Assert.False(openWith.FixDeletesKey);
        Assert.True(openWith.SafeToFix);
    }

    [Fact]
    public void Services_and_protected_locations_are_never_safe_to_fix()
    {
        var service = RegistryIssueRules.Create(RegistryIssueCategory.ServicesMissingImage, "HKLM", @"SYSTEM\CurrentControlSet\Services\Old", null, "p", @"C:\old.exe");
        var policy = RegistryIssueRules.Create(RegistryIssueCategory.StartupEntries, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", "x", "p", @"C:\old.exe");
        var container = RegistryIssueRules.Create(RegistryIssueCategory.AppPaths, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", null, "p", @"C:\old.exe");
        Assert.False(service.SafeToFix);
        Assert.False(policy.SafeToFix);
        Assert.False(container.SafeToFix);
    }

    [Fact]
    public void Microsoft_components_are_recognised()
    {
        Assert.True(RegistryIssueRules.LooksMicrosoftOwned(@"C:\Windows\System32\x.dll", null, @"C:\Windows"));
        Assert.True(RegistryIssueRules.LooksMicrosoftOwned(@"C:\Program Files\Microsoft Office\root\x.dll", null, @"C:\Windows"));
        Assert.True(RegistryIssueRules.LooksMicrosoftOwned(@"C:\Program Files\WindowsApps\pkg\x.exe", null, @"C:\Windows"));
        Assert.True(RegistryIssueRules.LooksMicrosoftOwned(@"C:\Tools\x.exe", "Microsoft Corporation", @"C:\Windows"));
        Assert.False(RegistryIssueRules.LooksMicrosoftOwned(@"C:\Program Files\Vendor\x.exe", "Vendor Ltd", @"C:\Windows"));
        Assert.True(RegistryIssueRules.AllowsMicrosoftOwnedTargets(RegistryIssueCategory.Fonts));
        Assert.False(RegistryIssueRules.AllowsMicrosoftOwnedTargets(RegistryIssueCategory.ComObjects));
    }

    [Fact]
    public void Ids_are_stable_and_counts_include_empty_categories()
    {
        var a = RegistryIssueRules.Create(RegistryIssueCategory.MuiCache, "HKCU", "Software\\X", "C:\\a.exe", "p", "C:\\a.exe");
        var b = RegistryIssueRules.Create(RegistryIssueCategory.MuiCache, "HKEY_CURRENT_USER", "Software\\X\\", "C:\\a.exe", "p", "C:\\a.exe");
        Assert.Equal(a.Id, b.Id);
        var counts = RegistryIssueRules.CountByCategory([a], [RegistryIssueCategory.MuiCache, RegistryIssueCategory.Fonts]);
        Assert.Equal(1, counts[RegistryIssueCategory.MuiCache]);
        Assert.Equal(0, counts[RegistryIssueCategory.Fonts]);
    }

    [Fact]
    public void Summaries_are_calm_and_grammatical()
    {
        var issues = new[]
        {
            RegistryIssueRules.Create(RegistryIssueCategory.StartupEntries, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "a", "p", null),
            RegistryIssueRules.Create(RegistryIssueCategory.StartupEntries, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "b", "p", null),
            RegistryIssueRules.Create(RegistryIssueCategory.Fonts, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", "f", "p", null),
        };
        Assert.Equal("2 startup entries and 1 font entry", RegistryIssueRules.Summarize(issues));
        Assert.Equal("Removed 1 entry, 2 skipped. A backup was saved first; you can undo this under Backups.",
            RegistryIssueRules.DescribeFix(new RegistryFixResult(1, 2, 0, "registry-backup-20250101-000000", [])));
        var scan = new RegistryScanResult(issues, RegistryIssueRules.CountByCategory(issues, []), 100, TimeSpan.FromSeconds(1), [], DateTimeOffset.Now);
        Assert.Contains("will not make Windows noticeably faster", RegistryIssueRules.DescribeScan(scan), StringComparison.Ordinal);
        Assert.DoesNotContain("problem", RegistryIssueRules.DescribeScan(scan), StringComparison.OrdinalIgnoreCase);
    }
}

public class RegistryBackupCatalogTests
{
    [Theory]
    [InlineData("registry-backup-20251027-143005", true)]
    [InlineData("registry-backup-20251027-143005-2", true)]
    [InlineData("..\\..\\Windows\\System32\\config", false)]
    [InlineData("registry-backup-../../x", false)]
    [InlineData("other-file", false)]
    [InlineData("", false)]
    public void Only_own_backup_ids_are_accepted(string id, bool valid)
    {
        Assert.Equal(valid, RegistryBackupCatalog.IsValidId(id));
    }

    [Fact]
    public void Ids_carry_their_timestamp_and_old_backups_are_found()
    {
        var created = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2025, 1, 2)));
        var id = RegistryBackupCatalog.NewId(created);
        Assert.Equal("registry-backup-20250102-030405", id);
        Assert.Equal(created, RegistryBackupCatalog.CreatedFromId(id));

        var now = created.AddDays(100);
        var backups = new[]
        {
            new RegistryBackup(id, created, "a.reg", 3, "x"),
            new RegistryBackup("registry-backup-new", now.AddDays(-5), "b.reg", 1, "y"),
        };
        Assert.Equal(id, Assert.Single(RegistryBackupCatalog.OlderThan(backups, now)).Id);
    }

    [Fact]
    public void Manifest_round_trips_and_rejects_garbage()
    {
        var issue = RegistryIssueRules.Create(RegistryIssueCategory.AppPaths, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\old.exe", null, "p", @"C:\old.exe");
        var manifest = new RegistryBackupManifest("registry-backup-20250102-030405", DateTimeOffset.Now, 1, "1 App Paths entry", [issue]);
        var back = RegistryBackupCatalog.DeserializeManifest(RegistryBackupCatalog.SerializeManifest(manifest));
        Assert.NotNull(back);
        Assert.Equal(issue, Assert.Single(back!.Issues));
        Assert.Null(RegistryBackupCatalog.DeserializeManifest("{not json"));
        Assert.Null(RegistryBackupCatalog.DeserializeManifest("{\"Id\":\"../evil\",\"IssueCount\":0,\"Summary\":\"\",\"Issues\":[]}"));
    }
}

public class DemoRegistryHealthServiceTests
{
    [Fact]
    public async Task Demo_scan_returns_modest_consistent_results()
    {
        var svc = new DemoRegistryHealthService();
        var standard = RegistryIssueRules.VisibleCategories(advancedMode: false);
        var scan = await svc.ScanAsync(standard, null, CancellationToken.None);

        Assert.Equal(23, scan.Issues.Count);
        Assert.Equal(scan.Issues.Count, scan.CountsByCategory.Values.Sum());
        Assert.All(standard, c => Assert.True(scan.CountsByCategory.ContainsKey(c)));
        Assert.Equal(6, scan.CountsByCategory[RegistryIssueCategory.UninstallEntries]);
        Assert.Equal(2, scan.Issues.Count(i => !i.SafeToFix));
        Assert.DoesNotContain(scan.Issues, i => i.AdvancedOnly);
        Assert.True(scan.KeysScanned > 1000);
        Assert.Equal(scan.Issues.Count, scan.Issues.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task Demo_fix_creates_a_backup_and_restore_brings_entries_back()
    {
        var svc = new DemoRegistryHealthService();
        var categories = new[] { RegistryIssueCategory.MuiCache, RegistryIssueCategory.ServicesMissingImage };
        var scan = await svc.ScanAsync(categories, null, CancellationToken.None);
        var before = (await svc.ListBackupsAsync(CancellationToken.None)).Count;

        var result = await svc.FixAsync(scan.Issues, null, CancellationToken.None);
        Assert.Equal(5, result.Fixed);
        Assert.Equal(2, result.Skipped);
        Assert.NotNull(result.BackupId);
        Assert.Equal(before + 1, (await svc.ListBackupsAsync(CancellationToken.None)).Count);

        var after = await svc.ScanAsync(categories, null, CancellationToken.None);
        Assert.Equal(2, after.Issues.Count);

        Assert.True((await svc.RestoreBackupAsync(result.BackupId!, CancellationToken.None)).Success);
        Assert.Equal(7, (await svc.ScanAsync(categories, null, CancellationToken.None)).Issues.Count);
        Assert.True((await svc.DeleteBackupAsync(result.BackupId!, CancellationToken.None)).Success);
        Assert.Equal(before, (await svc.ListBackupsAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Demo_offers_an_old_backup_for_cleanup()
    {
        var backups = await new DemoRegistryHealthService().ListBackupsAsync(CancellationToken.None);
        Assert.Single(RegistryBackupCatalog.OlderThan(backups, DateTimeOffset.Now));
    }
}
