using PcChecker.Core.Common;
using PcChecker.Core.Software;
using Xunit;

namespace PcChecker.Core.Tests;

public class BundleDetectorTests
{
    private static readonly DateTime T0 = new(2025, 3, 1, 10, 0, 0);

    [Fact]
    public void Nested_install_location_groups_child_under_parent()
    {
        var parent = InstalledProgram.Create("p", "Big Suite", "Contoso", installLocation: @"C:\Program Files\BigSuite");
        var child = InstalledProgram.Create("c", "Big Suite Helper", "Contoso", installLocation: @"C:\Program Files\BigSuite\Helper\");
        var unrelated = InstalledProgram.Create("u", "Other", "Fabrikam", installLocation: @"C:\Program Files\Other");

        var groups = BundleDetector.Detect([parent, child, unrelated]);
        var g = Assert.Single(groups);
        Assert.Equal("p", g.Parent.Id);
        Assert.Equal("c", Assert.Single(g.Children).Id);
        Assert.Contains("folder", g.Reason);
    }

    [Fact]
    public void Same_time_different_publisher_groups()
    {
        var a = InstalledProgram.Create("a", "Photo Editor", "Fabrikam", T0);
        var b = InstalledProgram.Create("b", "McAfee WebAdvisor", "McAfee", T0.AddMinutes(1));
        var groups = BundleDetector.Detect([a, b]);
        var g = Assert.Single(groups);
        Assert.Equal("a", g.Parent.Id);
        Assert.Equal("b", g.Children[0].Id);
        Assert.Contains("known bundled extra", g.Reason);
    }

    [Fact]
    public void Date_only_install_dates_on_the_same_day_are_not_a_bundle()
    {
        // The registry's InstallDate is yyyyMMdd: every program installed on the same day would otherwise pair up.
        var steam = InstalledProgram.Create("a", "Steam", "Valve", new DateTime(2025, 3, 1));
        var vlc = InstalledProgram.Create("b", "VLC", "VideoLAN", new DateTime(2025, 3, 1));
        Assert.Empty(BundleDetector.Detect([steam, vlc]));
        Assert.False(BundleDetector.HasInstallTime(steam));
        Assert.True(BundleDetector.HasInstallTime(InstalledProgram.Create("c", "X", "Y", T0)));

        // A known bundled extra still needs a real time to be matched by time.
        var extra = InstalledProgram.Create("x", "McAfee WebAdvisor", "McAfee", new DateTime(2025, 3, 1));
        Assert.Empty(BundleDetector.Detect([steam, extra]));
    }

    [Fact]
    public void Known_extra_becomes_child_even_when_installed_first()
    {
        var extra = InstalledProgram.Create("x", "Avast Secure Browser", "Avast", T0);
        var app = InstalledProgram.Create("y", "Video Tool", "Contoso", T0.AddMinutes(2));
        var g = Assert.Single(BundleDetector.Detect([extra, app]));
        Assert.Equal("y", g.Parent.Id);
        Assert.Equal("x", g.Children[0].Id);
    }

    [Fact]
    public void Same_publisher_or_far_apart_do_not_group()
    {
        var a = InstalledProgram.Create("a", "Tool A", "Contoso", T0);
        var b = InstalledProgram.Create("b", "Tool B", "Contoso", T0.AddMinutes(1));
        var c = InstalledProgram.Create("c", "Tool C", "Fabrikam", T0.AddHours(3));
        Assert.Empty(BundleDetector.Detect([a, b, c]));
    }

    [Fact]
    public void Runtime_dependencies_are_labelled_not_bloat()
    {
        var game = InstalledProgram.Create("g", "Space Game", "Studio", T0);
        var vc = InstalledProgram.Create("v", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Corporation", T0.AddSeconds(30));
        var g = Assert.Single(BundleDetector.Detect([game, vc]));
        Assert.Contains("runtime dependency", g.Reason);
        Assert.True(BundleDetector.IsRuntimeDependency(vc));
        Assert.False(BundleDetector.IsKnownBundledExtra(vc));
    }

    [Fact]
    public void Store_apps_without_folders_are_ignored()
    {
        var store = InstalledProgram.Create("s", "Store App", "Contoso", T0, source: SoftwareSource.StoreApp);
        var other = InstalledProgram.Create("o", "Other", "Fabrikam", T0);
        Assert.Empty(BundleDetector.Detect([store, other]));
    }

    [Fact]
    public void Empty_input_gives_empty_output()
    {
        Assert.Empty(BundleDetector.Detect([]));
    }
}

public class UninstallCommandBuilderTests
{
    [Fact]
    public void Msi_product_code_builds_msiexec()
    {
        var p = InstalledProgram.Create("{12345678-1234-1234-1234-123456789ABC}", "Msi App",
            uninstallString: "MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}", installer: InstallerKind.Msi);
        var quiet = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal("msiexec.exe", quiet.FileName);
        Assert.Equal("/x {12345678-1234-1234-1234-123456789ABC} /norestart /qn", quiet.Arguments);
        var loud = UninstallCommandBuilder.Build(p, quiet: false)!.Value;
        Assert.DoesNotContain("/qn", loud.Arguments);
    }

    [Fact]
    public void Inno_setup_gets_very_silent_flags()
    {
        var p = InstalledProgram.Create("i", "Inno App", uninstallString: "\"C:\\Program Files\\Inno App\\unins000.exe\"", installer: InstallerKind.InnoSetup);
        var (file, args) = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal(@"C:\Program Files\Inno App\unins000.exe", file);
        Assert.Contains("/VERYSILENT", args);
        Assert.Contains("/NORESTART", args);
    }

    [Fact]
    public void Nsis_and_installshield_flags()
    {
        var nsis = InstalledProgram.Create("n", "Nsis App", uninstallString: "C:\\Apps\\Nsis\\uninstall.exe");
        var (nsisFile, nsisArgs) = UninstallCommandBuilder.Build(nsis, quiet: true)!.Value;
        Assert.Equal(@"C:\Apps\Nsis\uninstall.exe", nsisFile);
        Assert.Equal("/S", nsisArgs);

        var shield = InstalledProgram.Create("s", "Shield App", uninstallString: "\"C:\\Apps\\setup.exe\" -runfromtemp -removeonly", installer: InstallerKind.InstallShield);
        var (_, args) = UninstallCommandBuilder.Build(shield, quiet: true)!.Value;
        Assert.Contains("/s /v\"/qn\"", args);
        Assert.Contains("-removeonly", args);
    }

    [Fact]
    public void Quiet_uninstall_string_is_preferred_when_present()
    {
        var p = InstalledProgram.Create("q", "Quiet App", uninstallString: "\"C:\\A\\u.exe\"", quietUninstallString: "\"C:\\A\\u.exe\" /silent");
        var (file, args) = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal(@"C:\A\u.exe", file);
        Assert.Equal("/silent /norestart", args);
        // quiet:false must ignore the quiet string.
        Assert.Equal((@"C:\A\u.exe", string.Empty), UninstallCommandBuilder.Build(p, quiet: false)!.Value);
    }

    [Fact]
    public void Quiet_msiexec_strings_get_norestart_so_an_uninstall_never_reboots_unattended()
    {
        var p = InstalledProgram.Create("q", "App", uninstallString: "MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC}",
            quietUninstallString: "MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC} /qn");
        var (file, args) = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal("MsiExec.exe", file);
        Assert.Equal("/X{12345678-1234-1234-1234-123456789ABC} /qn /norestart", args);

        var already = InstalledProgram.Create("r", "App", uninstallString: "x", quietUninstallString: "MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC} /quiet /norestart");
        Assert.Equal("/X{12345678-1234-1234-1234-123456789ABC} /quiet /norestart", UninstallCommandBuilder.Build(already, quiet: true)!.Value.Arguments);
        var property = InstalledProgram.Create("s", "App", uninstallString: "x", quietUninstallString: "MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC} /qn REBOOT=ReallySuppress");
        Assert.DoesNotContain("/norestart", UninstallCommandBuilder.Build(property, quiet: true)!.Value.Arguments);
        // Not msiexec: untouched.
        Assert.Equal("/S", UninstallCommandBuilder.EnsureNoRestart(@"C:\x\uninstall.exe", "/S"));
    }

    [Fact]
    public void Quiet_bundle_strings_get_norestart_so_a_burn_bundle_never_reboots_unattended()
    {
        var python = InstalledProgram.Create("b", "Python 3.12.4 (64-bit)",
            uninstallString: "\"C:\\ProgramData\\Package Cache\\{12345678-1234-1234-1234-123456789ABC}\\python-3.12.4-amd64.exe\" /uninstall",
            quietUninstallString: "\"C:\\ProgramData\\Package Cache\\{12345678-1234-1234-1234-123456789ABC}\\python-3.12.4-amd64.exe\" /uninstall /quiet");
        var (file, args) = UninstallCommandBuilder.Build(python, quiet: true)!.Value;
        Assert.Equal(@"C:\ProgramData\Package Cache\{12345678-1234-1234-1234-123456789ABC}\python-3.12.4-amd64.exe", file);
        Assert.Equal("/uninstall /quiet /norestart", args);

        Assert.Equal("/uninstall /passive /norestart", UninstallCommandBuilder.EnsureNoRestart(@"C:\x\vc_redist.x64.exe", "/uninstall /passive"));
        Assert.Equal("/uninstall /q /norestart", UninstallCommandBuilder.EnsureNoRestart(@"C:\x\bundle.exe", "/uninstall /q"));
        Assert.Equal("/uninstall /quiet /norestart", UninstallCommandBuilder.EnsureNoRestart(@"C:\x\bundle.exe", "/uninstall /quiet /norestart"));
        Assert.Equal("/uninstall /quiet /forcerestart", UninstallCommandBuilder.EnsureNoRestart(@"C:\x\bundle.exe", "/uninstall /quiet /forcerestart"));
        // Not quiet, or the quiet switch belongs to msiexec inside an InstallShield /v"..." pass-through: untouched.
        Assert.Equal("/uninstall", UninstallCommandBuilder.EnsureNoRestart(@"C:\x\bundle.exe", "/uninstall"));
        Assert.Equal("-runfromtemp /s /v\"/qn\"", UninstallCommandBuilder.EnsureNoRestart(@"C:\x\setup.exe", "-runfromtemp /s /v\"/qn\""));
    }

    [Fact]
    public void Squirrel_quiet_uninstall_adds_the_silent_switch()
    {
        var discord = InstalledProgram.Create("d", "Discord", uninstallString: "C:\\Users\\me\\AppData\\Local\\Discord\\Update.exe --uninstall");
        var (file, args) = UninstallCommandBuilder.Build(discord, quiet: true)!.Value;
        Assert.Equal(@"C:\Users\me\AppData\Local\Discord\Update.exe", file);
        Assert.Equal("--uninstall -s", args);
        Assert.Equal("--uninstall", UninstallCommandBuilder.Build(discord, quiet: false)!.Value.Arguments);
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\unins000.exe /SILENT", InstallerKind.Unknown, true, @"C:\Program Files\App\unins000.exe", "/SILENT /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData(@"C:\Apps\Nsis\uninstall.exe", InstallerKind.Unknown, true, @"C:\Apps\Nsis\uninstall.exe", "/S")]
    [InlineData("\"C:\\Apps\\setup.exe\" -runfromtemp /s", InstallerKind.InstallShield, true, @"C:\Apps\setup.exe", "-runfromtemp /s /v\"/qn\"")]
    [InlineData(@"C:\Apps\tool.exe.d\run.exe /x", InstallerKind.Unknown, false, @"C:\Apps\tool.exe.d\run.exe", "/x")]
    [InlineData("\"C:\\Apps\\tool.exe.d\\run.exe\" /x", InstallerKind.Unknown, false, @"C:\Apps\tool.exe.d\run.exe", "/x")]
    public void Builder_handles_the_shapes_real_registries_produce(string uninstallString, InstallerKind kind, bool quiet, string file, string args)
    {
        var p = InstalledProgram.Create("p", "App", uninstallString: uninstallString, installer: kind);
        var (builtFile, builtArgs) = UninstallCommandBuilder.Build(p, quiet)!.Value;
        Assert.Equal(file, builtFile);
        Assert.Equal(args, builtArgs);
    }

    [Fact]
    public void Msi_product_without_uninstall_string_uses_the_registry_key_code()
    {
        var p = InstalledProgram.Create("{12345678-1234-1234-1234-123456789ABC}", "Msi App") with { RegistryKey = @"HKLM\...\{12345678-1234-1234-1234-123456789ABC}" };
        var (file, args) = UninstallCommandBuilder.Build(p, quiet: false)!.Value;
        Assert.Equal("msiexec.exe", file);
        Assert.Equal("/x {12345678-1234-1234-1234-123456789ABC} /norestart", args);
    }

    [Fact]
    public void Store_package_names_with_apostrophes_are_escaped()
    {
        var p = InstalledProgram.Create("st", "O'Store", source: SoftwareSource.StoreApp, installer: InstallerKind.StoreApp, packageFullName: "Contoso.O'App_1.0.0.0_x64__abc123");
        var (file, args) = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal("powershell.exe", file);
        Assert.Contains("-Package 'Contoso.O''App_1.0.0.0_x64__abc123'", args);
    }

    [Fact]
    public void Store_apps_use_remove_appxpackage()
    {
        var p = InstalledProgram.Create("st", "Store App", source: SoftwareSource.StoreApp, installer: InstallerKind.StoreApp,
            packageFullName: "Contoso.App_1.0.0.0_x64__abc123");
        var (file, args) = UninstallCommandBuilder.Build(p, quiet: true)!.Value;
        Assert.Equal("powershell.exe", file);
        Assert.Contains("Remove-AppxPackage -Package 'Contoso.App_1.0.0.0_x64__abc123'", args);
    }

    [Fact]
    public void Missing_information_returns_null()
    {
        Assert.Null(UninstallCommandBuilder.Build(InstalledProgram.Create("x", "No uninstall"), quiet: true));
        Assert.Null(UninstallCommandBuilder.Build(InstalledProgram.Create("s", "Store no pkg", source: SoftwareSource.StoreApp), quiet: true));
    }

    [Fact]
    public void SplitCommand_handles_quoted_and_unquoted()
    {
        Assert.Equal((@"C:\x y\u.exe", "/a"), UninstallCommandBuilder.SplitCommand("\"C:\\x y\\u.exe\" /a"));
        Assert.Equal((@"C:\x\u.exe", "/b /c"), UninstallCommandBuilder.SplitCommand(@"C:\x\u.exe /b /c"));
        Assert.Equal(("rundll32", "foo"), UninstallCommandBuilder.SplitCommand("rundll32 foo"));
        Assert.Null(UninstallCommandBuilder.SplitCommand(" "));
    }

    [Theory]
    [InlineData("MsiExec.exe /X{...}", InstallerKind.Msi)]
    [InlineData("\"C:\\P\\unins000.exe\"", InstallerKind.InnoSetup)]
    [InlineData("C:\\P\\uninstall.exe", InstallerKind.Nsis)]
    [InlineData("C:\\Users\\me\\AppData\\Local\\App\\Update.exe --uninstall", InstallerKind.Squirrel)]
    [InlineData("C:\\P\\setup.exe -runfromtemp", InstallerKind.InstallShield)]
    [InlineData("C:\\P\\remove.exe", InstallerKind.Unknown)]
    public void GuessInstaller_recognises_common_tools(string command, InstallerKind expected)
    {
        Assert.Equal(expected, UninstallCommandBuilder.GuessInstaller(command));
    }
}

public class ProtectedSoftwareRulesTests
{
    [Fact]
    public void Edge_and_defender_are_critical()
    {
        var edge = ProtectedSoftwareRules.Evaluate(InstalledProgram.Create("e", "Microsoft Edge"));
        Assert.True(edge.IsProtected);
        Assert.True(edge.Critical);
        Assert.False(ProtectedSoftwareRules.CanUninstall(InstalledProgram.Create("e", "Microsoft Edge"), advancedMode: true).Allowed);
    }

    [Fact]
    public void Runtimes_can_be_removed_in_advanced_mode_only()
    {
        var vc = InstalledProgram.Create("v", "Microsoft Visual C++ 2019 Redistributable");
        Assert.False(ProtectedSoftwareRules.CanUninstall(vc, advancedMode: false).Allowed);
        Assert.True(ProtectedSoftwareRules.CanUninstall(vc, advancedMode: true).Allowed);
    }

    [Fact]
    public void Ordinary_programs_are_not_protected()
    {
        var p = ProtectedSoftwareRules.Apply(InstalledProgram.Create("p", "Notepad++"));
        Assert.False(p.IsProtected);
        Assert.Null(p.ProtectionReason);
        Assert.True(ProtectedSoftwareRules.CanUninstall(p, false).Allowed);
    }

    [Fact]
    public void Store_framework_packages_are_protected()
    {
        var p = InstalledProgram.Create("s", "VCLibs", source: SoftwareSource.StoreApp, packageFullName: "Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe");
        Assert.True(ProtectedSoftwareRules.Evaluate(p).IsProtected);
    }
}

public class InstallerKindDetectorTests
{
    [Theory]
    [InlineData("MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC}", null, false, InstallerKind.Msi)]
    [InlineData("\"C:\\Apps\\Thing\\unins000.exe\"", null, false, InstallerKind.InnoSetup)]
    [InlineData("\"C:\\Apps\\Thing\\unins001.exe\" /SILENT", null, false, InstallerKind.InnoSetup)]
    [InlineData("\"C:\\Program Files\\InstallShield Installation Information\\{ABC}\\setup.exe\" -runfromtemp -l0x0409", null, false, InstallerKind.InstallShield)]
    [InlineData("C:\\Users\\me\\AppData\\Local\\App\\Update.exe --uninstall", null, false, InstallerKind.Squirrel)]
    [InlineData("C:\\Apps\\Thing\\uninstall.exe", null, false, InstallerKind.Nsis)]
    [InlineData("\"C:\\Apps\\Thing\\remove.exe\" --quiet", null, false, InstallerKind.Unknown)]
    public void Detects_from_strings(string uninstall, string? quiet, bool windowsInstaller, InstallerKind expected)
    {
        Assert.Equal(expected, InstallerKindDetector.Detect(uninstall, quiet, windowsInstaller));
    }

    [Fact]
    public void WindowsInstaller_flag_wins_even_without_msiexec()
    {
        Assert.Equal(InstallerKind.Msi, InstallerKindDetector.Detect("\"C:\\Apps\\setup.exe\" /remove", null, windowsInstallerFlag: true));
    }

    [Fact]
    public void Uninstaller_header_confirms_or_rejects_nsis()
    {
        var nsis = System.Text.Encoding.ASCII.GetBytes("MZ....garbage....Nullsoft Install System v3.09....");
        var other = System.Text.Encoding.ASCII.GetBytes("MZ....garbage....Some other installer....");
        Assert.Equal(InstallerKind.Nsis, InstallerKindDetector.Detect("C:\\Apps\\uninstall.exe", null, false, nsis));
        Assert.Equal(InstallerKind.Unknown, InstallerKindDetector.Detect("C:\\Apps\\uninstall.exe", null, false, other));
        Assert.True(InstallerKindDetector.LooksLikeNsis(nsis));
        Assert.False(InstallerKindDetector.LooksLikeNsis(other));
    }
}

public class InstallDateParserTests
{
    [Theory]
    [InlineData("20250301", 2025, 3, 1)]
    [InlineData("2025-03-01", 2025, 3, 1)]
    [InlineData("2025/03/01", 2025, 3, 1)]
    [InlineData("20250301143000", 2025, 3, 1)]
    [InlineData(" 20250301 ", 2025, 3, 1)]
    public void Parses_common_formats(string value, int y, int m, int d)
    {
        var parsed = InstallDateParser.Parse(value);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(y, m, d), parsed!.Value.Date);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("20251399")]
    [InlineData("19800101")]
    public void Rejects_missing_or_invalid(string? value)
    {
        Assert.Null(InstallDateParser.Parse(value));
    }
}

public class AppxNameHumanizerTests
{
    [Theory]
    [InlineData("Microsoft.WindowsCalculator", "Calculator")]
    [InlineData("Microsoft.549981C3F5F10", "Cortana")]
    [InlineData("Contoso.PhotoStudioPro", "Photo Studio Pro")]
    [InlineData("Fabrikam.MyApp2024", "My App 2024")]
    [InlineData("SpotifyAB.SpotifyMusic", "Spotify")]
    public void Humanizes_package_names(string package, string expected)
    {
        Assert.Equal(expected, AppxNameHumanizer.Humanize(package));
    }

