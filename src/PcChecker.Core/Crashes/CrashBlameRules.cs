using System.Globalization;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>
/// The "probably caused by" guess for a blue screen, using simple rules in the spirit of BlueScreenView (no symbol
/// files, no stack walking): first a driver whose address range contains the crash address or a stop-code parameter,
/// then the first driver from outside Windows found on the crashing thread's raw stack, then the graphics driver for a
/// graphics stop code. It is a best guess and the page says so.
/// </summary>
public static class CrashBlameRules
{
    /// <summary>Values below this are counts, flags or status codes, never addresses.</summary>
    private const ulong MinAddress = 0x10000;

    private static readonly HashSet<uint> GraphicsCodes = [0x10E, 0x113, 0x116, 0x117, 0x119, 0x141];

    private static readonly string[] GraphicsDrivers = ["nvlddmkm", "amdkmdag", "amdwddmg", "atikmdag", "atikmpag", "igdkmd", "igdkmdn", "igdkmdnd", "igfx"];

    /// <summary>Windows' own kernel modules and common system DLLs; a hit here is rarely the real cause.</summary>
    private static readonly HashSet<string> WindowsModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntoskrnl.exe", "ntkrnlmp.exe", "ntkrnlpa.exe", "ntkrpamp.exe", "ntkrla57.exe", "hal.dll", "kd.dll", "ci.dll", "clfs.sys",
        "tm.sys", "pshed.dll", "bootvid.dll", "fltmgr.sys", "ntfs.sys", "refs.sys", "fastfat.sys", "exfat.sys", "ksecdd.sys",
        "ksecpkg.sys", "cng.sys", "tcpip.sys", "ndis.sys", "netio.sys", "afd.sys", "http.sys", "win32k.sys", "win32kbase.sys",
        "win32kfull.sys", "win32kns.sys", "dxgkrnl.sys", "dxgmms1.sys", "dxgmms2.sys", "watchdog.sys", "basicdisplay.sys",
        "basicrender.sys", "storport.sys", "stornvme.sys", "storahci.sys", "ataport.sys", "classpnp.sys", "disk.sys",
        "partmgr.sys", "volmgr.sys", "volmgrx.sys", "volsnap.sys", "volume.sys", "acpi.sys", "pci.sys", "wdf01000.sys",
        "wdfldr.sys", "usbxhci.sys", "ucx01000.sys", "usbhub3.sys", "usbport.sys", "usbccgp.sys", "hidclass.sys", "hidusb.sys",
        "kbdclass.sys", "mouclass.sys", "wdfilter.sys", "wdboot.sys", "wdnisdrv.sys", "mssecflt.sys", "msrpc.sys",
        "fwpkclnt.sys", "cdd.dll", "peauth.sys", "srv2.sys", "mrxsmb.sys", "rdbss.sys", "npfs.sys", "msfs.sys", "ks.sys",
        "portcls.sys", "hdaudbus.sys", "hdaudio.sys", "intelppm.sys", "amdppm.sys", "werkernel.sys", "fileinfo.sys",
        "wof.sys", "bindflt.sys", "cldflt.sys", "luafv.sys", "storqosflt.sys", "spaceport.sys", "iorate.sys", "mup.sys",
        "nsiproxy.sys", "tdx.sys", "netbt.sys", "vwififlt.sys", "nwifi.sys", "pacer.sys",
        "ntdll.dll", "kernelbase.dll", "kernel32.dll", "ucrtbase.dll", "msvcrt.dll", "user32.dll", "win32u.dll", "gdi32.dll",
        "gdi32full.dll", "combase.dll", "rpcrt4.dll", "ole32.dll", "d3d11.dll", "d3d12.dll", "d3d12core.dll", "dxgi.dll",
        "coreclr.dll", "clr.dll", "kernel.appcore.dll", "windows.ui.xaml.dll", "twinapi.appcore.dll",
    };

    /// <summary>True for a module that ships with Windows (by file name).</summary>
    public static bool IsWindowsModule(string? name) =>
        !string.IsNullOrWhiteSpace(name) && WindowsModules.Contains(FileName(name));

    /// <summary>The file name of a Windows path, on any platform (the tests run on Linux).</summary>
    private static string FileName(string path)
    {
        var trimmed = path.Trim();
        int slash = trimmed.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>The module whose address range contains <paramref name="address"/>, or null.</summary>
    public static CrashModule? FindModule(IReadOnlyList<CrashModule> modules, ulong address)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (address < MinAddress)
        {
            return null;
        }

        foreach (var m in modules)
        {
            if (m.Contains(address))
            {
                return m;
            }
        }

        return null;
    }

    /// <summary>True for a graphics driver file name (nvlddmkm.sys, amdkmdag.sys, igdkmd64.sys…).</summary>
    public static bool IsGraphicsDriver(string? name) =>
        !string.IsNullOrWhiteSpace(name) && GraphicsDrivers.Any(g => name.Contains(g, StringComparison.OrdinalIgnoreCase));

    /// <summary>The best guess, or null when nothing points anywhere.</summary>
    public static CrashBlame? Guess(uint? bugCheckCode, IReadOnlyList<ulong> parameters, ulong? exceptionAddress,
        IReadOnlyList<CrashModule> modules, IReadOnlyList<ulong> stack)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(stack);
        if (modules.Count == 0)
        {
            return null;
        }

        // 1. The crash address or a stop-code parameter inside a driver.
        var pointed = new List<(CrashModule Module, string Reason)>();
        if (exceptionAddress is { } ea && FindModule(modules, ea) is { } atCrash)
        {
            pointed.Add((atCrash, T("The crash happened inside this file.")));
        }

        for (int i = 0; i < parameters.Count; i++)
        {
            if (FindModule(modules, parameters[i]) is { } m)
            {
                pointed.Add((m, string.Format(CultureInfo.InvariantCulture, T("Stop code parameter {0} points inside this file."), i + 1)));
            }
        }

        if (pointed.FirstOrDefault(p => !IsWindowsModule(p.Module.Name)) is { Module: not null } outside)
        {
            return new CrashBlame(outside.Module.Name, BlameConfidence.Medium, outside.Reason, false);
        }

        // 2. The first driver from outside Windows on the crashing thread's stack.
        foreach (var value in stack)
        {
            if (FindModule(modules, value) is { } m && !IsWindowsModule(m.Name))
            {
                return new CrashBlame(m.Name, BlameConfidence.Low, T("This driver was on the crashing thread's stack."), false);
            }
        }

        // 3. A graphics stop code: the graphics driver that was loaded.
        if (bugCheckCode is { } code && GraphicsCodes.Contains(code) && modules.FirstOrDefault(m => IsGraphicsDriver(m.Name)) is { } gpu)
        {
            return new CrashBlame(gpu.Name, BlameConfidence.Low, T("This is a graphics stop code and this is the graphics driver."), false);
        }

        // 4. Only Windows was pointed at: say so, the real cause is usually a driver that called it.
        if (pointed.Count > 0)
        {
            var (module, reason) = pointed[0];
            return new CrashBlame(module.Name, BlameConfidence.Low, reason + " " + T("It is part of Windows, so the real cause is often a driver that called it."), true);
        }

        return null;
    }
}
