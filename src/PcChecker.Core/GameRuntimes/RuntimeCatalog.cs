namespace PcChecker.Core.GameRuntimes;

/// <summary>
/// Every DLL the Missing DLLs page knows, and the official package that provides it. PC Checker never downloads single
/// DLL files: a missing DLL is fixed by installing (or repairing) the whole package from its vendor, which puts the
/// right, signed version in the right folder and keeps it updated by Windows Update where that applies.
/// </summary>
/// <remarks>
/// Sources, checked on 27 October 2026 (every link answered over HTTPS from the vendor's own host, and every installer
/// that was downloaded carried a valid Authenticode signature from the signer listed here):
/// <list type="bullet">
/// <item>Visual C++: Microsoft Learn, "Latest supported Visual C++ Redistributable downloads"
/// (learn.microsoft.com/cpp/windows/latest-supported-vc-redist). The aka.ms/vc14 links redirect to
/// download.visualstudio.microsoft.com; 2012, 2010, 2008 and 2005 come from download.microsoft.com. Silent switches:
/// "Redistributing Visual C++ files" on the same site (2012 and later: /install /quiet /norestart); 2010: /q /norestart;
/// 2008 and 2005: /q. winget ids from github.com/microsoft/winget-pkgs (manifests/m/Microsoft/VCRedist).</item>
/// <item>DirectX: "DirectX End-User Runtimes (June 2010)" on the Microsoft Download Center (id 8109), file
/// directx_Jun2010_redist.exe on download.microsoft.com; extracted with /Q /T:folder and installed with DXSETUP.exe
/// /silent as the DirectX SDK documentation for DirectSetup describes. winget's Microsoft.DirectX is not used because
/// its manifest also lists the Store (MSIX) DirectX runtime, which does not install the D3DX DLLs desktop games need.</item>
/// <item>XNA Framework 4.0: Microsoft Download Center id 20914, xnafx40_redist.msi on download.microsoft.com.</item>
/// <item>OpenAL: openal.org/downloads (Creative Labs), oalinst.zip holding the signed oalinst.exe. The /s switch is
/// the one Steam's own redistributable scripts use; Creative does not document switches.</item>
/// <item>PhysX: NVIDIA's download server us.download.nvidia.com (the same link as winget's Nvidia.PhysX 9.26.0703
/// manifest, switch /s) and the Legacy System Software 9.13.0604 MSI for games built with PhysX 2.7 and older.</item>
/// <item>.NET Framework 3.5 and DirectPlay are Windows optional features (NetFx3, DirectPlay) and are switched on through
/// the Windows Features service, so Windows fetches them from Windows Update itself.</item>
/// </list>
/// </remarks>
public static class RuntimeCatalog
{
    public const string Microsoft = "Microsoft";
    public const string Nvidia = "NVIDIA";
    public const string Creative = "Creative Labs";

    private static readonly string[] MicrosoftSigners = ["Microsoft Corporation"];
    private static readonly string[] NvidiaSigners = ["NVIDIA Corporation"];
    private static readonly string[] CreativeSigners = ["Creative Labs Inc", "Creative Technology Ltd"];

    /// <summary>Hosts Microsoft's runtime downloads come from (aka.ms redirects to the other two).</summary>
    public static readonly IReadOnlyList<string> MicrosoftHosts = ["download.microsoft.com", "aka.ms", "download.visualstudio.microsoft.com"];

    /// <summary>NVIDIA's download server (us.download.nvidia.com is a subdomain).</summary>
    public static readonly IReadOnlyList<string> NvidiaHosts = ["download.nvidia.com"];

    /// <summary>Creative's OpenAL site (www.openal.org is a subdomain).</summary>
    public static readonly IReadOnlyList<string> CreativeHosts = ["openal.org"];

    /// <summary>Every host any download may touch. Nothing outside this list is ever contacted.</summary>
    public static IReadOnlyList<string> VendorHosts { get; } = [.. MicrosoftHosts, .. NvidiaHosts, .. CreativeHosts];

