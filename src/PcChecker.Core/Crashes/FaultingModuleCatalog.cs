using System.Text.RegularExpressions;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>What kind of file a faulting module is.</summary>
public enum ModuleCategory
{
    /// <summary>The graphics card maker's driver (the part that runs inside the program).</summary>
    GraphicsDriver,

    /// <summary>Windows' own DirectX or graphics files.</summary>
    WindowsGraphics,

    /// <summary>A Visual C++ runtime file.</summary>
    VisualCppRuntime,

    /// <summary>A file from the old DirectX End-User Runtime (D3DX, XInput 1.3, XAudio 2.7…).</summary>
    DirectXRuntime,

    /// <summary>.NET.</summary>
    DotNet,

    /// <summary>A core Windows file that usually only reports someone else's error.</summary>
    WindowsCore,

    /// <summary>An overlay or recording tool that hooks into games.</summary>
    Overlay,

    /// <summary>Anti-cheat software.</summary>
    AntiCheat,

    /// <summary>Antivirus or security software that hooks into programs.</summary>
    Security,

    /// <summary>Audio software or effects.</summary>
    Audio,

    /// <summary>Game controller or input files.</summary>
    Input,

    /// <summary>A graphics mod or mod loader (ReShade, ENB, ASI loaders).</summary>
    Mod,

    /// <summary>A library that ships with the game (engine, video, sound, upscaler, launcher API).</summary>
    GameFile,
}

/// <summary>One known faulting module: what it belongs to and what to do about it.</summary>
public sealed record FaultingModuleInfo(string Pattern, ModuleCategory Category, string Owner, string Advice, CrashFixKind Fix)
{
    /// <summary>The Missing DLLs package family to reinstall (vc14, directx, physx…), when the fix is a runtime.</summary>
    public string? RuntimeFamily { get; init; }
}

/// <summary>
/// About 120 files that often show up as the "faulting module" of an app crash, from graphics drivers and runtimes to
/// overlays, anti-cheat and mods, with what they belong to and the fix. Lookups ignore case and match versioned names
/// (d3dx9_43.dll, msvcp140_1.dll, xinput1_3.dll). A few special cases depend on the folder or exception code: a
/// dxgi.dll in a game folder is ReShade or another mod, not Windows; KERNELBASE.dll with 0xe0434352 is a .NET
/// exception; ntdll.dll with 0xc0000374 is heap corruption. Pure.
/// </summary>
public static class FaultingModuleCatalog
{
    private static readonly string GpuAdvice = T("Update the graphics driver; if it keeps crashing, do a clean install of it (or go back one version if this started after an update).");
    private static readonly string WindowsGraphicsAdvice = T("This is Windows' DirectX. The real cause is usually the graphics driver or an overlay: update the graphics driver and Windows.");
    private static readonly string VcAdvice = T("Repair the Visual C++ runtimes (Missing DLLs page) and verify the game's or program's files.");
    private static readonly string DxAdvice = T("Install the DirectX End-User Runtime (Missing DLLs page); it adds the old DirectX parts games still use.");
    private static readonly string OverlayAdvice = T("Turn this overlay off as a test (in its own settings) and see whether the crashes stop.");
    private static readonly string AntiCheatAdvice = T("Repair the anti-cheat: verify the game's files in its launcher (that reinstalls the anti-cheat), or run the anti-cheat's own repair.");
    private static readonly string SecurityAdvice = T("This is antivirus software hooking into the program. Update it, or add the game to its exclusions as a test.");
    private static readonly string AudioAdvice = T("Audio effects software is known to conflict with games: update or turn off its effects as a test, and update the audio driver.");
    private static readonly string InputAdvice = T("Unplug extra controllers, close controller tools (DS4Windows, Steam Input, macro software) as a test and update the game.");
    private static readonly string ModAdvice = T("This is a mod or graphics injector (ReShade, ENB, an ASI loader) in the game folder. Remove or update it as a test.");
    private static readonly string GameFileAdvice = T("This file ships with the game. Verify the game's files in its launcher and update the game.");

