using PcChecker.Core.Common;
using PcChecker.Core.Software;
using Xunit;

namespace PcChecker.Core.Tests;

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