    private const string VcSource = "learn.microsoft.com/cpp/windows/latest-supported-vc-redist; winget-pkgs Microsoft.VCRedist";

    public static IReadOnlyList<RuntimePackage> Packages { get; } = BuildPackages();

    public static IReadOnlyList<KnownDll> Dlls { get; } = BuildDlls();

    private static readonly Dictionary<string, KnownDll> ByName =
        Dlls.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The catalog entry for <paramref name="dllName"/> (any case), or null.</summary>
    public static KnownDll? Find(string? dllName) =>
        dllName is not null && ByName.TryGetValue(dllName.Trim(), out var dll) ? dll : null;

    /// <summary>The package by id, or null.</summary>
    public static RuntimePackage? Package(string id) => Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The package of <paramref name="familyId"/> that provides <paramref name="arch"/>, or null.</summary>
    public static RuntimePackage? PackageFor(string familyId, DllArch arch) =>
        Packages.FirstOrDefault(p => p.FamilyId == familyId && p.Arch == arch)
        ?? Packages.FirstOrDefault(p => p.FamilyId == familyId && p.Arch is null);

    /// <summary>The DLLs a package provides (for its architecture).</summary>
    public static IEnumerable<(KnownDll Dll, DllArch Arch)> DllsOf(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        foreach (var dll in Dlls.Where(d => d.FamilyId == package.FamilyId))
        {
            foreach (var arch in new[] { DllArch.X86, DllArch.X64 })
            {
                if (dll.Has(arch) && (package.Arch is null || package.Arch == arch))
                {
                    yield return (dll, arch);
                }
            }
        }
    }