    [Fact]
    public void Resource_display_names_fall_back_to_humanized_name()
    {
        Assert.Equal("Photo Studio", AppxNameHumanizer.ResolveDisplayName("ms-resource:AppName", "Contoso.PhotoStudio"));
        Assert.Equal("Real Name", AppxNameHumanizer.ResolveDisplayName("Real Name", "Contoso.PhotoStudio"));
    }

    [Fact]
    public void Publisher_common_name_is_extracted()
    {
        Assert.Equal("Microsoft Corporation", AppxNameHumanizer.ParsePublisherCommonName("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
        Assert.Equal("Contoso", AppxNameHumanizer.ParsePublisherCommonName("Contoso"));
        Assert.Null(AppxNameHumanizer.ParsePublisherCommonName("  "));
    }
}

public class UninstallExitCodesTests
{
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1641, true, true)]
    [InlineData(1605, true, false)]
    [InlineData(1602, false, false)]
    [InlineData(1603, false, false)]
    [InlineData(1618, false, false)]
    [InlineData(42, false, false)]
    public void Maps_exit_codes(int code, bool success, bool reboot)
    {
        var r = UninstallExitCodes.Map(code, "Thing");
        Assert.Equal(success, r.Success);
        Assert.Equal(reboot, r.RebootRequired);
        Assert.Equal(success, UninstallExitCodes.IsSuccess(code));
        Assert.Contains(code == 1618 ? "Another installation" : "Thing", r.Message);
    }
}

public class WingetListParserTests
{
    private const string Sample =
        "   -\r\n" +
        "Name                     Id                    Version    Available  Source\r\n" +
        "-----------------------------------------------------------------------------\r\n" +
        "Discord                  Discord.Discord       1.0.9170   1.0.9175   winget\r\n" +
        "7-Zip 24.08 (x64)        7zip.7zip             24.08                 winget\r\n" +
        "Some Unknown Tool        ARP\\Machine\\X64\\Tool  2.0\r\n" +
        "2 upgrades available.\r\n";

