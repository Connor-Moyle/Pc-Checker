using System.Text;
using PcChecker.Core.GameRuntimes;
using PcChecker.Core.Processes;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Builds small but valid PE32 / PE32+ images with an import table and a delay-import table.</summary>
internal static class PeImageBuilder
{
    private const int SectionRva = 0x1000;
    private const int SectionFileOffset = 0x200;

    public static byte[] Build(bool pe32Plus, IReadOnlyList<string> imports, IReadOnlyList<string> delayImports, bool isDll = false,
        bool oldStyleDelay = false, ushort? machine = null)
    {
        const ulong imageBase = 0x400000;
        int optionalSize = pe32Plus ? 240 : 224;

        // Section layout (RVAs relative to SectionRva): import descriptors, delay descriptors, then names.
        int importTable = 0;
        int delayTable = importTable + (20 * (imports.Count + 1));
        int names = delayTable + (32 * (delayImports.Count + 1));
        var section = new List<byte>(new byte[names]);
        var importNameRvas = new List<int>();
        var delayNameRvas = new List<int>();
        foreach (var name in imports)
        {
            importNameRvas.Add(SectionRva + section.Count);
            section.AddRange(Encoding.ASCII.GetBytes(name));
            section.Add(0);
        }

        foreach (var name in delayImports)
        {
            delayNameRvas.Add(SectionRva + section.Count);
            section.AddRange(Encoding.ASCII.GetBytes(name));
            section.Add(0);
        }

        while (section.Count % 0x200 != 0)
        {
            section.Add(0);
        }

        var data = section.ToArray();
        for (int i = 0; i < imports.Count; i++)
        {
            int at = importTable + (20 * i);
            WriteUInt32(data, at, (uint)(SectionRva + 0x10)); // OriginalFirstThunk (any non-zero RVA)
            WriteUInt32(data, at + 12, (uint)importNameRvas[i]);
            WriteUInt32(data, at + 16, (uint)(SectionRva + 0x10)); // FirstThunk
        }

        for (int i = 0; i < delayImports.Count; i++)
        {
            int at = delayTable + (32 * i);
            WriteUInt32(data, at, oldStyleDelay ? 0u : 1u);
            WriteUInt32(data, at + 4, oldStyleDelay ? (uint)(imageBase + (ulong)delayNameRvas[i]) : (uint)delayNameRvas[i]);
        }

        var image = new byte[SectionFileOffset + data.Length];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        WriteUInt32(image, 0x3C, 0x40);
        int pe = 0x40;
        image[pe] = (byte)'P';
        image[pe + 1] = (byte)'E';
        int fh = pe + 4;
        WriteUInt16(image, fh, machine ?? (ushort)(pe32Plus ? 0x8664 : 0x14C));
        WriteUInt16(image, fh + 2, 1);
        WriteUInt16(image, fh + 16, (ushort)optionalSize);
        WriteUInt16(image, fh + 18, (ushort)(0x0002 | (isDll ? 0x2000 : 0)));
        int opt = fh + 20;
        WriteUInt16(image, opt, (ushort)(pe32Plus ? 0x20B : 0x10B));
        if (pe32Plus)
        {
            WriteUInt64(image, opt + 24, imageBase);
        }
        else
        {
            WriteUInt32(image, opt + 28, (uint)imageBase);
        }

        WriteUInt32(image, opt + 60, SectionFileOffset); // SizeOfHeaders
        int countAt = pe32Plus ? 108 : 92;
        int dirAt = pe32Plus ? 112 : 96;
        WriteUInt32(image, opt + countAt, 16);
        if (imports.Count > 0)
        {
            WriteUInt32(image, opt + dirAt + 8, (uint)(SectionRva + importTable));
            WriteUInt32(image, opt + dirAt + 12, (uint)(20 * (imports.Count + 1)));
        }

        if (delayImports.Count > 0)
        {
            WriteUInt32(image, opt + dirAt + (8 * 13), (uint)(SectionRva + delayTable));
            WriteUInt32(image, opt + dirAt + (8 * 13) + 4, (uint)(32 * (delayImports.Count + 1)));
        }

        int sh = opt + optionalSize;
        Encoding.ASCII.GetBytes(".idata").CopyTo(image, sh);
        WriteUInt32(image, sh + 8, (uint)data.Length);
        WriteUInt32(image, sh + 12, SectionRva);
        WriteUInt32(image, sh + 16, (uint)data.Length);
        WriteUInt32(image, sh + 20, SectionFileOffset);
        data.CopyTo(image, SectionFileOffset);
        return image;
    }

    private static void WriteUInt16(byte[] b, int at, ushort v) => BitConverter.GetBytes(v).CopyTo(b, at);

    private static void WriteUInt32(byte[] b, int at, uint v) => BitConverter.GetBytes(v).CopyTo(b, at);

    private static void WriteUInt64(byte[] b, int at, ulong v) => BitConverter.GetBytes(v).CopyTo(b, at);
}

public class PeImportReaderTests
{
    [Fact]
    public void Reads_a_32_bit_program_with_imports_and_delay_imports()
    {
        var image = PeImageBuilder.Build(false, ["KERNEL32.dll", "d3dx9_43.dll", "XINPUT1_3.dll"], ["dbghelp.dll"]);

        var info = PeImportReader.Read(image);

        Assert.NotNull(info);
        Assert.False(info.IsPe32Plus);
        Assert.Equal(DllArch.X86, info.Arch);
        Assert.False(info.IsDll);
        Assert.Equal(["KERNEL32.dll", "d3dx9_43.dll", "XINPUT1_3.dll"], info.Imports);
        Assert.Equal(["dbghelp.dll"], info.DelayImports);
    }