    private static IReadOnlyList<RuntimePackage> BuildPackages()
    {
        var list = new List<RuntimePackage>
        {
            new("directx", "directx", "DirectX End-User Runtime (June 2010)", Microsoft,
                "The extra DirectX 9, 10 and 11 parts (D3DX, XInput, XAudio, XACT) that older games were built with. Windows only includes the core of DirectX.",
                null, RuntimeInstallKind.Installer)
            {
                Download = new RuntimeDownload(
                    "https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe",
                    "directx_Jun2010_redist.exe", InstallerFormat.DirectXRedist, ["/Q", "/T:" + ExtractFolderToken],
                    "DXSETUP.exe", ["/silent"], 120L * 1024 * 1024),
                InfoUrl = "https://www.microsoft.com/download/details.aspx?id=8109",
                ExpectedSigners = MicrosoftSigners,
                AllowedHosts = MicrosoftHosts,
                VersionText = "9.29.1974 (June 2010)",
                SourceNote = "Microsoft Download Center id 8109; DXSETUP /silent per the DirectX SDK DirectSetup docs.",
            },
        };

        // Visual C++: one package per year and architecture. Order: newest first, as people look for those most.
        list.AddRange(Vc("vc14", "Visual C++ 2015-2022 Redistributable", "14.x (latest)",
            "The C and C++ runtime used by most games and apps made since 2015 (vcruntime140, msvcp140).",
            "Microsoft.VCRedist.2015+", "https://aka.ms/vc14/vc_redist.{0}.exe", "vc_redist.{0}.exe", ["/install", "/quiet", "/norestart"]));
        list.AddRange(Vc("vc2013", "Visual C++ 2013 Redistributable", "12.0.40664",
            "The C and C++ runtime for programs made with Visual Studio 2013 (msvcr120, msvcp120).",
            "Microsoft.VCRedist.2013", "https://aka.ms/highdpimfc2013{0}enu", "vcredist_{0}.exe", ["/install", "/quiet", "/norestart"]));
        list.AddRange(Vc("vc2012", "Visual C++ 2012 Redistributable (Update 4)", "11.0.61030",
            "The C and C++ runtime for programs made with Visual Studio 2012 (msvcr110, msvcp110).",
            "Microsoft.VCRedist.2012", "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_{0}.exe",
            "vcredist_{0}.exe", ["/install", "/quiet", "/norestart"]));
        list.AddRange(Vc("vc2010", "Visual C++ 2010 Redistributable (SP1)", "10.0.40219",
            "The C and C++ runtime for programs made with Visual Studio 2010 (msvcr100, msvcp100). Very common in games from 2010 to 2013.",
            "Microsoft.VCRedist.2010", "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_{0}.exe",
            "vcredist_{0}.exe", ["/q", "/norestart"]));
        list.AddRange(Vc("vc2008", "Visual C++ 2008 Redistributable (SP1)", "9.0.30729.6161",
            "The C and C++ runtime for programs made with Visual Studio 2008 (msvcr90, msvcp90), kept in Windows' side-by-side store.",
            "Microsoft.VCRedist.2008", "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_{0}.exe",
            "vcredist_{0}.exe", ["/q"]));
        list.AddRange(Vc("vc2005", "Visual C++ 2005 Redistributable (SP1)", "8.0.61001",
            "The C and C++ runtime for programs made with Visual Studio 2005 (msvcr80, msvcp80), kept in Windows' side-by-side store.",
            "Microsoft.VCRedist.2005", "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_{0}.EXE",
            "vcredist_{0}.exe", ["/q"]));

        list.Add(new("vc2003", "vc2003", "Visual C++ 2003 runtime (msvcr71)", Microsoft,
            "The runtime of Visual Studio .NET 2003, used by some games from 2003 to 2006.", DllArch.X86, RuntimeInstallKind.NotAvailable)
        {
            UnavailableReason = "Not available from Microsoft any more. Reinstall the game: it usually ships this file in its own folder.",
        });

        list.Add(new("netfx3", "netfx3", ".NET Framework 3.5 (includes 2.0 and 3.0)", Microsoft,
            "The older .NET runtime some games, launchers and mod tools still ask for. It is part of Windows and only needs to be switched on.",
            null, RuntimeInstallKind.WindowsFeature)
        {
            FeatureName = "NetFx3",
            ExpectedSigners = MicrosoftSigners,
            SourceNote = "Windows optional feature NetFx3; Windows downloads it from Windows Update.",
        });

        list.Add(new("directplay", "directplay", "DirectPlay", Microsoft,
            "Old multiplayer networking used by games from around 1998 to 2005. Part of Windows; it only needs to be switched on.",
            null, RuntimeInstallKind.WindowsFeature)
        {
            FeatureName = "DirectPlay",
            ExpectedSigners = MicrosoftSigners,
            SourceNote = "Windows optional feature DirectPlay (under Legacy Components).",
        });

        list.Add(new("xna4", "xna4", "XNA Framework 4.0 Redistributable", Microsoft,
            "Runs games made with Microsoft XNA (many indie games from 2010 to 2014).", DllArch.X86, RuntimeInstallKind.Installer)
        {
            Download = new RuntimeDownload(
                "https://download.microsoft.com/download/A/C/2/AC2C903B-E6E8-42C2-9FD7-BEBAC362A930/xnafx40_redist.msi",
                "xnafx40_redist.msi", InstallerFormat.Msi, ["/qn", "/norestart"], MaxBytes: 32L * 1024 * 1024),
            InfoUrl = "https://www.microsoft.com/download/details.aspx?id=20914",
            ExpectedSigners = MicrosoftSigners,
            AllowedHosts = MicrosoftHosts,
            VersionText = "4.0",
            SourceNote = "Microsoft Download Center id 20914.",
        });

        list.Add(new("openal", "openal", "OpenAL (Creative)", Creative,
            "3D sound library used by many older games (OpenAL32.dll).", null, RuntimeInstallKind.Installer)
        {
            Download = new RuntimeDownload("https://www.openal.org/downloads/oalinst.zip", "oalinst.zip", InstallerFormat.ZipWithExe, [],
                "oalinst.exe", ["/s"], 16L * 1024 * 1024),
            InfoUrl = "https://www.openal.org/downloads/",
            ExpectedSigners = CreativeSigners,
            AllowedHosts = CreativeHosts,
            VersionText = "2.0.7.0",
            SourceNote = "openal.org/downloads (Creative Labs).",
        });

        list.Add(new("physx", "physx", "NVIDIA PhysX System Software", Nvidia,
            "Physics effects runtime used by games with NVIDIA PhysX (PhysXLoader.dll). Works on any graphics card.", null, RuntimeInstallKind.Installer)
        {
            WingetId = "Nvidia.PhysX",
            Download = new RuntimeDownload("https://us.download.nvidia.com/Windows/9.26.0703/PhysX_9.26.0703_SystemSoftware.exe",
                "PhysX_9.26.0703_SystemSoftware.exe", InstallerFormat.Exe, ["/s"], MaxBytes: 128L * 1024 * 1024),
            InfoUrl = "https://www.nvidia.com/en-us/drivers/physx/physx-9-26-0703-driver/",
            ExpectedSigners = NvidiaSigners,
            AllowedHosts = NvidiaHosts,
            VersionText = "9.26.0703",
            SourceNote = "us.download.nvidia.com, the same file as winget's Nvidia.PhysX 9.26.0703 manifest.",
        });

        list.Add(new("physx-legacy", "physx-legacy", "NVIDIA PhysX Legacy System Software", Nvidia,
            "The older PhysX parts (2.7 and before) that games from 2006 to 2009 need, such as NxCooking.dll.", DllArch.X86, RuntimeInstallKind.Installer)
        {
            Download = new RuntimeDownload("https://us.download.nvidia.com/Windows/9.13.0604/PhysX-9.13.0604-SystemSoftware-Legacy.msi",
                "PhysX-9.13.0604-SystemSoftware-Legacy.msi", InstallerFormat.Msi, ["/qn", "/norestart"], MaxBytes: 64L * 1024 * 1024),
            InfoUrl = "https://www.nvidia.com/en-us/drivers/physx/physx-9-13-0604-legacy-driver/",
            ExpectedSigners = NvidiaSigners,
            AllowedHosts = NvidiaHosts,
            VersionText = "9.13.0604",
            SourceNote = "us.download.nvidia.com, PhysX Legacy System Software 9.13.0604.",
        });

        list.Add(new("gfwl", "gfwl", "Games for Windows Live (xlive.dll)", Microsoft,
            "Microsoft's old online service for PC games. It was shut down in 2014.", DllArch.X86, RuntimeInstallKind.NotAvailable)
        {
            UnavailableReason = "Microsoft no longer offers it. Use the game's own patch or the current store version, which usually removed it.",
        });

        list.Add(new("mediafeaturepack", "mediafeaturepack", "Media Feature Pack", Microsoft,
            "Video and audio playback parts that Windows N editions leave out. Games that play videos need them.", null, RuntimeInstallKind.WindowsSettings)
        {
            InfoUrl = "ms-settings:optionalfeatures",
            UnavailableReason = "Install it in Settings → System → Optional features → Media Feature Pack.",
        });

        list.Add(new("graphics-driver", "graphics-driver", "Graphics driver (Vulkan)", "Your graphics card maker",
            "Vulkan comes with the graphics driver from NVIDIA, AMD or Intel.", null, RuntimeInstallKind.GraphicsDriver)
        {
            UnavailableReason = "Comes with the graphics driver. Update it on the Drivers page.",
        });

        list.Add(new("windows", "windows", "Part of Windows", Microsoft,
            "Core Windows DLLs such as d3d9.dll, ddraw.dll and xinput1_4.dll. They are always there.", null, RuntimeInstallKind.PartOfWindows)
        {
            UnavailableReason = "Part of Windows. If one is missing, Windows needs a repair (run sfc /scannow as administrator).",
        });

        return list;
    }