    [Fact]
    public void Parses_fixed_width_columns_from_header()
    {
        var rows = WingetListParser.Parse(Sample);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Discord", rows[0].Name);
        Assert.Equal("Discord.Discord", rows[0].Id);
        Assert.Equal("1.0.9170", rows[0].Version);
        Assert.Equal("1.0.9175", rows[0].Available);
        Assert.Equal("winget", rows[0].Source);
        Assert.Null(rows[1].Available);
        Assert.Equal("Some Unknown Tool", rows[2].Name);
        Assert.Null(rows[2].Source);
    }

    [Fact]
    public void UpgradesOnly_keeps_rows_with_newer_versions()
    {
        var upgrades = WingetListParser.UpgradesOnly(WingetListParser.Parse(Sample));
        var only = Assert.Single(upgrades);
        Assert.Equal("Discord.Discord", only.Id);
    }

    [Fact]
    public void Output_without_header_is_empty()
    {
        Assert.Empty(WingetListParser.Parse("No installed package found matching input criteria."));
        Assert.Empty(WingetListParser.Parse(null));
        Assert.Null(WingetListParser.TryParse("No installed package found matching input criteria."));
    }

    private const string GermanSample =
        "   -\r   \\\r                                                    \r" +
        "Name                     ID                    Version    Verfügbar  Quelle\r\n" +
        "-----------------------------------------------------------------------------\r\n" +
        "Discord                  Discord.Discord       1.0.9170   1.0.9175   winget\r\n" +
        "Mozilla Firefox (x64 de) Mozilla.Firefox       128.0      129.0      winget\r\n" +
        "7-Zip 24.08 (x64)        7zip.7zip             24.08                 winget\r\n" +
        "2 Aktualisierungen verfügbar.\r\n" +
        "\r\n" +
        "Die folgenden Pakete haben ein verfügbares Upgrade, erfordern jedoch eine explizite Zielbestimmung für das Upgrade:\r\n" +
        "Name                     ID                    Version    Verfügbar  Quelle\r\n" +
        "-----------------------------------------------------------------------------\r\n" +
        "Pinned App               Pinned.App            1.0        2.0        winget\r\n";