    [Fact]
    public void Reads_a_64_bit_dll()
    {
        var image = PeImageBuilder.Build(true, ["VCRUNTIME140.dll", "MSVCP140.dll", "api-ms-win-crt-runtime-l1-1-0.dll"], ["vulkan-1.dll", "nvapi64.dll"], isDll: true);

        var info = PeImportReader.Read(image);

        Assert.NotNull(info);
        Assert.True(info.IsPe32Plus);
        Assert.Equal(DllArch.X64, info.Arch);
        Assert.True(info.IsDll);
        Assert.Equal(0x8664, info.Machine);
        Assert.Equal(3, info.Imports.Count);
        Assert.Equal(["vulkan-1.dll", "nvapi64.dll"], info.DelayImports);
    }

    [Fact]
    public void Old_visual_cpp_6_delay_descriptors_with_virtual_addresses_are_read()
    {
        var image = PeImageBuilder.Build(false, ["KERNEL32.dll"], ["dplayx.dll"], oldStyleDelay: true);

        var info = PeImportReader.Read(image);

        Assert.NotNull(info);
        Assert.Equal(["dplayx.dll"], info.DelayImports);
    }

    [Fact]
    public void A_program_without_imports_has_empty_lists()
    {
        var info = PeImportReader.Read(PeImageBuilder.Build(true, [], []));

        Assert.NotNull(info);
        Assert.Empty(info.Imports);
        Assert.Empty(info.DelayImports);
    }

    [Fact]
    public void Truncated_files_never_throw()
    {
        var image = PeImageBuilder.Build(false, ["KERNEL32.dll", "msvcr100.dll", "OpenAL32.dll"], ["dbghelp.dll"]);
        for (int length = 0; length < image.Length; length += 7)
        {
            var cut = image.AsSpan(0, length).ToArray();
            var info = PeImportReader.Read(cut);
            if (info is not null)
            {
                Assert.All(info.Imports, n => Assert.False(string.IsNullOrWhiteSpace(n)));
            }
        }
    }

    [Fact]
    public void Garbage_and_hostile_headers_give_null()
    {
        Assert.Null(PeImportReader.Read([]));
        Assert.Null(PeImportReader.Read(Encoding.ASCII.GetBytes("MZ this is not a program at all, only text that starts with MZ")));
        Assert.Null(PeImportReader.Read(new byte[4096]));

        var random = new byte[8192];
        new Random(42).NextBytes(random);
        random[0] = (byte)'M';
        random[1] = (byte)'Z';
        _ = PeImportReader.Read(random); // must not throw

        // e_lfanew pointing far outside the file.
        var image = PeImageBuilder.Build(false, ["KERNEL32.dll"], []);
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(image, 0x3C);
        Assert.Null(PeImportReader.Read(image));

        // Unknown optional header magic.
        image = PeImageBuilder.Build(false, ["KERNEL32.dll"], []);
        image[0x40 + 24] = 0x99;
        Assert.Null(PeImportReader.Read(image));
    }

    [Fact]
    public void Corrupt_name_pointers_and_endless_tables_are_skipped_safely()
    {
        var image = PeImageBuilder.Build(false, ["KERNEL32.dll", "USER32.dll"], []);

        // Point the second descriptor's name far outside every section: it is skipped, the first one kept.
        BitConverter.GetBytes(0x7FFF0000u).CopyTo(image, 0x200 + 20 + 12);
        var info = PeImportReader.Read(image);
        Assert.NotNull(info);
        Assert.Equal(["KERNEL32.dll"], info.Imports);

        // Names with path characters are refused.
        image = PeImageBuilder.Build(false, [@"..\..\evil.dll"], []);
        info = PeImportReader.Read(image);
        Assert.NotNull(info);
        Assert.Empty(info.Imports);
    }

    [Fact]
    public void Reading_a_missing_file_gives_null()
    {
        Assert.Null(PeImportReader.ReadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "game.exe")));
    }
}

public class RuntimeCatalogTests
{
    [Fact]
    public void The_list_feels_complete()
    {
        Assert.True(RuntimeCatalog.Dlls.Count >= 200, $"Only {RuntimeCatalog.Dlls.Count} DLLs in the catalog.");
        foreach (var name in new[] { "d3dx9_43.dll", "d3dx9_24.dll", "xinput1_3.dll", "msvcp140.dll", "vcruntime140_1.dll", "msvcr100.dll", "msvcr80.dll",
                     "OpenAL32.dll", "PhysXLoader.dll", "xlive.dll", "dplayx.dll", "vulkan-1.dll", "d3d9.dll", "xactengine3_7.dll", "xaudio2_7.dll" })
        {
            Assert.NotNull(RuntimeCatalog.Find(name));
        }

        Assert.NotNull(RuntimeCatalog.Find("D3DX9_43.DLL"));
        Assert.Null(RuntimeCatalog.Find("steam_api.dll"));
    }

    [Fact]
    public void No_dll_name_belongs_to_two_packages_for_the_same_architecture()
    {
        foreach (var arch in new[] { ArchSet.X86, ArchSet.X64 })
        {
            var duplicates = RuntimeCatalog.Dlls.Where(d => d.Archs.HasFlag(arch))
                .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            Assert.Empty(duplicates);
        }
    }