    /// <summary>The placeholder in DirectX's arguments that is replaced with the extraction folder.</summary>
    public const string ExtractFolderToken = "{extract}";

    private static IEnumerable<RuntimePackage> Vc(string family, string name, string version, string purpose, string wingetBase,
        string urlPattern, string filePattern, string[] arguments)
    {
        foreach (var (arch, tag) in new[] { (DllArch.X64, "x64"), (DllArch.X86, "x86") })
        {
            yield return new RuntimePackage($"{family}-{tag}", family, $"{name} ({(arch == DllArch.X64 ? "64-bit" : "32-bit")})", Microsoft, purpose,
                arch, RuntimeInstallKind.Installer)
            {
                WingetId = $"{wingetBase}.{tag}",
                Download = new RuntimeDownload(string.Format(System.Globalization.CultureInfo.InvariantCulture, urlPattern, tag),
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, filePattern, tag), InstallerFormat.Exe, arguments, MaxBytes: 64L * 1024 * 1024),
                ExpectedSigners = MicrosoftSigners,
                AllowedHosts = MicrosoftHosts,
                VersionText = version,
                SourceNote = VcSource,
            };
        }
    }

    private static IReadOnlyList<KnownDll> BuildDlls()
    {
        var list = new List<KnownDll>();

        void Add(string family, ArchSet archs, params string[] names)
        {
            foreach (var n in names)
            {
                list.Add(new KnownDll(n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? n : n + ".dll", family, archs));
            }
        }

        void Range(string family, string prefix, int from, int to, ArchSet archs = ArchSet.Both)
        {
            for (int i = from; i <= to; i++)
            {
                Add(family, archs, prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        void Sxs(string family, string assembly, params string[] names)
        {
            foreach (var n in names)
            {
                list.Add(new KnownDll(n + ".dll", family, ArchSet.Both, DllLocation.WinSxS, assembly));
            }
        }

        // DirectX June 2010 (every D3DX, XInput, XAudio, XACT and X3DAudio version it carries, 32 and 64-bit).
        Range("directx", "d3dx9_", 24, 43);
        Add("directx", ArchSet.Both, "d3dx10");
        Range("directx", "d3dx10_", 33, 43);
        Range("directx", "d3dx11_", 42, 43);
        Range("directx", "d3dcompiler_", 33, 43);
        Range("directx", "d3dcsx_", 42, 43);
        Range("directx", "xinput1_", 1, 3);
        Range("directx", "x3daudio1_", 0, 7);
        Range("directx", "xapofx1_", 0, 5);
        Range("directx", "xactengine2_", 0, 10);
        Range("directx", "xactengine3_", 0, 7);
        Range("directx", "xaudio2_", 0, 7);

        // Visual C++ 2015-2022 (v14). vcruntime140_1 only exists for 64-bit programs.
        Add("vc14", ArchSet.Both, "vcruntime140", "vcruntime140_threads", "msvcp140", "msvcp140_1", "msvcp140_2", "msvcp140_atomic_wait",
            "msvcp140_codecvt_ids", "concrt140", "vccorlib140", "vcomp140", "mfc140", "mfc140u", "mfcm140", "mfcm140u");
        Add("vc14", ArchSet.X64, "vcruntime140_1");
        Add("vc2013", ArchSet.Both, "msvcr120", "msvcp120", "mfc120", "mfc120u", "mfcm120", "mfcm120u", "vcomp120", "vccorlib120", "vcamp120");
        Add("vc2012", ArchSet.Both, "msvcr110", "msvcp110", "mfc110", "mfc110u", "mfcm110", "mfcm110u", "vcomp110", "vccorlib110", "vcamp110", "atl110");
        Add("vc2010", ArchSet.Both, "msvcr100", "msvcp100", "mfc100", "mfc100u", "mfcm100", "mfcm100u", "vcomp100", "atl100");
        Sxs("vc2008", "microsoft.vc90.crt", "msvcr90", "msvcp90", "msvcm90");
        Sxs("vc2008", "microsoft.vc90.mfc", "mfc90", "mfc90u", "mfcm90", "mfcm90u");
        Sxs("vc2008", "microsoft.vc90.atl", "atl90");
        Sxs("vc2008", "microsoft.vc90.openmp", "vcomp90");
        Sxs("vc2005", "microsoft.vc80.crt", "msvcr80", "msvcp80", "msvcm80");
        Sxs("vc2005", "microsoft.vc80.mfc", "mfc80", "mfc80u", "mfcm80", "mfcm80u");
        Sxs("vc2005", "microsoft.vc80.atl", "atl80");
        Sxs("vc2005", "microsoft.vc80.openmp", "vcomp");
        Add("vc2003", ArchSet.X86, "msvcr71", "msvcp71", "mfc71", "mfc71u");

        // .NET Framework 2.0/3.5 runtime files live in the Framework folders, not System32.
        list.Add(new KnownDll("mscorwks.dll", "netfx3", ArchSet.Both, DllLocation.NetFx2));
        list.Add(new KnownDll("mscorjit.dll", "netfx3", ArchSet.Both, DllLocation.NetFx2));

        Add("directplay", ArchSet.Both, "dplayx", "dpnet", "dpwsockx", "dpnhpast", "dpnhupnp", "dpnaddr", "dpnlobby", "dpmodemx", "dpnathlp");

        foreach (var n in new[] { "Microsoft.Xna.Framework", "Microsoft.Xna.Framework.Game", "Microsoft.Xna.Framework.Graphics",
                     "Microsoft.Xna.Framework.Xact", "Microsoft.Xna.Framework.Input.Touch", "Microsoft.Xna.Framework.Avatar",
                     "Microsoft.Xna.Framework.GamerServices", "Microsoft.Xna.Framework.Net", "Microsoft.Xna.Framework.Storage",
                     "Microsoft.Xna.Framework.Video" })
        {
            list.Add(new KnownDll(n + ".dll", "xna4", ArchSet.X86, DllLocation.XnaGac));
        }

        Add("openal", ArchSet.Both, "OpenAL32", "wrap_oal");

        foreach (var (n, a) in new[]
                 {
                     ("PhysXLoader", ArchSet.X86), ("PhysXLoader64", ArchSet.X64), ("PhysXDevice", ArchSet.X86), ("PhysXDevice64", ArchSet.X64),
                     ("PhysXUpdateLoader", ArchSet.X86), ("PhysXUpdateLoader64", ArchSet.X64), ("PhysXCore", ArchSet.X86), ("PhysXCore64", ArchSet.X64),
                     ("PhysXCooking", ArchSet.X86), ("PhysXCooking64", ArchSet.X64), ("cudart32_30_9", ArchSet.X86), ("cudart64_30_9", ArchSet.X64),
                 })
        {
            list.Add(new KnownDll(n + ".dll", "physx", a, DllLocation.PhysX));
        }

        foreach (var n in new[] { "NxCooking", "NxCharacter", "NxExtensions", "NxPhysics" })
        {
            list.Add(new KnownDll(n + ".dll", "physx-legacy", ArchSet.X86, DllLocation.PhysX));
        }

        Add("gfwl", ArchSet.X86, "xlive");
        Add("mediafeaturepack", ArchSet.Both, "mfplat", "mf", "mfreadwrite", "mfplay", "mfcore", "wmvcore", "wmasf", "msmpeg2vdec", "evr");
        Add("graphics-driver", ArchSet.Both, "vulkan-1");

        // Part of every Windows 10 and 11 installation. d3d8, d3dim700 and msvbvm60 only exist for 32-bit programs.
        Add("windows", ArchSet.X86, "d3d8", "d3dim700", "msvbvm60");
        Add("windows", ArchSet.Both, "ddraw", "d3d9", "d3d10", "d3d10_1", "d3d10core", "d3d11", "d3d12", "dxgi", "dinput", "dinput8", "dsound",
            "xinput1_4", "xinput9_1_0", "xaudio2_8", "xaudio2_9", "d3dcompiler_47", "msvcrt", "msvcp60", "mfc42", "mfc42u", "ucrtbase",
            "opengl32", "glu32", "winmm", "dwrite", "d2d1", "msacm32", "mscoree", "gdiplus", "comctl32");

        return list;
    }
}
