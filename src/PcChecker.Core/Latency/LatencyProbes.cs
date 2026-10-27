using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Latency;

/// <summary>
/// Thin, guarded readers and writers for the Windows settings the Latency module looks at: registry keys, powercfg,
/// bcdedit, the kernel timer resolution and the mouse acceleration flag. Every member returns null or a failed
/// result instead of throwing, so callers can degrade gracefully.
/// </summary>
public static class LatencyProbes
{
    public const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public const string GameBarKey = @"Software\Microsoft\GameBar";
    public const string NetworkClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    public const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    public const string PciEnumKey = @"SYSTEM\CurrentControlSet\Enum\PCI";
    public const string TcpInterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    // powercfg sub-group / setting GUIDs.
    public const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    public const string UsbSelectiveSuspendSetting = "48e6b7a6-50f5-4782-a5d4-53bb50ff42bd";
    public const string PciSubgroup = "501a4d13-42af-4429-9fd1-a8218c268e20";
    public const string PciAspmSetting = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    public const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    public const string CoreParkingMinCoresSetting = "0cc5b647-c1df-4637-891a-dec35c318583";

    private const uint SpiGetMouse = 0x0003;
    private const uint SpiSetMouse = 0x0004;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    /// <summary>Current, minimum and maximum timer resolution in milliseconds, or null when unavailable.</summary>
    public static (double CurrentMs, double MinMs, double MaxMs)? QueryTimerResolution()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            int status = NativeMethods.NtQueryTimerResolution(out uint max, out uint min, out uint current);
            if (status != 0)
            {
                return null;
            }