    [Fact]
    public void Every_dll_has_a_package_for_each_of_its_architectures()
    {
        foreach (var dll in RuntimeCatalog.Dlls)
        {
            Assert.NotEqual(ArchSet.None, dll.Archs);
            foreach (var arch in new[] { DllArch.X86, DllArch.X64 }.Where(dll.Has))
            {
                Assert.True(RuntimeCatalog.PackageFor(dll.FamilyId, arch) is not null, $"{dll.Name} ({arch}) has no package.");
            }

            if (dll.Location == DllLocation.WinSxS)
            {
                Assert.False(string.IsNullOrEmpty(dll.SxsAssembly));
            }
        }
    }

    [Fact]
    public void Every_download_comes_from_an_allow_listed_vendor_host_with_an_expected_signer_and_silent_switches()
    {
        var installers = RuntimeCatalog.Packages.Where(p => p.Kind == RuntimeInstallKind.Installer).ToList();
        Assert.True(installers.Count >= 15);
        foreach (var p in installers)
        {
            Assert.NotNull(p.Download);
            var d = p.Download!;
            Assert.StartsWith("https://", d.Url, StringComparison.Ordinal);
            Assert.True(RuntimeInstallRules.IsAllowedUrl(p, d.Url), $"{p.Id}: {d.Url} is not allowed");
            Assert.NotEmpty(p.AllowedHosts);
            Assert.All(p.AllowedHosts, h => Assert.Contains(h, RuntimeCatalog.VendorHosts));
            Assert.NotEmpty(p.ExpectedSigners);
            Assert.True(RuntimeInstallRules.IsSafeFileName(d.FileName, d.Format), $"{p.Id}: {d.FileName}");
            Assert.True(d.MaxBytes is > 0 and <= 256L * 1024 * 1024);
            Assert.False(string.IsNullOrWhiteSpace(p.SourceNote));

            var silent = d.Format is InstallerFormat.ZipWithExe or InstallerFormat.DirectXRedist ? d.InnerArguments : d.Arguments;
            Assert.True(silent is { Count: > 0 }, $"{p.Id} has no silent switches");
            if (d.Format is InstallerFormat.ZipWithExe or InstallerFormat.DirectXRedist)
            {
                Assert.False(string.IsNullOrWhiteSpace(d.InnerFileName));
            }

            if (p.WingetId is not null)
            {
                Assert.True(RuntimeInstallRules.IsValidWingetId(p.WingetId), p.WingetId);
            }
        }

        Assert.All(RuntimeCatalog.Packages.Where(p => p.Kind == RuntimeInstallKind.WindowsFeature), p => Assert.False(string.IsNullOrEmpty(p.FeatureName)));
        Assert.All(RuntimeCatalog.Packages.Where(p => !p.CanInstall), p => Assert.False(string.IsNullOrWhiteSpace(p.UnavailableReason), p.Id));
    }

    [Fact]
    public void Visual_cpp_has_separate_32_and_64_bit_packages_with_the_documented_switches()
    {
        foreach (var year in new[] { "vc14", "vc2013", "vc2012", "vc2010", "vc2008", "vc2005" })
        {
            var x86 = RuntimeCatalog.Package(year + "-x86");
            var x64 = RuntimeCatalog.Package(year + "-x64");
            Assert.NotNull(x86);
            Assert.NotNull(x64);
            Assert.Equal(DllArch.X86, x86!.Arch);
            Assert.Equal(DllArch.X64, x64!.Arch);
            Assert.Contains("x86", x86.Download!.Url, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("x64", x64.Download!.Url, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".x86", x86.WingetId, StringComparison.Ordinal);
        }

        Assert.Equal(["/install", "/quiet", "/norestart"], RuntimeCatalog.Package("vc14-x64")!.Download!.Arguments);
        Assert.Equal(["/install", "/quiet", "/norestart"], RuntimeCatalog.Package("vc2012-x86")!.Download!.Arguments);
        Assert.Equal(["/q", "/norestart"], RuntimeCatalog.Package("vc2010-x64")!.Download!.Arguments);
        Assert.Equal(["/q"], RuntimeCatalog.Package("vc2005-x86")!.Download!.Arguments);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", RuntimeCatalog.Package("vc14-x64")!.WingetId);

        // vcruntime140_1.dll only exists for 64-bit programs.
        var v = RuntimeCatalog.Find("vcruntime140_1.dll")!;
        Assert.True(v.Has(DllArch.X64));
        Assert.False(v.Has(DllArch.X86));
        Assert.False(RuntimeCatalog.Package("vc2003")!.CanInstall);
    }

    [Fact]
    public void DirectX_is_extracted_then_installed_with_its_signed_setup()
    {
        var dx = RuntimeCatalog.Package("directx")!;
        Assert.Null(dx.Arch);
        Assert.Null(dx.WingetId);
        Assert.Equal(InstallerFormat.DirectXRedist, dx.Download!.Format);
        Assert.Equal("download.microsoft.com", dx.SourceHost);
        var args = RuntimeInstallRules.Arguments(dx.Download.Arguments, @"C:\ProgramData\PcChecker-runtime-1\dx");
        Assert.Equal(["/Q", @"/T:C:\ProgramData\PcChecker-runtime-1\dx"], args);
        Assert.Equal("DXSETUP.exe", dx.Download.InnerFileName);
        Assert.Equal(["/silent"], dx.Download.InnerArguments);
        Assert.Equal(["Microsoft Corporation"], dx.ExpectedSigners);
    }
}

public class RuntimeStatusRulesTests
{
    private static RuntimeProbe Probe(Func<KnownDll, DllArch, bool> present, bool nEdition = false, Dictionary<string, bool?>? features = null,
        Dictionary<string, IReadOnlyList<string>>? versions = null) =>
        new(present, versions ?? [], features ?? [], nEdition);