    public static IReadOnlyList<FaultingModuleInfo> All { get; } =
    [
        // NVIDIA user-mode drivers.
        new("nvwgf2um*.dll", ModuleCategory.GraphicsDriver, T("NVIDIA graphics driver (DirectX 10/11/12)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("nvd3dum*.dll", ModuleCategory.GraphicsDriver, T("NVIDIA graphics driver (DirectX 9)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("nvoglv*.dll", ModuleCategory.GraphicsDriver, T("NVIDIA graphics driver (OpenGL)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("nvcuda*.dll", ModuleCategory.GraphicsDriver, T("NVIDIA graphics driver (CUDA)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("nvgpucomp*.dll", ModuleCategory.GraphicsDriver, T("NVIDIA graphics driver (shader compiler)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("nvapi*.dll", ModuleCategory.GraphicsDriver, T("NVIDIA graphics driver (NVAPI)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("nvldumd*.dll", ModuleCategory.GraphicsDriver, T("NVIDIA graphics driver (loader)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("nvumdshim*.dll", ModuleCategory.GraphicsDriver, T("NVIDIA graphics driver (Optimus shim)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("nvwgf2umx_cfg.dll", ModuleCategory.GraphicsDriver, T("NVIDIA graphics driver"), GpuAdvice, CrashFixKind.UpdateDriver),

        // AMD user-mode drivers.
        new("amdxx*.dll", ModuleCategory.GraphicsDriver, T("AMD graphics driver (DirectX 12)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("atidxx*.dll", ModuleCategory.GraphicsDriver, T("AMD graphics driver (DirectX 11)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("aticfx*.dll", ModuleCategory.GraphicsDriver, T("AMD graphics driver (DirectX)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("atiumd*.dll", ModuleCategory.GraphicsDriver, T("AMD graphics driver (DirectX 9)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("amdvlk*.dll", ModuleCategory.GraphicsDriver, T("AMD graphics driver (Vulkan)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("atio6axx.dll", ModuleCategory.GraphicsDriver, T("AMD graphics driver (OpenGL)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("amdxc*.dll", ModuleCategory.GraphicsDriver, T("AMD graphics driver (shader compiler)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("amdihk*.dll", ModuleCategory.GraphicsDriver, T("AMD graphics driver (in-game hooks)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("atiadlxx.dll", ModuleCategory.GraphicsDriver, T("AMD graphics driver (ADL)"), GpuAdvice, CrashFixKind.UpdateDriver),

        // Intel user-mode drivers.
        new("igd10iumd*.dll", ModuleCategory.GraphicsDriver, T("Intel graphics driver (DirectX 11)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("igd12umd*.dll", ModuleCategory.GraphicsDriver, T("Intel graphics driver (DirectX 12)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("igdumdim*.dll", ModuleCategory.GraphicsDriver, T("Intel graphics driver (DirectX 9)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("igxelpicd*.dll", ModuleCategory.GraphicsDriver, T("Intel graphics driver (OpenGL)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("igvk*.dll", ModuleCategory.GraphicsDriver, T("Intel graphics driver (Vulkan)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("igc*.dll", ModuleCategory.GraphicsDriver, T("Intel graphics driver (shader compiler)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("igdusc*.dll", ModuleCategory.GraphicsDriver, T("Intel graphics driver (shader compiler)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("vulkan-1.dll", ModuleCategory.GraphicsDriver, T("Vulkan loader (installed with the graphics driver)"), GpuAdvice, CrashFixKind.UpdateDriver),
        new("GFSDK_Aftermath_Lib*.dll", ModuleCategory.GraphicsDriver, T("NVIDIA Aftermath crash reporter (the GPU crashed)"), GpuAdvice, CrashFixKind.UpdateDriver),

        // Windows graphics.
        new("d3d11.dll", ModuleCategory.WindowsGraphics, T("Windows DirectX 11"), WindowsGraphicsAdvice, CrashFixKind.UpdateDriver),
        new("d3d12.dll", ModuleCategory.WindowsGraphics, T("Windows DirectX 12"), WindowsGraphicsAdvice, CrashFixKind.UpdateDriver),
        new("d3d12core.dll", ModuleCategory.WindowsGraphics, T("Windows DirectX 12"), WindowsGraphicsAdvice, CrashFixKind.UpdateDriver),
        new("dxgi.dll", ModuleCategory.WindowsGraphics, T("Windows DirectX graphics infrastructure"), WindowsGraphicsAdvice, CrashFixKind.UpdateDriver),
        new("d3d9.dll", ModuleCategory.WindowsGraphics, T("Windows DirectX 9"), WindowsGraphicsAdvice, CrashFixKind.UpdateDriver),
        new("dxcore.dll", ModuleCategory.WindowsGraphics, T("Windows DirectX core"), WindowsGraphicsAdvice, CrashFixKind.UpdateDriver),
        new("opengl32.dll", ModuleCategory.WindowsGraphics, T("Windows OpenGL"), WindowsGraphicsAdvice, CrashFixKind.UpdateDriver),
        new("d3dcompiler_47.dll", ModuleCategory.WindowsGraphics, T("Windows shader compiler"), WindowsGraphicsAdvice, CrashFixKind.UpdateDriver),

        // Visual C++ runtimes.
        new("ucrtbase.dll", ModuleCategory.VisualCppRuntime, T("Universal C runtime (part of Windows)"), T("The program passed bad data to the C runtime; this is usually a program bug. Repair the Visual C++ runtimes and Windows files, and update the program."), CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc14" },
        new("msvcp140*.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2015-2022 runtime"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc14" },
        new("vcruntime140*.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2015-2022 runtime"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc14" },
        new("concrt140.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2015-2022 runtime"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc14" },
        new("vcomp140.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2015-2022 runtime (OpenMP)"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc14" },
        new("mfc140*.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2015-2022 runtime (MFC)"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc14" },
        new("msvc?120.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2013 runtime"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc2013" },
        new("msvc?110.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2012 runtime"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc2012" },
        new("msvc?100.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2010 runtime"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc2010" },
        new("msvc?90.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2008 runtime"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc2008" },
        new("msvc?80.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2005 runtime"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc2005" },
        new("msvcr71.dll", ModuleCategory.VisualCppRuntime, T("Visual C++ 2003 runtime"), VcAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "vc2003" },
        new("msvcrt.dll", ModuleCategory.WindowsCore, T("Windows C runtime"), T("The error is in the program's own use of the C runtime. Update or reinstall the program."), CrashFixKind.UpdateApp),

        // DirectX End-User Runtime.
        new("d3dx9_*.dll", ModuleCategory.DirectXRuntime, T("DirectX 9 extensions (D3DX9)"), DxAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "directx" },
        new("d3dx10*.dll", ModuleCategory.DirectXRuntime, T("DirectX 10 extensions (D3DX10)"), DxAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "directx" },
        new("d3dx11_*.dll", ModuleCategory.DirectXRuntime, T("DirectX 11 extensions (D3DX11)"), DxAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "directx" },
        new("d3dcompiler_4?.dll", ModuleCategory.DirectXRuntime, T("Old DirectX shader compiler"), DxAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "directx" },
        new("xinput1_3.dll", ModuleCategory.DirectXRuntime, T("XInput 1.3 controller support (DirectX runtime)"), DxAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "directx" },
        new("xinput1_4.dll", ModuleCategory.Input, T("Windows XInput controller support"), InputAdvice, CrashFixKind.UpdateApp),
        new("xinput9_1_0.dll", ModuleCategory.Input, T("Windows XInput controller support"), InputAdvice, CrashFixKind.UpdateApp),
        new("xaudio2_7.dll", ModuleCategory.DirectXRuntime, T("XAudio 2.7 sound (DirectX runtime)"), DxAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "directx" },
        new("xaudio2_[0-6].dll", ModuleCategory.DirectXRuntime, T("Old XAudio sound (DirectX runtime)"), DxAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "directx" },
        new("xaudio2_9*.dll", ModuleCategory.Audio, T("Windows XAudio 2.9 sound"), AudioAdvice, CrashFixKind.UpdateDriver),
        new("xaudio2_8.dll", ModuleCategory.Audio, T("Windows XAudio 2.8 sound"), AudioAdvice, CrashFixKind.UpdateDriver),
        new("x3daudio1_*.dll", ModuleCategory.DirectXRuntime, T("X3DAudio (DirectX runtime)"), DxAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "directx" },
        new("xapofx1_*.dll", ModuleCategory.DirectXRuntime, T("XAPOFX sound effects (DirectX runtime)"), DxAdvice, CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "directx" },

        // .NET.
        new("clr.dll", ModuleCategory.DotNet, T(".NET Framework runtime"), T("Repair .NET Framework (Microsoft's .NET Framework Repair Tool) and update the program."), CrashFixKind.RepairDotNet),
        new("coreclr.dll", ModuleCategory.DotNet, T(".NET runtime"), T("Install the latest .NET runtime the program needs and update the program."), CrashFixKind.RepairDotNet),
        new("clrjit.dll", ModuleCategory.DotNet, T(".NET runtime (JIT compiler)"), T("Repair .NET and update the program; if many .NET programs crash, test the memory."), CrashFixKind.RepairDotNet),
        new("mscorwks.dll", ModuleCategory.DotNet, T(".NET Framework 2.0/3.5 runtime"), T("Repair .NET Framework 3.5 (Windows Features or the Missing DLLs page) and update the program."), CrashFixKind.RepairDotNet) { RuntimeFamily = "netfx3" },
        new("mscoree.dll", ModuleCategory.DotNet, T(".NET runtime loader"), T("Repair .NET and update the program."), CrashFixKind.RepairDotNet),

        // Windows core (usually only reports the error).
        new("ntdll.dll", ModuleCategory.WindowsCore, T("Windows core (ntdll)"), T("ntdll.dll is where Windows noticed the problem, not usually the cause: update the program and turn off overlays and mods."), CrashFixKind.UpdateApp),
        new("kernelbase.dll", ModuleCategory.WindowsCore, T("Windows core (KernelBase)"), T("KernelBase.dll is where the program reported its own error. Update or repair the program."), CrashFixKind.UpdateApp),
        new("kernel32.dll", ModuleCategory.WindowsCore, T("Windows core (kernel32)"), T("The program reported its own error through Windows. Update or repair the program."), CrashFixKind.UpdateApp),
        new("combase.dll", ModuleCategory.WindowsCore, T("Windows COM"), T("Usually a program or add-on bug. Update the program; repair Windows files if many programs crash here."), CrashFixKind.UpdateApp),
        new("user32.dll", ModuleCategory.WindowsCore, T("Windows window manager"), T("Usually a program, overlay or input hook bug. Update the program and turn off overlays."), CrashFixKind.DisableOverlay),
        new("AcLayers.dll", ModuleCategory.WindowsCore, T("Windows compatibility fixes"), T("A compatibility mode is applied to this program. Turn compatibility mode off in the program's Properties."), CrashFixKind.UpdateApp),
        new("AudioSes.dll", ModuleCategory.Audio, T("Windows audio session"), T("Update the audio driver, unplug and re-plug USB headsets, and turn off audio effects."), CrashFixKind.UpdateDriver),

        // Overlays and capture tools.
        new("DiscordHook*.dll", ModuleCategory.Overlay, T("Discord overlay"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("GameOverlayRenderer*.dll", ModuleCategory.Overlay, T("Steam overlay"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("RTSSHooks*.dll", ModuleCategory.Overlay, T("RivaTuner Statistics Server (MSI Afterburner overlay)"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("obs-graphics-hook*.dll", ModuleCategory.Overlay, T("OBS Studio game capture"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("graphics-hook*.dll", ModuleCategory.Overlay, T("OBS Studio game capture"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("nvspcap*.dll", ModuleCategory.Overlay, T("NVIDIA overlay / ShadowPlay capture"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("EOSOverlayRenderer*.dll", ModuleCategory.Overlay, T("Epic Games overlay"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("igo*.dll", ModuleCategory.Overlay, T("EA app in-game overlay"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("uplay_overlay*.dll", ModuleCategory.Overlay, T("Ubisoft Connect overlay"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("ow-graphics-hook*.dll", ModuleCategory.Overlay, T("Overwolf overlay"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("fraps*.dll", ModuleCategory.Overlay, T("Fraps"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("bdcap*.dll", ModuleCategory.Overlay, T("Bandicam capture"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("action_x*.dll", ModuleCategory.Overlay, T("Mirillis Action! capture"), OverlayAdvice, CrashFixKind.DisableOverlay),
        new("Gamebar*.dll", ModuleCategory.Overlay, T("Xbox Game Bar"), OverlayAdvice, CrashFixKind.DisableOverlay),

        // Anti-cheat.
        new("EasyAntiCheat*.dll", ModuleCategory.AntiCheat, T("Easy Anti-Cheat"), AntiCheatAdvice, CrashFixKind.VerifyGameFiles),
        new("BEClient*.dll", ModuleCategory.AntiCheat, T("BattlEye anti-cheat"), AntiCheatAdvice, CrashFixKind.VerifyGameFiles),
        new("vgk.sys", ModuleCategory.AntiCheat, T("Riot Vanguard anti-cheat"), T("Repair or reinstall Riot Vanguard (uninstall it in Apps, then start the game to reinstall it)."), CrashFixKind.UninstallRecentProgram),
        new("vgc.exe", ModuleCategory.AntiCheat, T("Riot Vanguard anti-cheat"), T("Repair or reinstall Riot Vanguard (uninstall it in Apps, then start the game to reinstall it)."), CrashFixKind.UninstallRecentProgram),
        new("EOSSDK-Win*.dll", ModuleCategory.GameFile, T("Epic Online Services (shipped with the game)"), GameFileAdvice, CrashFixKind.VerifyGameFiles),

        // Security software hooks.
        new("aswhook.dll", ModuleCategory.Security, T("Avast / AVG antivirus"), SecurityAdvice, CrashFixKind.UninstallRecentProgram),
        new("atcuf*.dll", ModuleCategory.Security, T("Bitdefender antivirus"), SecurityAdvice, CrashFixKind.UninstallRecentProgram),
        new("sysfer.dll", ModuleCategory.Security, T("Symantec / Norton antivirus"), SecurityAdvice, CrashFixKind.UninstallRecentProgram),
        new("umppc*.dll", ModuleCategory.Security, T("CrowdStrike Falcon"), SecurityAdvice, CrashFixKind.UninstallRecentProgram),

        // Audio software.
        new("Nahimic*.dll", ModuleCategory.Audio, T("Nahimic audio (MSI, Dell, Lenovo…)"), AudioAdvice, CrashFixKind.UninstallRecentProgram),
        new("*SonicStudio*.dll", ModuleCategory.Audio, T("ASUS Sonic Studio audio"), AudioAdvice, CrashFixKind.UninstallRecentProgram),
        new("RtkApo*.dll", ModuleCategory.Audio, T("Realtek audio effects"), AudioAdvice, CrashFixKind.UpdateDriver),
        new("MaxxAudio*.dll", ModuleCategory.Audio, T("Waves MaxxAudio"), AudioAdvice, CrashFixKind.UninstallRecentProgram),

        // Input.
        new("hid.dll", ModuleCategory.Input, T("Windows game controller and HID support"), InputAdvice, CrashFixKind.UpdateApp),
        new("SDL2.dll", ModuleCategory.Input, T("SDL (controllers, window and input library shipped with the game)"), InputAdvice, CrashFixKind.VerifyGameFiles),
        new("SDL3.dll", ModuleCategory.Input, T("SDL (controllers, window and input library shipped with the game)"), InputAdvice, CrashFixKind.VerifyGameFiles),
        new("steam_api*.dll", ModuleCategory.GameFile, T("Steam API (shipped with the game)"), GameFileAdvice, CrashFixKind.VerifyGameFiles),
        new("steamclient*.dll", ModuleCategory.GameFile, T("Steam client (Steam Input, overlay, achievements)"), T("Restart Steam, update it, and turn off Steam Input for this game as a test."), CrashFixKind.VerifyGameFiles),
        new("ViGEmClient.dll", ModuleCategory.Input, T("ViGEm virtual controller"), InputAdvice, CrashFixKind.UpdateApp),

        // Mods.
        new("ReShade*.dll", ModuleCategory.Mod, T("ReShade"), ModAdvice, CrashFixKind.VerifyGameFiles),
        new("d3d8to9*.dll", ModuleCategory.Mod, T("d3d8to9 wrapper (a mod)"), ModAdvice, CrashFixKind.VerifyGameFiles),

        // Game libraries.
        new("nvngx_dlss*.dll", ModuleCategory.GameFile, T("NVIDIA DLSS (shipped with the game)"), T("Update the game and the graphics driver; turn DLSS or frame generation off as a test."), CrashFixKind.VerifyGameFiles),
        new("amd_fidelityfx*.dll", ModuleCategory.GameFile, T("AMD FidelityFX / FSR (shipped with the game)"), T("Update the game and the graphics driver; turn FSR or frame generation off as a test."), CrashFixKind.VerifyGameFiles),
        new("libxess*.dll", ModuleCategory.GameFile, T("Intel XeSS (shipped with the game)"), T("Update the game and the graphics driver; turn XeSS off as a test."), CrashFixKind.VerifyGameFiles),
        new("PhysX*.dll", ModuleCategory.GameFile, T("NVIDIA PhysX"), T("Reinstall NVIDIA PhysX (Missing DLLs page) and verify the game's files."), CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "physx" },
        new("bink2w*.dll", ModuleCategory.GameFile, T("Bink video (shipped with the game)"), GameFileAdvice, CrashFixKind.VerifyGameFiles),
        new("fmod*.dll", ModuleCategory.GameFile, T("FMOD sound (shipped with the game)"), GameFileAdvice, CrashFixKind.VerifyGameFiles),
        new("mss*.dll", ModuleCategory.GameFile, T("Miles Sound System (shipped with the game)"), GameFileAdvice, CrashFixKind.VerifyGameFiles),
        new("UnityPlayer.dll", ModuleCategory.GameFile, T("Unity game engine"), GameFileAdvice, CrashFixKind.VerifyGameFiles),
        new("OpenAL32.dll", ModuleCategory.GameFile, T("OpenAL sound"), T("Reinstall OpenAL (Missing DLLs page) and verify the game's files."), CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "openal" },
        new("xlive.dll", ModuleCategory.GameFile, T("Games for Windows Live"), T("Games for Windows Live is discontinued; look for the game's patch that removes it, or reinstall it from the Missing DLLs page."), CrashFixKind.ReinstallRuntimes) { RuntimeFamily = "gfwl" },
        new("libcef.dll", ModuleCategory.GameFile, T("Chromium Embedded (a built-in web view)"), T("Update the program; turn off hardware acceleration in its settings as a test."), CrashFixKind.UpdateApp),
    ];

    // Files that are Windows' own when loaded from the Windows folder, and a mod or wrapper when they sit next to the game.
    private static readonly HashSet<string> ProxyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dxgi.dll", "d3d11.dll", "d3d9.dll", "d3d8.dll", "d3d12.dll", "dinput8.dll", "opengl32.dll", "version.dll", "winmm.dll", "xinput1_3.dll",
    };

    private static readonly FaultingModuleInfo ProxyMod = new("(mod)", ModuleCategory.Mod, T("A mod or graphics injector (ReShade, ENB, an ASI loader) in the game folder"), ModAdvice, CrashFixKind.VerifyGameFiles);

    private static readonly FaultingModuleInfo DotNetException = new("kernelbase.dll", ModuleCategory.DotNet, T(".NET exception reported through Windows"),
        T("The program's .NET code hit an error it did not handle. Update the program and repair .NET."), CrashFixKind.RepairDotNet);

    private static readonly FaultingModuleInfo CppException = new("kernelbase.dll", ModuleCategory.WindowsCore, T("C++ exception reported through Windows"),
        T("The program threw an error it did not handle, often because a file is missing or damaged. Verify or repair the program and its Visual C++ runtimes."), CrashFixKind.VerifyGameFiles) { RuntimeFamily = "vc14" };

    private static readonly FaultingModuleInfo HeapCorruption = new("ntdll.dll", ModuleCategory.WindowsCore, T("Heap corruption noticed by Windows"),
        T("Something inside the program damaged its memory. Mods and overlays are common causes; remove them as a test, and test the RAM if many programs do this."), CrashFixKind.DisableOverlay);

    private static readonly IReadOnlyList<(Regex Regex, FaultingModuleInfo Info)> Compiled =
        All.Select(e => (new Regex("^" + Regex.Escape(e.Pattern).Replace("\\*", ".*").Replace("\\?", ".").Replace("\\[", "[").Replace("]", "]") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), e)).ToList();

    /// <summary>
    /// The entry for a faulting module (file name or path), or null when it is not a known file. <paramref name="modulePath"/>
    /// tells a mod's dxgi.dll in a game folder from Windows' own; <paramref name="exceptionCode"/> refines KernelBase
    /// and ntdll.
    /// </summary>
    public static FaultingModuleInfo? Find(string? module, string? modulePath = null, uint? exceptionCode = null)
    {
        if (string.IsNullOrWhiteSpace(module))
        {
            return null;
        }

        var name = FileName(module);
        if (ProxyNames.Contains(name) && IsOutsideWindows(modulePath ?? module))
        {
            return ProxyMod;
        }

        if (name.Equals("kernelbase.dll", StringComparison.OrdinalIgnoreCase))
        {
            if (exceptionCode == 0xE0434352)
            {
                return DotNetException;
            }

            if (exceptionCode == 0xE06D7363)
            {
                return CppException;
            }
        }

        if (name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase) && exceptionCode == 0xC0000374)
        {
            return HeapCorruption;
        }

        foreach (var (regex, info) in Compiled)
        {
            if (regex.IsMatch(name))
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>True when the path is a full path that is not inside a Windows folder (System32, SysWOW64, WinSxS).</summary>
    public static bool IsOutsideWindows(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!path.Contains('\\') && !path.Contains('/')))
        {
            return false;
        }

        var p = path.Replace('/', '\\');
        return !p.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)
            && !p.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase)
            && !p.Contains(@"\SysWOW64\", StringComparison.OrdinalIgnoreCase);
    }

    private static string FileName(string path)
    {
        var trimmed = path.Trim().Trim('"');
        int slash = trimmed.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}