            // NtQueryTimerResolution reports the coarsest value as "maximum" and the finest as "minimum" (both in 100 ns).
            return (LatencyRules.HundredNanosecondsToMs(current), LatencyRules.HundredNanosecondsToMs(min), LatencyRules.HundredNanosecondsToMs(max));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads a DWORD value; null when the key or value is missing or the platform is not Windows.</summary>
    public static int? ReadDword(RegistryHive hive, string subKey, string valueName)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(subKey, false);
            return key?.GetValue(valueName) is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads any value as text; null when missing.</summary>
    public static string? ReadString(RegistryHive hive, string subKey, string valueName)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(subKey, false);
            return key?.GetValue(valueName)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a value that may be REG_MULTI_SZ (for example the static <c>IPAddress</c> list of a TCP/IP interface) as
    /// a list of strings. A plain string value becomes a single-item list; missing values give an empty list.
    /// </summary>
    public static IReadOnlyList<string> ReadStrings(RegistryHive hive, string subKey, string valueName)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(subKey, false);
            return key?.GetValue(valueName) switch
            {
                string[] many => many,
                string one => [one],
                null => [],
                var other => [other.ToString() ?? string.Empty],
            };
        }
        catch
        {
            return [];
        }
    }

    /// <summary>True when the value exists under the key (regardless of its content).</summary>
    public static bool ValueExists(RegistryHive hive, string subKey, string valueName)
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(subKey, false);
            return key?.GetValue(valueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes a DWORD value, creating the key when needed.</summary>
    public static OperationResult WriteDword(RegistryHive hive, string subKey, string valueName, int value)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Registry changes are only possible on Windows.");
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).CreateSubKey(subKey, true);
            key.SetValue(valueName, value, RegistryValueKind.DWord);
            return OperationResult.Ok($"{valueName} = {value}");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not write {subKey}\\{valueName}: {ex.Message}", ex);
        }
    }

    /// <summary>Writes a REG_SZ value (network driver settings are stored as strings).</summary>
    public static OperationResult WriteString(RegistryHive hive, string subKey, string valueName, string value)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Registry changes are only possible on Windows.");
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).CreateSubKey(subKey, true);
            key.SetValue(valueName, value, RegistryValueKind.String);
            return OperationResult.Ok($"{valueName} = {value}");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not write {subKey}\\{valueName}: {ex.Message}", ex);
        }
    }

    /// <summary>Deletes a value if present. Succeeds when it was already absent.</summary>
    public static OperationResult DeleteValue(RegistryHive hive, string subKey, string valueName)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Registry changes are only possible on Windows.");
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(subKey, true);
            key?.DeleteValue(valueName, false);
            return OperationResult.Ok($"{valueName} removed");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not remove {subKey}\\{valueName}: {ex.Message}", ex);
        }
    }

    /// <summary>Names of the sub keys under a key, or an empty list.</summary>
    public static IReadOnlyList<string> SubKeyNames(RegistryHive hive, string subKey)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(subKey, false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Hardware-accelerated GPU scheduling from HwSchMode (2 on, 1 off).</summary>
    public static bool? ReadHags() => LatencyRules.HagsFromRegistry(ReadDword(RegistryHive.LocalMachine, GraphicsDriversKey, "HwSchMode"));

    /// <summary>Game Mode: AutoGameModeEnabled under HKCU\Software\Microsoft\GameBar. Missing means the Windows 11 default (on).</summary>
    public static bool? ReadGameMode()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        int? value = ReadDword(RegistryHive.CurrentUser, GameBarKey, "AutoGameModeEnabled");
        return value is null ? true : value != 0;
    }

    /// <summary>"Enhance pointer precision" (mouse acceleration) via SystemParametersInfo(SPI_GETMOUSE).</summary>
    public static bool? ReadPointerPrecision()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            var values = new int[3];
            return NativeMethods.SystemParametersInfo(SpiGetMouse, 0, values, 0) ? values[2] != 0 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sets mouse acceleration on ({6,10,1}) or off ({0,0,0}) and persists it to the user profile.</summary>
    public static OperationResult SetPointerPrecision(bool enabled)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Mouse settings can only be changed on Windows.");
        }

        try
        {
            int[] values = enabled ? [6, 10, 1] : [0, 0, 0];
            bool ok = NativeMethods.SystemParametersInfo(SpiSetMouse, 0, values, SpifUpdateIniFile | SpifSendChange);
            return ok
                ? OperationResult.Ok(enabled ? "Enhance pointer precision is on." : "Enhance pointer precision is off.")
                : OperationResult.Fail($"Windows refused the change (error {Marshal.GetLastWin32Error()}).");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Could not change pointer precision: " + ex.Message, ex);
        }
    }

    /// <summary>True when a display adapter with the NVIDIA vendor id (10DE) is present.</summary>
    public static bool HasNvidiaGpu() => FindGpuPnpPaths().Any(p => p.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase));

    /// <summary>PCI instance paths (relative to Enum\PCI) of every display adapter, e.g. "VEN_10DE&amp;DEV_2786…\4&amp;…".</summary>
    public static IReadOnlyList<string> FindGpuPnpPaths()
    {
        var result = new List<string>();
        foreach (var device in SubKeyNames(RegistryHive.LocalMachine, PciEnumKey))
        {
            foreach (var instance in SubKeyNames(RegistryHive.LocalMachine, $@"{PciEnumKey}\{device}"))
            {
                var path = $@"{device}\{instance}";
                var classGuid = ReadString(RegistryHive.LocalMachine, $@"{PciEnumKey}\{path}", "ClassGUID");
                if (string.Equals(classGuid, DisplayClassGuid, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(path);
                }
            }
        }

        return result;
    }

    /// <summary>Power-saving state of every physical network adapter, read from the driver class key.</summary>
    public static IReadOnlyList<(string SubKey, NicPowerState State)> ReadNetworkAdapters()
    {
        var list = new List<(string, NicPowerState)>();
        foreach (var index in SubKeyNames(RegistryHive.LocalMachine, NetworkClassKey))
        {
            if (index.Length != 4 || !index.All(char.IsDigit))
            {
                continue;
            }

            var subKey = $@"{NetworkClassKey}\{index}";
            var description = ReadString(RegistryHive.LocalMachine, subKey, "DriverDesc");
            var instanceId = ReadString(RegistryHive.LocalMachine, subKey, "NetCfgInstanceId");
            if (string.IsNullOrEmpty(instanceId) || NicPowerRules.IsVirtualAdapter(description))
            {
                continue;
            }

            list.Add((subKey, NicPowerRules.Interpret(description!,
                ReadString(RegistryHive.LocalMachine, subKey, "PnPCapabilities"),
                ReadString(RegistryHive.LocalMachine, subKey, "*EEE"),
                ReadString(RegistryHive.LocalMachine, subKey, "EnableGreenEthernet"),
                ReadString(RegistryHive.LocalMachine, subKey, "*InterruptModeration"),
                ReadString(RegistryHive.LocalMachine, subKey, "*FlowControl"))));
        }

        return list;
    }

    /// <summary>Active power scheme id and name from <c>powercfg /getactivescheme</c>.</summary>
    public static async Task<(Guid? Id, string? Name)> ReadActiveSchemeAsync(CancellationToken cancellationToken)
    {
        var run = await RunAsync("powercfg.exe", "/getactivescheme", cancellationToken).ConfigureAwait(false);
        return run.ExitCode == 0 ? PowerCfgQueryParser.ParseActiveScheme(run.Output) : (null, null);
    }

    /// <summary>AC and DC indexes of one setting in the active scheme.</summary>
    public static async Task<(int? Ac, int? Dc)> ReadPowerSettingAsync(string subgroup, string setting, CancellationToken cancellationToken)
    {
        var run = await RunAsync("powercfg.exe", $"/q SCHEME_CURRENT {subgroup} {setting}", cancellationToken).ConfigureAwait(false);
        return run.ExitCode == 0 ? PowerCfgQueryParser.ParseIndexes(run.Output) : (null, null);
    }

    /// <summary>The powercfg alias for the active power plan.</summary>
    public const string CurrentScheme = "SCHEME_CURRENT";

    /// <summary>The scheme argument for powercfg: a captured plan GUID when it is one, otherwise <see cref="CurrentScheme"/>. Pure.</summary>
    public static string SchemeArgument(string? schemeId) =>
        Guid.TryParse(schemeId, out var id) && id != Guid.Empty ? id.ToString("D") : CurrentScheme;

    /// <summary>Sets a power setting for both AC and DC in the active scheme and re-activates it.</summary>
    public static Task<OperationResult> SetPowerSettingAsync(string subgroup, string setting, int value, CancellationToken cancellationToken) =>
        SetPowerSettingAsync(subgroup, setting, value, value, cancellationToken);

    /// <summary>Sets separate AC and DC indexes of a power setting in the active scheme and re-activates it.</summary>
    public static Task<OperationResult> SetPowerSettingAsync(string subgroup, string setting, int acValue, int dcValue, CancellationToken cancellationToken) =>
        SetPowerSettingAsync(CurrentScheme, subgroup, setting, acValue, dcValue, cancellationToken);

    /// <summary>
    /// Sets separate AC and DC indexes of a power setting in <paramref name="scheme"/> (a plan GUID or
    /// <see cref="CurrentScheme"/>) and re-activates the active plan so a change to it takes effect.
    /// </summary>
    public static async Task<OperationResult> SetPowerSettingAsync(string scheme, string subgroup, string setting, int acValue, int dcValue, CancellationToken cancellationToken)
    {
        foreach (var (verb, value) in new[] { ("/setacvalueindex", acValue), ("/setdcvalueindex", dcValue) })
        {
            var run = await RunAsync("powercfg.exe", $"{verb} {scheme} {subgroup} {setting} {value}", cancellationToken).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                return OperationResult.Fail($"powercfg {verb} failed: {Describe(run)}");
            }
        }

        var activate = await RunAsync("powercfg.exe", "/setactive SCHEME_CURRENT", cancellationToken).ConfigureAwait(false);
        return activate.ExitCode == 0
            ? OperationResult.Ok(acValue == dcValue ? $"Power setting {setting} set to {acValue}." : $"Power setting {setting} set to AC {acValue} / DC {dcValue}.")
            : OperationResult.Fail("powercfg /setactive failed: " + Describe(activate));
    }

    /// <summary>Boot configuration values of the current entry from <c>bcdedit /enum {current}</c>.</summary>
    public static async Task<IReadOnlyDictionary<string, string>?> ReadBootConfigAsync(CancellationToken cancellationToken)
    {
        var run = await RunAsync("bcdedit.exe", "/enum {current}", cancellationToken).ConfigureAwait(false);
        return run.ExitCode == 0 ? BcdEditParser.Parse(run.Output) : null;
    }

    /// <summary>Runs <c>bcdedit</c> with the given arguments (for example "/set disabledynamictick yes").</summary>
    public static async Task<OperationResult> BcdEditAsync(string arguments, CancellationToken cancellationToken)
    {
        var run = await RunAsync("bcdedit.exe", arguments, cancellationToken).ConfigureAwait(false);
        return run.ExitCode == 0 ? OperationResult.Ok($"bcdedit {arguments}") : OperationResult.Fail($"bcdedit {arguments} failed: {Describe(run)}");
    }

    /// <summary>Runs a console tool and captures its output. Never throws except for cancellation.</summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string fileName, string arguments, CancellationToken cancellationToken, int timeoutSeconds = 60)
    {
        if (!PlatformGuard.IsWindows)
        {
            return (-1, string.Empty, $"{fileName} is only available on Windows.");
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    // Console tools (bcdedit, sc, shutdown, powercfg) write in the OEM code page; null keeps the process default.
                    StandardOutputEncoding = StorageCommandRunner.OemEncoding(),
                    StandardErrorEncoding = StorageCommandRunner.OemEncoding(),
                },
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* best effort */ }
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return (-2, string.Empty, $"{fileName} timed out after {timeoutSeconds} s.");
            }

            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string Describe((int ExitCode, string Output, string Error) run) =>
        string.IsNullOrWhiteSpace(run.Error) ? (string.IsNullOrWhiteSpace(run.Output) ? $"exit code {run.ExitCode}" : run.Output.Trim()) : run.Error.Trim();

    private static class NativeMethods
    {
        [DllImport("ntdll.dll")]
        public static extern int NtQueryTimerResolution(out uint maximumResolution, out uint minimumResolution, out uint currentResolution);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);
    }
}