    [Fact]
    public void A_dll_is_not_applicable_for_an_architecture_it_is_never_made_for()
    {
        var d3d8 = RuntimeCatalog.Find("d3d8.dll")!;
        Assert.Equal(DllStatus.NotApplicable, RuntimeStatusRules.StatusOf(d3d8, DllArch.X64, true, (_, _) => true));
        Assert.Equal(DllStatus.Present, RuntimeStatusRules.StatusOf(d3d8, DllArch.X86, true, (_, _) => true));
        Assert.Equal(DllStatus.Missing, RuntimeStatusRules.StatusOf(RuntimeCatalog.Find("d3dx9_43.dll")!, DllArch.X64, true, (_, _) => false));
        Assert.Equal(DllStatus.NotApplicable, RuntimeStatusRules.StatusOf(RuntimeCatalog.Find("d3dx9_43.dll")!, DllArch.X64, false, (_, _) => true));
    }

    [Fact]
    public void Packages_are_installed_partial_or_missing_per_architecture()
    {
        // VC++ 2015+ only for 64-bit; DirectX has only d3dx9_43 for 32-bit; everything else missing.
        bool Present(KnownDll d, DllArch a) =>
            (d.FamilyId == "vc14" && a == DllArch.X64) || (d.Name == "d3dx9_43.dll" && a == DllArch.X86) || d.FamilyId == "windows";
        var scan = RuntimeStatusRules.Build(Probe(Present, versions: new() { ["vc14-x64"] = ["14.44.35211"] }), true, DateTimeOffset.Now);

        PackageState State(string id) => scan.Packages.Single(p => p.Package.Id == id).State;
        Assert.Equal(PackageState.Installed, State("vc14-x64"));
        Assert.Equal(PackageState.Missing, State("vc14-x86"));
        Assert.Equal(PackageState.Partial, State("directx"));
        Assert.Equal(PackageState.Missing, State("openal"));
        Assert.Equal(PackageState.NotApplicable, State("windows"));
        Assert.Equal(PackageState.NotApplicable, State("mediafeaturepack"));
        Assert.Equal(["14.44.35211"], scan.Packages.Single(p => p.Package.Id == "vc14-x64").InstalledVersions);

        var row = scan.Dlls.Single(r => r.Dll.Name == "vcruntime140_1.dll");
        Assert.Equal(DllStatus.NotApplicable, row.Status32);
        Assert.Equal(DllStatus.Present, row.Status64);

        var (m32, m64) = RuntimeStatusRules.MissingCounts(scan);
        Assert.True(m32 > m64);
        Assert.DoesNotContain(RuntimeStatusRules.MissingInstallable(scan), p => p.Id == "vc14-x64");
        Assert.Contains(RuntimeStatusRules.MissingInstallable(scan), p => p.Id == "directx");
        Assert.DoesNotContain(RuntimeStatusRules.MissingInstallable(scan), p => p.Id is "gfwl" or "vc2003" or "windows");
    }

    [Fact]
    public void Feature_state_wins_over_files_and_media_pack_only_counts_on_N_editions()
    {
        var features = new Dictionary<string, bool?> { ["NetFx3"] = false, ["DirectPlay"] = true };
        var scan = RuntimeStatusRules.Build(Probe((_, _) => true, features: features), true, DateTimeOffset.Now);
        Assert.Equal(PackageState.Missing, scan.Packages.Single(p => p.Package.Id == "netfx3").State);
        Assert.Equal(PackageState.Installed, scan.Packages.Single(p => p.Package.Id == "directplay").State);

        var nScan = RuntimeStatusRules.Build(Probe((d, _) => d.FamilyId != "mediafeaturepack", nEdition: true), true, DateTimeOffset.Now);
        Assert.Equal(PackageState.Missing, nScan.Packages.Single(p => p.Package.Id == "mediafeaturepack").State);
        Assert.True(RuntimeStatusRules.CountsAsProblem(nScan.Dlls.First(r => r.Dll.FamilyId == "mediafeaturepack"), true));
        Assert.False(RuntimeStatusRules.CountsAsProblem(nScan.Dlls.First(r => r.Dll.FamilyId == "mediafeaturepack"), false));
        Assert.False(RuntimeStatusRules.CountsAsProblem(nScan.Dlls.First(r => r.Dll.Name == "xlive.dll"), false));
    }