    [Fact]
    public void Translated_header_is_read_from_the_line_above_the_separator()
    {
        var rows = WingetListParser.Parse(GermanSample);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Discord", rows[0].Name);
        Assert.Equal("Discord.Discord", rows[0].Id);
        Assert.Equal("1.0.9170", rows[0].Version);
        Assert.Equal("1.0.9175", rows[0].Available);
        Assert.Equal("winget", rows[0].Source);
        Assert.Equal("Mozilla Firefox (x64 de)", rows[1].Name);
        Assert.Null(rows[2].Available);

        var upgrades = WingetListParser.UpgradesOnly(rows);
        Assert.Equal(["Discord.Discord", "Mozilla.Firefox"], upgrades.Select(u => u.Id));
    }

    [Fact]
    public void Unreadable_output_is_not_reported_as_up_to_date()
    {
        var unknown = WingetUpgradeChecker.Interpret("Es wurde kein installiertes Paket gefunden, das den Eingabekriterien entspricht.");
        Assert.Empty(unknown.Upgrades);
        Assert.DoesNotContain("up to date", unknown.Note);

        Assert.Contains("up to date", WingetUpgradeChecker.Interpret("No installed package found matching input criteria.").Note);
        var german = WingetUpgradeChecker.Interpret(GermanSample);
        Assert.Equal(2, german.Upgrades.Count);
        Assert.Null(german.Note);
    }
}

public class InstallLocationInferenceTests
{
    [Fact]
    public void Explicit_location_is_normalized_and_preferred()
    {
        Assert.Equal(@"C:\Apps\Thing", InstallLocationInference.Infer(@"""C:\Apps\Thing\""", @"C:\Other\x.exe,0", null));
    }

    [Fact]
    public void Falls_back_to_icon_then_uninstall_string()
    {
        Assert.Equal(@"C:\Apps\Thing", InstallLocationInference.Infer(null, @"C:\Apps\Thing\thing.exe,0", null));
        Assert.Equal(@"C:\Apps\Thing", InstallLocationInference.Infer(null, null, @"""C:\Apps\Thing\uninstall.exe"" /S"));
    }

    [Theory]
    [InlineData("MsiExec.exe /X{ABC}")]
    [InlineData(@"C:\Windows\System32\rundll32.exe advpack.dll,LaunchINFSection")]
    [InlineData(@"C:\Windows\Temp\SetupChipset.exe -uninstall")]
    [InlineData(@"C:\ProgramData\Package Cache\{guid}\setup.exe /uninstall")]
    public void Generic_hosts_and_temp_folders_give_no_location(string command)
    {
        Assert.Null(InstallLocationInference.Infer(null, null, command));
    }

    [Fact]
    public void Icon_index_is_stripped()
    {
        Assert.Equal(@"C:\Apps\a.exe", InstallLocationInference.StripIconIndex(@"""C:\Apps\a.exe"",-3"));
        Assert.Equal(@"C:\Apps\a.ico", InstallLocationInference.StripIconIndex(@"C:\Apps\a.ico"));
        Assert.Null(InstallLocationInference.StripIconIndex(" "));
    }

    [Theory]
    [InlineData(@"C:\Program Files\setup.exe")]
    [InlineData(@"C:\Program Files (x86)\uninstall.exe /S")]
    [InlineData(@"C:\ProgramData\vendor.exe")]
    [InlineData(@"C:\tool.exe")]
    [InlineData(@"C:\Users\jane\AppData\Local\installer.exe")]
    [InlineData(@"C:\Users\jane\AppData\Local\Programs\stub.exe")]
    [InlineData(@"C:\Users\jane\remove.exe")]
    public void Executables_directly_in_shared_root_folders_give_no_location(string command)
    {
        Assert.Null(InstallLocationInference.Infer(null, command + ",0", null));
        Assert.Null(InstallLocationInference.Infer(null, null, command));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:", true)]
    [InlineData(@"C:\Program Files", true)]
    [InlineData(@"C:\Program Files (x86)\", true)]
    [InlineData(@"C:\Program Files\Common Files", true)]
    [InlineData(@"C:\ProgramData", true)]
    [InlineData(@"C:\Users\jane", true)]
    [InlineData(@"C:\Users\jane\AppData\Roaming", true)]
    [InlineData(@"C:\Users\jane\AppData\Local\Programs", true)]
    [InlineData(@"C:\Program Files\NVIDIA Corporation", false)]
    [InlineData(@"C:\Users\jane\AppData\Local\Programs\Signal", false)]
    [InlineData(@"C:\Users\jane\AppData\Local\Discord", false)]
    [InlineData(@"D:\Games\Steam", false)]
    public void Well_known_root_folders_are_recognised(string dir, bool isRoot)
    {
        Assert.Equal(isRoot, InstallLocationInference.IsWellKnownRoot(dir));
    }
}

public class UninstallCompletionWaiterTests
{
    [Fact]
    public async Task Returns_at_once_when_entry_is_already_gone()
    {
        int probes = 0;
        var gone = await UninstallCompletionWaiter.WaitUntilGoneAsync(() => { probes++; return false; }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.True(gone);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task Keeps_polling_until_the_entry_disappears()
    {
        int probes = 0;
        var gone = await UninstallCompletionWaiter.WaitUntilGoneAsync(() => ++probes < 4, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.True(gone);
        Assert.Equal(4, probes);
    }

    [Fact]
    public async Task Gives_up_after_the_grace_period()
    {
        int probes = 0;
        var gone = await UninstallCompletionWaiter.WaitUntilGoneAsync(() => { probes++; return true; }, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.False(gone);
        Assert.True(probes >= 2);
    }

    [Fact]
    public async Task Probe_failure_counts_as_gone_and_cancellation_propagates()
    {
        Assert.True(await UninstallCompletionWaiter.WaitUntilGoneAsync(() => throw new InvalidOperationException(), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UninstallCompletionWaiter.WaitUntilGoneAsync(() => true, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), cts.Token));
    }

    [Fact]
    public void Nsis_gets_the_longest_grace_period()
    {
        Assert.True(UninstallCompletionWaiter.GraceFor(InstallerKind.Nsis, true) > UninstallCompletionWaiter.GraceFor(InstallerKind.Msi, true));
        Assert.True(UninstallCompletionWaiter.GraceFor(InstallerKind.Msi, true) >= UninstallCompletionWaiter.GraceFor(InstallerKind.Msi, false));
    }
}

public class UninstallEntryMapperTests
{
    private static UninstallEntry Entry(SoftwareSource source, Dictionary<string, object?> values, DateTime? lastWrite = null) =>
        new("{KEY}", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{KEY}", source, lastWrite, values);

    [Fact]
    public void Maps_typical_msi_entry()
    {
        var p = UninstallEntryMapper.Map(Entry(SoftwareSource.Machine64, new()
        {
            ["DisplayName"] = "Contoso Suite",
            ["DisplayVersion"] = "2.1",
            ["Publisher"] = "Contoso",
            ["InstallDate"] = "20250301",
            ["InstallLocation"] = @"C:\Program Files\Contoso\",
            ["UninstallString"] = "MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC}",
            ["EstimatedSize"] = 2048,
            ["WindowsInstaller"] = 1,
            ["URLInfoAbout"] = "https://contoso.example",
        }));

        Assert.NotNull(p);
        Assert.Equal("m64:{KEY}", p!.Id);
        Assert.Equal("Contoso Suite", p.Name);
        Assert.Equal(InstallerKind.Msi, p.Installer);
        Assert.Equal(new DateTime(2025, 3, 1), p.InstallDate);
        Assert.Equal(@"C:\Program Files\Contoso", p.InstallLocation);
        Assert.Equal(2048L * 1024, p.SizeBytes);
        Assert.Equal(SoftwareSource.Machine64, p.Source);
        Assert.Equal("https://contoso.example", p.AboutUrl);
        Assert.EndsWith(@"\{KEY}", p.RegistryKey);
    }

    [Fact]
    public void Nameless_entries_are_skipped_unless_requested()
    {
        var entry = Entry(SoftwareSource.Machine32, new() { ["UninstallString"] = @"C:\x\uninstall.exe" });
        Assert.Null(UninstallEntryMapper.Map(entry));
        var kept = UninstallEntryMapper.Map(entry, includeNameless: true);
        Assert.NotNull(kept);
        Assert.Equal("{KEY}", kept!.Name);
    }

    [Fact]
    public void Windows_updates_and_hotfixes_are_skipped()
    {
        Assert.Null(UninstallEntryMapper.Map(Entry(SoftwareSource.Machine64, new()
        {
            ["DisplayName"] = "Update for Microsoft Office 2016 (KB5002438)",
            ["ParentKeyName"] = "Office16.PROPLUS",
            ["ReleaseType"] = "Update",
        })));
        Assert.True(UninstallEntryMapper.IsWindowsUpdate("KB5034441", null, null));
        Assert.True(UninstallEntryMapper.IsWindowsUpdate("Security Update for Windows (KB123)", null, null));
        Assert.False(UninstallEntryMapper.IsWindowsUpdate("KBar Studio", null, null));
        Assert.False(UninstallEntryMapper.IsWindowsUpdate("Contoso", "Parent", "Product"));
    }

    [Fact]
    public void Missing_install_date_uses_key_timestamp_and_system_component_flag_changes_source()
    {
        var stamp = new DateTime(2024, 12, 24, 10, 0, 0);
        var p = UninstallEntryMapper.Map(Entry(SoftwareSource.Machine64, new()
        {
            ["DisplayName"] = "Hidden Helper",
            ["SystemComponent"] = 1,
            ["DisplayIcon"] = @"C:\Apps\Helper\helper.exe,0",
            ["VersionMajor"] = 3,
            ["VersionMinor"] = 7,
        }, stamp));
        Assert.NotNull(p);
        Assert.Equal(stamp, p!.InstallDate);
        Assert.True(p.IsSystemComponent);
        Assert.Equal(SoftwareSource.SystemComponent, p.Source);
        Assert.Equal(@"C:\Apps\Helper", p.InstallLocation);
        Assert.Equal("3.7", p.Version);
    }
}

public class StoreAppMapperTests
{
    [Fact]
    public void Maps_store_package_and_skips_frameworks()
    {
        var app = StoreAppMapper.Map(new AppxPackageDto
        {
            Name = "Contoso.PhotoStudio",
            PackageFullName = "Contoso.PhotoStudio_1.2.3.0_x64__abc",
            Publisher = "CN=Contoso Ltd, O=Contoso",
            Version = "1.2.3.0",
            InstallLocation = @"C:\Program Files\WindowsApps\Contoso.PhotoStudio_1.2.3.0_x64__abc",
            SignatureKind = "Store",
        });
        Assert.NotNull(app);
        Assert.Equal("Photo Studio", app!.Name);
        Assert.Equal("Contoso Ltd", app.Publisher);
        Assert.Equal(SoftwareSource.StoreApp, app.Source);
        Assert.Equal(InstallerKind.StoreApp, app.Installer);
        Assert.StartsWith("appx:", app.Id);

        Assert.Null(StoreAppMapper.Map(new AppxPackageDto { Name = "Microsoft.VCLibs.140.00", PackageFullName = "x", IsFramework = true }));
        Assert.Null(StoreAppMapper.Map(new AppxPackageDto { Name = "NoFullName" }));
    }

    [Fact]
    public void Json_from_powershell_parses_with_numeric_signature_kind()
    {
        const string json = """[{"Name":"Contoso.App","PackageFullName":"Contoso.App_1.0.0.0_x64__abc","Publisher":"CN=Contoso","Version":"1.0.0.0","IsFramework":false,"SignatureKind":4,"NonRemovable":true}]""";
        var dtos = PowerShellRunner.ParseJson<List<AppxPackageDto>>(json);
        Assert.NotNull(dtos);
        var app = StoreAppMapper.Map(Assert.Single(dtos!));
        Assert.NotNull(app);
        Assert.True(app!.IsSystemComponent);
    }

    [Fact]
    public void Json_from_windows_powershell_parses_version_object()
    {
        // Windows PowerShell 5.1 serializes System.Version as an object when it is not projected to a string.
        const string json = """[{"Name":"Contoso.App","PackageFullName":"Contoso.App_1.2.3.4_x64__abc","Version":{"Major":1,"Minor":2,"Build":3,"Revision":4,"MajorRevision":0,"MinorRevision":4},"SignatureKind":"Store"}]""";
        var dtos = PowerShellRunner.ParseJson<List<AppxPackageDto>>(json);
        Assert.NotNull(dtos);
        var dto = Assert.Single(dtos!);
        Assert.Equal("1.2.3.4", dto.Version);
        Assert.Equal("1.2.3.4", StoreAppMapper.Map(dto)!.Version);
    }

    [Fact]
    public void Version_converter_accepts_string_null_and_partial_objects()
    {
        Assert.Equal("2.0.1.0", PowerShellRunner.ParseJson<AppxPackageDto>("""{"PackageFullName":"x","Version":"2.0.1.0"}""")!.Version);
        Assert.Null(PowerShellRunner.ParseJson<AppxPackageDto>("""{"PackageFullName":"x","Version":null}""")!.Version);
        Assert.Equal("1.2", PowerShellRunner.ParseJson<AppxPackageDto>("""{"PackageFullName":"x","Version":{"Major":1,"Minor":2,"Build":-1,"Revision":-1}}""")!.Version);
        Assert.Equal("1.2.3.4", AppxVersionConverter.Format(1, 2, 3, 4));
        Assert.Equal("1.2", AppxVersionConverter.Format(1, 2, -1, -1));
        Assert.Null(AppxVersionConverter.Format(null, null, null, null));
    }

    [Fact]
    public void Reader_command_projects_version_to_string()
    {
        var field = typeof(StoreAppReader).GetField("Command", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var command = (string)field!.GetValue(null)!;
        Assert.Contains("[string]$_.Version", command);
    }
}

public class SoftwareInventoryAssemblerTests
{
    private static readonly DateTime T0 = new(2025, 5, 1, 9, 0, 0);

    [Fact]
    public void Assemble_applies_protection_detects_bundles_and_links_both_ways()
    {
        var app = InstalledProgram.Create("m64:app", "Video Tool", "Contoso", T0, @"C:\Program Files\VideoTool");
        var extra = InstalledProgram.Create("m64:extra", "Driver Booster 12", "IObit", T0.AddMinutes(1), @"C:\Program Files (x86)\IObit\Driver Booster");
        var edge = InstalledProgram.Create("m64:edge", "Microsoft Edge", "Microsoft Corporation", T0.AddDays(-3));

        var inv = SoftwareInventoryAssembler.Assemble([extra, edge, app], ["note"]);

        Assert.Equal(3, inv.Programs.Count);
        Assert.Equal(["note"], inv.Warnings);
        var group = Assert.Single(inv.Bundles);
        Assert.Equal("m64:app", group.Parent.Id);
        Assert.Equal("m64:extra", Assert.Single(group.Children).Id);

        var parent = inv.Programs.Single(p => p.Id == "m64:app");
        var child = inv.Programs.Single(p => p.Id == "m64:extra");
        Assert.Contains("m64:extra", parent.BundledChildIds);
        Assert.Equal("m64:app", child.BundledWithId);
        Assert.True(inv.Programs.Single(p => p.Id == "m64:edge").IsProtected);
        Assert.Same(parent, group.Parent);
    }

    [Fact]
    public void Duplicates_across_hives_keep_the_richer_entry()
    {
        var poor = InstalledProgram.Create("m32:a", "Tool", "Contoso", version: "1.0", source: SoftwareSource.Machine32);
        var rich = InstalledProgram.Create("m64:a", "Tool", "Contoso", version: "1.0", installLocation: @"C:\Tool", uninstallString: @"C:\Tool\uninstall.exe", sizeBytes: 10);
        var user = InstalledProgram.Create("usr:a", "Tool", "Contoso", version: "1.0", source: SoftwareSource.User);

        var kept = SoftwareInventoryAssembler.Deduplicate([poor, rich, user]);
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, p => p.Id == "m64:a");
        Assert.Contains(kept, p => p.Id == "usr:a");
    }

    [Fact]
    public void Registry_parent_key_name_resolves_to_a_program_id()
    {
        var parent = InstalledProgram.Create("m64:Office16", "Office", "Microsoft") with { RegistryKey = @"HKLM\...\Uninstall\Office16" };
        var child = InstalledProgram.Create("m64:Office16.Lang", "Office Language Pack", "Microsoft") with { RegistryKey = @"HKLM\...\Uninstall\Office16.Lang", BundledWithId = "Office16" };
        var linked = SoftwareInventoryAssembler.Link([parent, child], []);
        Assert.Equal("m64:Office16", linked.Single(p => p.Id == "m64:Office16.Lang").BundledWithId);
    }
}

public class SoftwareCsvExporterTests
{
    [Fact]
    public void Builds_header_and_escapes_cells()
    {
        var a = InstalledProgram.Create("a", "Tool, \"Pro\"", "Contoso", new DateTime(2025, 1, 2), @"C:\Tool", uninstallString: @"C:\Tool\u.exe", sizeBytes: 1234, version: "1.0");
        var b = InstalledProgram.Create("b", "Extra", "Other") with { BundledWithId = "a" };
        var csv = SoftwareCsvExporter.Build([a, b]);
        var lines = csv.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Name,Version,Publisher,Installed,Size (bytes)", lines[0]);
        Assert.StartsWith("\"Tool, \"\"Pro\"\"\",1.0,Contoso,2025-01-02,1234,C:\\Tool,Machine64", lines[1]);
        Assert.Contains("\"Tool, \"\"Pro\"\"\"", lines[2]);
        Assert.Equal(string.Empty, SoftwareCsvExporter.Escape(null));
        Assert.Equal("\"a,b\"", SoftwareCsvExporter.Escape("a,b"));
    }
}

public class DirectorySizeCalculatorTests
{
    [Fact]
    public async Task Measures_folders_and_leaves_missing_ones_alone()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcchecker-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        await File.WriteAllBytesAsync(Path.Combine(root, "a.bin"), new byte[1000]);
        await File.WriteAllBytesAsync(Path.Combine(root, "sub", "b.bin"), new byte[500]);
        try
        {
            var measured = InstalledProgram.Create("m", "Measured", installLocation: root, sizeBytes: 1);
            var missing = InstalledProgram.Create("x", "Missing", installLocation: Path.Combine(root, "does-not-exist"), sizeBytes: 77);
            var store = InstalledProgram.Create("s", "Store", installLocation: root, source: SoftwareSource.StoreApp);

            Assert.True(DirectorySizeCalculator.ShouldCalculate(measured, onlyMissing: false));
            Assert.False(DirectorySizeCalculator.ShouldCalculate(measured, onlyMissing: true));
            Assert.False(DirectorySizeCalculator.ShouldCalculate(store, onlyMissing: false));

            var updated = await DirectorySizeCalculator.CalculateAsync([measured, missing, store], onlyMissing: false, null, CancellationToken.None);
            Assert.Equal(1500, updated.Single(p => p.Id == "m").SizeBytes);
            Assert.Equal(77, updated.Single(p => p.Id == "x").SizeBytes);
            Assert.Null(updated.Single(p => p.Id == "s").SizeBytes);
            Assert.Null(DirectorySizeCalculator.TryGetSize(Path.Combine(root, "nope"), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class DemoSoftwareServicesTests
{
    [Fact]
    public async Task Demo_inventory_has_bundles_protected_entries_and_store_apps()
    {
        var inv = await new DemoSoftwareInventoryService().ScanAsync(null, CancellationToken.None);
        Assert.True(inv.Programs.Count >= 30);
        Assert.NotEmpty(inv.Bundles);
        Assert.Contains(inv.Programs, p => p.Source == SoftwareSource.StoreApp);
        Assert.Contains(inv.Programs, p => p.IsProtected && p.Name.Contains("Edge"));
        Assert.Contains(inv.Bundles, b => b.Children.Any(c => c.Name.Contains("Avast Secure Browser")));
        Assert.Equal(inv.Programs.Select(p => p.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), inv.Programs.Select(p => p.Name));
    }

    [Fact]
    public async Task Demo_uninstall_respects_protection()
    {
        var svc = new DemoUninstallService();
        var edge = ProtectedSoftwareRules.Apply(InstalledProgram.Create("e", "Microsoft Edge", uninstallString: @"C:\x\setup.exe --uninstall"));
        Assert.False(svc.CanUninstall(edge).CanUninstall);
        Assert.False((await svc.UninstallAsync(edge, true, null, CancellationToken.None)).Success);

        var vlc = InstalledProgram.Create("v", "VLC", uninstallString: @"C:\x\uninstall.exe");
        Assert.True(svc.CanUninstall(vlc).CanUninstall);
        var result = await svc.UninstallAsync(vlc, true, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains("demo", result.Message);
    }
}