    [Fact]
    public void Install_targets_cover_only_the_missing_architectures()
    {
        var msvcr100 = RuntimeCatalog.Find("msvcr100.dll")!;
        Assert.Equal(["vc2010-x86"], RuntimeStatusRules.InstallTargets(new DllStatusRow(msvcr100, DllStatus.Missing, DllStatus.Present)).Select(p => p.Id));
        Assert.Equal(["vc2010-x86", "vc2010-x64"], RuntimeStatusRules.InstallTargets(new DllStatusRow(msvcr100, DllStatus.Missing, DllStatus.Missing)).Select(p => p.Id));
        Assert.Equal(["vc2010-x86", "vc2010-x64"], RuntimeStatusRules.InstallTargets(new DllStatusRow(msvcr100, DllStatus.Present, DllStatus.Present)).Select(p => p.Id));

        // DirectX is one package for both architectures: once.
        var d3dx = RuntimeCatalog.Find("d3dx9_43.dll")!;
        Assert.Equal(["directx"], RuntimeStatusRules.InstallTargets(new DllStatusRow(d3dx, DllStatus.Missing, DllStatus.Missing)).Select(p => p.Id));

        // Windows parts and dead services cannot be installed.
        Assert.Empty(RuntimeStatusRules.InstallTargets(new DllStatusRow(RuntimeCatalog.Find("d3d9.dll")!, DllStatus.Missing, DllStatus.Missing)));
        Assert.Empty(RuntimeStatusRules.InstallTargets(new DllStatusRow(RuntimeCatalog.Find("xlive.dll")!, DllStatus.Missing, DllStatus.NotApplicable)));
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "vc14-x64")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.40.33810", "vc14-x86")]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x64) - 14.50.35719", "vc14-x64")]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x86) - 12.0.40664", "vc2013-x86")]
    [InlineData("Microsoft Visual C++ 2012 x64 Minimum Runtime - 11.0.61030", "vc2012-x64")]
    [InlineData("Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "vc2010-x64")]
    [InlineData("Microsoft Visual C++ 2008 Redistributable - x86 9.0.30729.6161", "vc2008-x86")]
    [InlineData("Microsoft Visual C++ 2005 Redistributable", "vc2005-x86")]
    [InlineData("Microsoft Visual C++ 2005 Redistributable (x64)", "vc2005-x64")]
    [InlineData("Microsoft XNA Framework Redistributable 4.0 Refresh", "xna4")]
    [InlineData("OpenAL", "openal")]
    [InlineData("NVIDIA PhysX System Software 9.23.1019", "physx")]
    [InlineData("Microsoft Visual C++ 2022 X64 Additional Runtime - 14.40.33810", null)]
    [InlineData("Microsoft Visual Studio Code", null)]
    [InlineData(null, null)]
    public void Uninstall_entries_map_to_packages(string? displayName, string? expected) =>
        Assert.Equal(expected, RuntimeStatusRules.PackageIdFromDisplayName(displayName));

    [Theory]
    [InlineData("v14.40.33810.00", "14.40.33810.00")]
    [InlineData("10.0.40219", "10.0.40219")]
    [InlineData("Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "10.0.40219")]
    [InlineData("none", null)]
    public void Versions_are_cleaned(string text, string? expected) => Assert.Equal(expected, RuntimeStatusRules.CleanVersion(text));

    [Theory]
    [InlineData("ProfessionalN", true)]
    [InlineData("CoreN", true)]
    [InlineData("EnterpriseSN", true)]
    [InlineData("Professional", false)]
    [InlineData("Core", false)]
    [InlineData(null, false)]
    public void N_editions_are_recognised(string? edition, bool expected) => Assert.Equal(expected, RuntimeStatusRules.IsNEdition(edition));

    [Fact]
    public void Feature_states_are_parsed_from_the_query_output()
    {
        var map = RuntimeInstallRules.ParseFeatureStates("""[{"Name":"NetFx3","State":"Disabled"},{"Name":"DirectPlay","State":"Enabled"}]""");
        Assert.False(map["NetFx3"]);
        Assert.True(map["DirectPlay"]);
        Assert.False(RuntimeInstallRules.ParseFeatureStates("""{"Name":"NetFx3","State":"DisabledWithPayloadRemoved"}""")["NetFx3"]);
        Assert.Null(RuntimeInstallRules.ParseFeatureStates("""[{"Name":"NetFx3","State":"Weird"}]""")["NetFx3"]);
        Assert.Empty(RuntimeInstallRules.ParseFeatureStates("not json"));
        Assert.Empty(RuntimeInstallRules.ParseFeatureStates(null));
    }
}

public class RuntimeInstallRulesTests
{
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1641, true, true)]
    [InlineData(1638, true, false)]
    [InlineData(5100, true, false)]
    [InlineData(1602, false, false)]
    [InlineData(1618, false, false)]
    [InlineData(1603, false, false)]
    [InlineData(-9, false, false)]
    public void Exit_codes_are_read_in_plain_words(int code, bool success, bool restart)
    {
        var outcome = RuntimeInstallRules.InterpretExitCode(code, "Visual C++ 2010");
        Assert.Equal(success, outcome.Success);
        Assert.Equal(restart, outcome.RestartNeeded);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message));
    }

    [Fact]
    public void Exit_code_messages_explain_the_common_cases()
    {
        Assert.Contains("newer version", RuntimeInstallRules.InterpretExitCode(1638, "X").Message, StringComparison.Ordinal);
        Assert.Contains("newer Visual C++", RuntimeInstallRules.InterpretExitCode(5100, "X").Message, StringComparison.Ordinal);
        Assert.Contains("Restart", RuntimeInstallRules.InterpretExitCode(3010, "X").Message, StringComparison.Ordinal);
        Assert.Contains("-9", RuntimeInstallRules.InterpretExitCode(-9, "X").Message, StringComparison.Ordinal);
        Assert.True(RuntimeInstallRules.InterpretWingetExitCode(unchecked((int)0x8A150061), "X").Success);
        Assert.False(RuntimeInstallRules.InterpretWingetExitCode(unchecked((int)0x8A150014), "X").Success);
    }

    [Fact]
    public void Only_the_vendors_own_https_hosts_are_allowed()
    {
        var vc = RuntimeCatalog.Package("vc14-x64")!;
        Assert.True(RuntimeInstallRules.IsAllowedUrl(vc, "https://aka.ms/vc14/vc_redist.x64.exe"));
        Assert.True(RuntimeInstallRules.IsAllowedUrl(vc, "https://download.visualstudio.microsoft.com/download/pr/abc/VC_redist.x64.exe"));
        Assert.False(RuntimeInstallRules.IsAllowedUrl(vc, "http://download.microsoft.com/download/vc_redist.x64.exe"));
        Assert.False(RuntimeInstallRules.IsAllowedUrl(vc, "https://download.microsoft.com.evil.example/vc_redist.x64.exe"));
        Assert.False(RuntimeInstallRules.IsAllowedUrl(vc, "https://evildownload.microsoft.com/vc_redist.x64.exe"));
        Assert.False(RuntimeInstallRules.IsAllowedUrl(vc, "https://www.dll-files.com/msvcp140.dll.html"));
        Assert.False(RuntimeInstallRules.IsAllowedUrl(vc, "https://user@download.microsoft.com/x.exe"));
        Assert.False(RuntimeInstallRules.IsAllowedUrl(vc, "https://download.microsoft.com:8443/x.exe"));
        Assert.False(RuntimeInstallRules.IsAllowedUrl(vc, "https://13.107.4.50/x.exe"));

        // A package may only use its own vendor's hosts.
        var physx = RuntimeCatalog.Package("physx")!;
        Assert.True(RuntimeInstallRules.IsAllowedUrl(physx, "https://us.download.nvidia.com/Windows/9.26.0703/PhysX_9.26.0703_SystemSoftware.exe"));
        Assert.False(RuntimeInstallRules.IsAllowedUrl(physx, "https://download.microsoft.com/x.exe"));
        Assert.False(RuntimeInstallRules.IsAllowedUrl(RuntimeCatalog.Package("openal")!, "https://us.download.nvidia.com/x.zip"));
        Assert.True(RuntimeInstallRules.IsAllowedUrl(RuntimeCatalog.Package("openal")!, "https://www.openal.org/downloads/oalinst.zip"));
    }

    [Fact]
    public void A_redirect_to_a_host_that_is_not_the_vendors_is_refused()
    {
        var vc = RuntimeCatalog.Package("vc14-x86")!;
        Assert.Null(RuntimeInstallRules.CheckRedirectChain(vc,
            ["https://aka.ms/vc14/vc_redist.x86.exe", "https://aka.ms/vs/18/release/vc_redist.x86.exe",
             "https://download.visualstudio.microsoft.com/download/pr/6ad6/F0BA/VC_redist.x86.exe"]));
        var refused = RuntimeInstallRules.CheckRedirectChain(vc, ["https://aka.ms/vc14/vc_redist.x86.exe", "https://mirror.example.net/VC_redist.x86.exe"]);
        Assert.NotNull(refused);
        Assert.Contains("mirror.example.net", refused, StringComparison.Ordinal);
        Assert.NotNull(RuntimeInstallRules.CheckRedirectChain(vc, ["https://aka.ms/x", "http://download.microsoft.com/x.exe"]));
        Assert.Null(RuntimeInstallRules.CheckRedirectChain(vc, ["https://download.microsoft.com/a/x.exe", "/b/y.exe"]));
        Assert.NotNull(RuntimeInstallRules.CheckRedirectChain(vc, Enumerable.Repeat("https://aka.ms/loop", 10)));
        Assert.NotNull(RuntimeInstallRules.CheckRedirectChain(vc, []));
    }

    [Fact]
    public void The_installer_must_be_validly_signed_by_the_expected_vendor()
    {
        var openal = RuntimeCatalog.Package("openal")!;
        Assert.True(RuntimeInstallRules.EvaluateSignature(openal, SignatureRules.Success,
            "CN=Creative Labs Inc, OU=CLI, O=Creative Labs Inc, L=Milpitas, S=California, C=US").Trusted);
        Assert.True(RuntimeInstallRules.EvaluateSignature(openal, SignatureRules.Success, "CN=\"Creative Labs, Inc.\", C=US").Trusted);
        Assert.False(RuntimeInstallRules.EvaluateSignature(openal, SignatureRules.Success, "CN=DLL Files Ltd, C=US").Trusted);
        Assert.False(RuntimeInstallRules.EvaluateSignature(openal, SignatureRules.TrustENoSignature, null).Trusted);

        var vc = RuntimeCatalog.Package("vc2010-x86")!;
        Assert.True(RuntimeInstallRules.EvaluateSignature(vc, SignatureRules.Success, "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US").Trusted);
        Assert.False(RuntimeInstallRules.EvaluateSignature(vc, SignatureRules.CertERevoked, "CN=Microsoft Corporation").Trusted);
        Assert.False(RuntimeInstallRules.EvaluateSignature(vc, SignatureRules.Success, "CN=Microsoft Corporation Tools Ltd").Trusted);
    }

    [Fact]
    public void File_names_winget_ids_and_zip_entries_are_checked()
    {
        Assert.True(RuntimeInstallRules.IsSafeFileName("vc_redist.x64.exe", InstallerFormat.Exe));
        Assert.True(RuntimeInstallRules.IsSafeFileName("xnafx40_redist.msi", InstallerFormat.Msi));
        Assert.False(RuntimeInstallRules.IsSafeFileName("xnafx40_redist.msi", InstallerFormat.Exe));
        Assert.False(RuntimeInstallRules.IsSafeFileName(@"..\evil.exe", InstallerFormat.Exe));
        Assert.False(RuntimeInstallRules.IsSafeFileName("msvcp140.dll", InstallerFormat.Exe));

        Assert.True(RuntimeInstallRules.IsValidWingetId("Microsoft.VCRedist.2015+.x64"));
        Assert.False(RuntimeInstallRules.IsValidWingetId("Microsoft.VCRedist.2015+.x64 --override evil"));
        Assert.False(RuntimeInstallRules.IsValidWingetId("-x"));
        var args = RuntimeInstallRules.WingetArguments("Nvidia.PhysX");
        Assert.Equal("install", args[0]);
        Assert.Contains("--silent", args);
        Assert.Contains("--exact", args);
        Assert.Throws<ArgumentException>(() => RuntimeInstallRules.WingetArguments("bad id"));

        Assert.True(RuntimeInstallRules.IsExpectedZipEntry("oalinst.exe", "oalinst.exe"));
        Assert.False(RuntimeInstallRules.IsExpectedZipEntry("sub/oalinst.exe", "oalinst.exe"));
        Assert.False(RuntimeInstallRules.IsExpectedZipEntry("../oalinst.exe", "oalinst.exe"));
        Assert.Equal(["/i", @"C:\x\a.msi", "/qn"], RuntimeInstallRules.MsiArguments(@"C:\x\a.msi", ["/qn"]));
    }

    [Fact]
    public void Install_order_puts_features_first_and_lists_each_package_once_with_source_and_signer()
    {
        var order = RuntimeInstallRules.InstallOrder([RuntimeCatalog.Package("openal")!, RuntimeCatalog.Package("netfx3")!,
            RuntimeCatalog.Package("directx")!, RuntimeCatalog.Package("openal")!, RuntimeCatalog.Package("windows")!]);
        Assert.Equal(["netfx3", "directx", "openal"], order.Select(p => p.Id));

        var text = RuntimeInstallRules.ConfirmationText(order, wingetAvailable: false);
        Assert.Contains("download.microsoft.com", text, StringComparison.Ordinal);
        Assert.Contains("www.openal.org", text, StringComparison.Ordinal);
        Assert.Contains("Creative Labs Inc", text, StringComparison.Ordinal);
        Assert.Contains("NetFx3", text, StringComparison.Ordinal);
        Assert.Contains("No single DLL files", text, StringComparison.Ordinal);
        Assert.Contains("winget package Microsoft.VCRedist.2010.x86", RuntimeInstallRules.SourceText(RuntimeCatalog.Package("vc2010-x86")!, true), StringComparison.Ordinal);
    }
}

public class GameScanRulesTests
{
    private static readonly HashSet<string> GameFiles = new(StringComparer.OrdinalIgnoreCase) { "binkw32.dll", "steam_api.dll" };

    private static bool InSystem(string name, DllArch arch) =>
        name.ToLowerInvariant() is "kernel32.dll" or "user32.dll" or "d3d9.dll" or "dbghelp.dll" || (arch == DllArch.X64 && name == "vcruntime140.dll");

    [Theory]
    [InlineData("api-ms-win-crt-runtime-l1-1-0.dll", true)]
    [InlineData("API-MS-WIN-CORE-SYNCH-L1-2-0.DLL", true)]
    [InlineData("ext-ms-win-ntuser-window-l1-1-0.dll", true)]
    [InlineData("kernel32.dll", false)]
    [InlineData("apims.dll", false)]
    [InlineData("", false)]
    public void Api_sets_are_recognised(string name, bool expected) => Assert.Equal(expected, GameScanRules.IsApiSet(name));

    [Fact]
    public void Each_import_is_resolved_by_the_rules()
    {
        (ImportResolution, string?) R(string name, DllArch arch, Func<KnownDll, DllArch, bool>? installed = null) =>
            GameScanRules.Resolve(name, arch, GameFiles, InSystem, installed ?? ((_, _) => false));

        Assert.Equal((ImportResolution.InGameFolder, null), R("STEAM_API.dll", DllArch.X86));
        Assert.Equal((ImportResolution.WindowsApiSet, null), R("api-ms-win-crt-heap-l1-1-0.dll", DllArch.X64));
        Assert.Equal((ImportResolution.Windows, null), R("KERNEL32.dll", DllArch.X86));
        Assert.Equal((ImportResolution.Windows, null), R("dbghelp", DllArch.X86)); // no extension: ".dll" is added
        Assert.Equal((ImportResolution.RuntimeMissing, "vc2010-x86"), R("MSVCR100.dll", DllArch.X86));
        Assert.Equal((ImportResolution.RuntimeMissing, "vc2010-x64"), R("msvcr100.dll", DllArch.X64));
        Assert.Equal((ImportResolution.RuntimeMissing, "directx"), R("d3dx9_43.dll", DllArch.X86));
        Assert.Equal((ImportResolution.RuntimeInstalled, null), R("xinput1_3.dll", DllArch.X86, (_, _) => true));
        Assert.Equal((ImportResolution.Windows, null), R("d3d9.dll", DllArch.X86, (_, _) => true));
        Assert.Equal((ImportResolution.WindowsPartMissing, "windows"), R("d3d9.dll", DllArch.X64));
        Assert.Equal((ImportResolution.NotFound, null), R("RallyOverlay.dll", DllArch.X86));
        Assert.Equal((ImportResolution.RuntimeMissing, "gfwl"), R("xlive.dll", DllArch.X86));
    }

    [Fact]
    public void A_scan_lists_each_dll_once_per_architecture_problems_first_and_needs_each_package_once()
    {
        var binaries = new[]
        {
            new GameBinary("Game.exe", new PeImportInfo(0x14C, false, false, ["KERNEL32.dll", "d3dx9_43.dll", "MSVCR100.dll", "steam_api.dll"], ["RallyOverlay.dll"])),
            new GameBinary(@"bin\Audio.dll", new PeImportInfo(0x14C, false, true, ["kernel32.dll", "OpenAL32.dll", "msvcr100.dll", "xinput1_3.dll"], [])),
            new GameBinary(@"bin\Late.dll", new PeImportInfo(0x14C, false, true, ["RallyOverlay.dll"], [])),
            new GameBinary("Tool64.exe", new PeImportInfo(0x8664, true, false, ["KERNEL32.dll", "VCRUNTIME140.dll", "MSVCR100.dll"], [])),
        };

        var imports = GameScanRules.ResolveAll(binaries, GameFiles, InSystem, (_, _) => false);

        Assert.Single(imports, i => i.DllName.Equals("msvcr100.dll", StringComparison.OrdinalIgnoreCase) && i.Arch == DllArch.X86);
        Assert.Single(imports, i => i.DllName.Equals("msvcr100.dll", StringComparison.OrdinalIgnoreCase) && i.Arch == DllArch.X64);
        Assert.True(imports.TakeWhile(i => i.IsProblem).Count() == imports.Count(i => i.IsProblem));
        Assert.Equal(ImportResolution.RuntimeMissing, imports[0].Resolution);

        // RallyOverlay is delay-loaded by Game.exe but a normal import of Late.dll: it counts as a normal import.
        var overlay = imports.Single(i => i.DllName == "RallyOverlay.dll");
        Assert.False(overlay.DelayLoaded);
        Assert.Equal(@"bin\Late.dll", overlay.ImportedBy);
        Assert.Contains("verify the game files", GameScanRules.Describe(overlay), StringComparison.Ordinal);

        string[] expected = ["directx", "vc14-x64", "vc2010-x64", "vc2010-x86", "openal"];
        Assert.Equal(expected.OrderBy(x => x), GameScanRules.NeededPackages(imports).OrderBy(x => x));
        Assert.Equal(GameScanRules.NeededPackages(imports).Count, GameScanRules.NeededPackages(imports).Distinct().Count());
    }

    [Fact]
    public void Game_names_come_from_the_folder_or_exe()
    {
        Assert.Equal("Midnight Rally 2009", GameScanRules.GameName(@"C:\Games\Midnight Rally 2009\"));
        Assert.Equal("MidnightRally", GameScanRules.GameName(@"C:\Games\Midnight Rally 2009\MidnightRally.exe"));
    }

    [Fact]
    public void A_crafted_game_folder_on_disk_is_parsed_and_resolved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pcchecker-dll-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "Game.exe"), PeImageBuilder.Build(false, ["KERNEL32.dll", "d3dx9_43.dll", "steam_api.dll"], []));
            File.WriteAllBytes(Path.Combine(folder, "bin", "steam_api.dll"), PeImageBuilder.Build(false, ["KERNEL32.dll"], [], isDll: true));
            File.WriteAllText(Path.Combine(folder, "readme.dll"), "not a program");

            var binaries = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Select(f => (f, PeImportReader.ReadFile(f)))
                .Where(x => x.Item2 is not null)
                .Select(x => new GameBinary(Path.GetRelativePath(folder, x.f), x.Item2!))
                .ToList();
            Assert.Equal(2, binaries.Count);

            var files = Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var imports = GameScanRules.ResolveAll(binaries, files, InSystem, (_, _) => false);
            Assert.Equal(ImportResolution.InGameFolder, imports.Single(i => i.DllName == "steam_api.dll").Resolution);
            Assert.Equal(["directx"], GameScanRules.NeededPackages(imports));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class DemoGameRuntimeServiceTests
{
    [Fact]
    public async Task Demo_machine_matches_the_story_and_installs_change_it()
    {
        var service = new DemoGameRuntimeService();
        var scan = await service.ScanAsync(null, CancellationToken.None);

        DllStatusRow Row(string name) => scan.Dlls.Single(r => r.Dll.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DllStatus.Present, Row("vcruntime140.dll").Status64);
        Assert.Equal(DllStatus.Missing, Row("vcruntime140.dll").Status32);
        Assert.Equal(DllStatus.Missing, Row("d3dx9_43.dll").Status32);
        Assert.Equal(DllStatus.Missing, Row("xinput1_3.dll").Status32);
        Assert.Equal(DllStatus.Missing, Row("OpenAL32.dll").Status64);
        Assert.Equal(PackageState.Missing, scan.Packages.Single(p => p.Package.Id == "netfx3").State);

        var game = await service.ScanGameAsync(DemoGameRuntimeService.DemoGameFolder, null, CancellationToken.None);
        Assert.True(game.Has32Bit);
        foreach (var dll in new[] { "d3dx9_43.dll", "xinput1_3.dll", "msvcr100.dll", "OpenAL32.dll" })
        {
            Assert.Contains(game.Imports, i => i.DllName.Equals(dll, StringComparison.OrdinalIgnoreCase) && i.Resolution == ImportResolution.RuntimeMissing);
        }

        Assert.Contains("directx", game.NeededPackageIds);
        Assert.Contains("vc2010-x86", game.NeededPackageIds);
        Assert.Contains("openal", game.NeededPackageIds);

        foreach (var id in game.NeededPackageIds)
        {
            Assert.True((await service.InstallAsync(RuntimeCatalog.Package(id)!, null, CancellationToken.None)).Success);
        }

        var after = await service.ScanGameAsync(DemoGameRuntimeService.DemoGameFolder, null, CancellationToken.None);
        Assert.Empty(after.NeededPackageIds);
        Assert.False((await service.InstallAsync(RuntimeCatalog.Package("gfwl")!, null, CancellationToken.None)).Success);
    }
}
