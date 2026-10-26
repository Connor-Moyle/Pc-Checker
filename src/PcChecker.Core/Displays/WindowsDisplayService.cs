using System.Management;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using static PcChecker.Core.Displays.DisplayNativeMethods;

namespace PcChecker.Core.Displays;

/// <summary>
/// Windows implementation of <see cref="IDisplayService"/> on GDI (<c>EnumDisplayDevices</c>, <c>ChangeDisplaySettingsEx</c>),
/// DisplayConfig (friendly names, connection, HDR), WMI (EDID facts, built-in panel brightness) and DDC/CI (external
/// monitor brightness and contrast). Layout changes run through <see cref="IChangeGuard"/>; brightness and contrast are
/// treated like a volume knob and applied directly.
/// </summary>
public sealed class WindowsDisplayService : IDisplayService
{
    private const string Module = "Displays";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsDisplayService> _logger;
    private readonly object _gate = new();
    private IReadOnlyList<DisplayInfo> _last = [];
    private Dictionary<string, DisplayTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _wmiInstanceByDevice = new(StringComparer.OrdinalIgnoreCase);

    public WindowsDisplayService(IChangeGuard guard, ILogger<WindowsDisplayService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    /// <summary>Warnings collected by the last enumeration (sources that could not be read).</summary>
    public IReadOnlyList<string> LastWarnings { get; private set; } = [];

    public Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<IReadOnlyList<DisplayInfo>>([]);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var warnings = new List<string>();
            var displays = new List<DisplayInfo>();
            try
            {
                displays = Enumerate(warnings, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Display enumeration failed.");
                warnings.Add("Displays could not be enumerated: " + ex.Message);
            }

            lock (_gate)
            {
                _last = displays;
                LastWarnings = warnings;
            }

            return (IReadOnlyList<DisplayInfo>)displays;
        }, cancellationToken);
    }

    public Task<OperationResult> SetModeAsync(string deviceName, DisplayMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var previous = Find(deviceName)?.Current;
        string? undo = previous is null ? null : $"Set {DisplayProfiles.ShortDeviceName(deviceName)} back to {previous}.";
        return _guard.RunGuardedAsync($"Change {DisplayProfiles.ShortDeviceName(deviceName)} to {mode}",
            new ChangeOptions(false, false, Module, deviceName, true, undo),
            ct => Task.Run(() => ApplyMode(deviceName, mode), ct), null, cancellationToken);
    }

    public Task<OperationResult> SetPrimaryAsync(string deviceName, CancellationToken cancellationToken)
    {
        var currentPrimary = Find(null)?.DeviceName;
        return _guard.RunGuardedAsync($"Make {DisplayProfiles.ShortDeviceName(deviceName)} the main display",
            new ChangeOptions(false, false, Module, deviceName, true, currentPrimary is null ? null : $"Make {DisplayProfiles.ShortDeviceName(currentPrimary)} the main display again."),
            ct => Task.Run(() => ApplyPrimary(deviceName), ct), null, cancellationToken);
    }

    public Task<OperationResult> SetOrientationAsync(string deviceName, int degrees, CancellationToken cancellationToken)
    {
        var previous = Find(deviceName)?.OrientationDegrees;
        return _guard.RunGuardedAsync($"Rotate {DisplayProfiles.ShortDeviceName(deviceName)} to {DisplayText.OrientationText(degrees)}",
            new ChangeOptions(false, false, Module, deviceName, true, previous is null ? null : $"Rotate back to {DisplayText.OrientationText(previous.Value)}."),
            ct => Task.Run(() => ApplyOrientation(deviceName, degrees), ct), null, cancellationToken);
    }

    public Task<OperationResult> SetBrightnessAsync(string deviceName, int percent, CancellationToken cancellationToken) =>
        Task.Run(() => ApplyBrightness(deviceName, Math.Clamp(percent, 0, 100)), cancellationToken);

    public Task<OperationResult> SetContrastAsync(string deviceName, int percent, CancellationToken cancellationToken) =>
        Task.Run(() => ApplyDdc(deviceName, true, Math.Clamp(percent, 0, 100)), cancellationToken);

    public Task<OperationResult> SetHdrAsync(string deviceName, bool enabled, CancellationToken cancellationToken) =>
        _guard.RunGuardedAsync($"Turn HDR {(enabled ? "on" : "off")} for {DisplayProfiles.ShortDeviceName(deviceName)}",
            new ChangeOptions(false, false, Module, deviceName, true, $"Turn HDR {(enabled ? "off" : "on")} again."),
            ct => Task.Run(() => ApplyHdr(deviceName, enabled), ct), null, cancellationToken);

    public Task<OperationResult> ApplyProfileAsync(DisplayProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _guard.RunGuardedAsync($"Apply display profile \"{profile.Name}\"",
            new ChangeOptions(false, false, Module, profile.Name, true, "Choose a different profile or set each display back by hand."),
            ct => Task.Run(() =>
            {
                var problems = new List<string>();
                foreach (var (device, mode) in profile.Modes)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = ApplyMode(device, mode);
                    if (!r.Success)
                    {
                        problems.Add($"{DisplayProfiles.ShortDeviceName(device)}: {r.Message}");
                    }
                }

                if (profile.PrimaryDevice is not null)
                {
                    var r = ApplyPrimary(profile.PrimaryDevice);
                    if (!r.Success)
                    {
                        problems.Add($"Main display: {r.Message}");
                    }
                }

                return problems.Count == 0
                    ? OperationResult.Ok($"Profile \"{profile.Name}\" applied.")
                    : OperationResult.Fail($"Profile \"{profile.Name}\" was only partly applied. " + string.Join(" ", problems));
            }, ct), null, cancellationToken);
    }

    public OperationResult OpenWindowsDisplaySettings() => ShellLauncher.Open("ms-settings:display");

    public OperationResult OpenNightLightSettings() => ShellLauncher.Open("ms-settings:nightlight");

    public OperationResult OpenColorCalibration() => ShellLauncher.Open("dccw.exe");

    public OperationResult OpenGpuControlPanel()
    {
        var vendor = DetectGraphicsVendor();
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var candidate in DisplayVendorTools.ControlPanelCandidates(vendor, programFiles, windows))
        {
            if (candidate.IsFilePath)
            {
                if (!File.Exists(candidate.Target))
                {
                    continue;
                }

                var r = ShellLauncher.Open(candidate.Target);
                if (r.Success)
                {
                    return OperationResult.Ok($"Opened {candidate.Description}.");
                }
            }
            else
            {
                var r = ShellLauncher.Open(candidate.Target);
                return r.Success ? OperationResult.Ok($"Opened {candidate.Description}.") : r;
            }
        }

        return OperationResult.Fail($"{DisplayVendorTools.ControlPanelName(vendor)} does not seem to be installed.");
    }

    // ----- enumeration -------------------------------------------------------------------------------------------

    private List<DisplayInfo> Enumerate(List<string> warnings, CancellationToken cancellationToken)
    {
        var gdi = WindowsDisplayEnumerator.EnumerateGdi();
        var handles = WindowsDisplayEnumerator.EnumerateMonitorHandles();
        var targets = WindowsDisplayEnumerator.QueryTargets(_logger, warnings);
        var wmi = WindowsDisplayEnumerator.QueryWmi(_logger, warnings);
        var wmiByDevice = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DisplayInfo>();

        foreach (var (adapter, monitor, current) in gdi)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string gdiName = adapter.DeviceName;
            targets.TryGetValue(gdiName, out var target);
            string monitorId = target?.DevicePath is { Length: > 0 } p ? p : monitor?.DeviceID ?? string.Empty;
            var wmiMonitor = wmi.FirstOrDefault(m => DisplayVendorTools.SameMonitor(m.InstanceName, monitorId));
            if (wmiMonitor is not null)
            {
                wmiByDevice[gdiName] = wmiMonitor.InstanceName;
            }

            handles.TryGetValue(gdiName, out var hMonitor);
            bool isInternal = target is not null && DisplayText.IsInternalOutput(target.OutputTechnology);
            string friendly = FirstNonEmpty(target?.FriendlyName, wmiMonitor?.FriendlyName, monitor?.DeviceString, gdiName);
            string? manufacturer = FirstNonEmptyOrNull(DisplayText.ManufacturerName(target?.ManufacturerCode), wmiMonitor?.Manufacturer);
            string? connection = target is null ? null : DisplayText.OutputTechnologyText(target.OutputTechnology);
            double? diagonal = wmiMonitor is null ? null : DisplayText.DiagonalInches(wmiMonitor.WidthCm, wmiMonitor.HeightCm);

            var modes = WindowsDisplayEnumerator.EnumerateModes(gdiName);
            var currentMode = new DisplayMode((int)current.dmPelsWidth, (int)current.dmPelsHeight, (int)current.dmDisplayFrequency, (int)current.dmBitsPerPel);

            DdcReading ddc = isInternal ? new DdcReading(false, null, null) : WindowsDisplayEnumerator.ReadDdc(hMonitor, _logger);
            int? brightness = ddc.Brightness ?? wmiMonitor?.Brightness;
            bool supportsDdc = ddc.Supported;
            if (isInternal && wmiMonitor?.Brightness is null)
            {
                brightness = null;
            }

            result.Add(new DisplayInfo(
                gdiName,
                friendly,
                manufacturer,
                wmiMonitor?.Serial,
                (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0,
                currentMode,
                WindowsDisplayEnumerator.ScalePercent(hMonitor),
                current.dmPositionX,
                current.dmPositionY,
                DisplayText.OrientationFromDevMode(current.dmDisplayOrientation),
                target?.HdrSupported ?? false,
                target?.HdrEnabled ?? false,
                connection,
                diagonal,
                string.IsNullOrWhiteSpace(adapter.DeviceString) ? null : adapter.DeviceString,
                modes.Count > 0 ? modes : [currentMode],
                supportsDdc,
                brightness,
                ddc.Contrast,
                isInternal));
        }

        if (result.Count == 0)
        {
            warnings.Add("Windows did not report any attached displays. Remote sessions and some virtual machines hide them.");
        }

        lock (_gate)
        {
            _targets = targets;
            _wmiInstanceByDevice = wmiByDevice;
        }

        return result;
    }

    // ----- changes -----------------------------------------------------------------------------------------------

    private OperationResult ApplyMode(string deviceName, DisplayMode mode)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Display changes are only possible on Windows.");
        }

        var dm = DevMode.Create();
        if (!EnumDisplaySettingsEx(deviceName, EnumCurrentSettings, ref dm, 0))
        {
            return OperationResult.Fail($"Could not read the current settings of {DisplayProfiles.ShortDeviceName(deviceName)}.");
        }

        dm.dmPelsWidth = (uint)mode.Width;
        dm.dmPelsHeight = (uint)mode.Height;
        dm.dmDisplayFrequency = (uint)mode.RefreshHz;
        dm.dmBitsPerPel = (uint)(mode.BitsPerPixel > 0 ? mode.BitsPerPixel : 32);
        dm.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency | DmBitsPerPel;

        int test = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (!DisplayText.IsDispChangeSuccess(test))
        {
            _logger.LogWarning("Mode test for {Device} {Mode} failed with {Code}.", deviceName, mode, test);
            return OperationResult.Fail(DisplayText.DispChangeMessage(test));
        }

        int code = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        _logger.LogInformation("ChangeDisplaySettingsEx {Device} → {Mode}: {Code}", deviceName, mode, code);
        return DisplayText.IsDispChangeSuccess(code)
            ? OperationResult.Ok($"{DisplayProfiles.ShortDeviceName(deviceName)} is now {mode}.", code == 1)
            : OperationResult.Fail(DisplayText.DispChangeMessage(code));
    }

    private OperationResult ApplyOrientation(string deviceName, int degrees)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Display changes are only possible on Windows.");
        }

        var dm = DevMode.Create();
        if (!EnumDisplaySettingsEx(deviceName, EnumCurrentSettings, ref dm, 0))
        {
            return OperationResult.Fail($"Could not read the current settings of {DisplayProfiles.ShortDeviceName(deviceName)}.");
        }

        int current = DisplayText.OrientationFromDevMode(dm.dmDisplayOrientation);
        int target = DisplayText.Normalize(degrees);
        if (DisplayText.SwapsDimensions(current, target))
        {
            (dm.dmPelsWidth, dm.dmPelsHeight) = (dm.dmPelsHeight, dm.dmPelsWidth);
        }

        dm.dmDisplayOrientation = (uint)DisplayText.OrientationToDevMode(target);
        dm.dmFields = DmDisplayOrientation | DmPelsWidth | DmPelsHeight;
        int code = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        _logger.LogInformation("Rotate {Device} → {Degrees}°: {Code}", deviceName, target, code);
        return DisplayText.IsDispChangeSuccess(code)
            ? OperationResult.Ok($"{DisplayProfiles.ShortDeviceName(deviceName)} is now in {DisplayText.OrientationText(target)} orientation.", code == 1)
            : OperationResult.Fail(DisplayText.DispChangeMessage(code));
    }

    private OperationResult ApplyPrimary(string deviceName)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Display changes are only possible on Windows.");
        }

        var gdi = WindowsDisplayEnumerator.EnumerateGdi();
        var chosen = gdi.FirstOrDefault(g => string.Equals(g.Adapter.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (chosen.Adapter.DeviceName is null)
        {
            return OperationResult.Fail($"{DisplayProfiles.ShortDeviceName(deviceName)} is not attached.");
        }

        if ((chosen.Adapter.StateFlags & DisplayDevicePrimaryDevice) != 0)
        {
            return OperationResult.Ok($"{DisplayProfiles.ShortDeviceName(deviceName)} is already the main display.");
        }

        // The main display must sit at (0,0): shift every display by the chosen one's current position, stage the
        // changes without applying them, then commit all at once so the desktop never ends up with a gap.
        int offsetX = chosen.Current.dmPositionX;
        int offsetY = chosen.Current.dmPositionY;
        foreach (var (adapter, _, current) in gdi)
        {
            var dm = current;
            dm.dmPositionX = current.dmPositionX - offsetX;
            dm.dmPositionY = current.dmPositionY - offsetY;
            dm.dmFields = DmPosition;
            bool isChosen = string.Equals(adapter.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase);
            uint flags = CdsUpdateRegistry | CdsNoReset | (isChosen ? CdsSetPrimary : 0);
            int code = ChangeDisplaySettingsEx(adapter.DeviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (!DisplayText.IsDispChangeSuccess(code))
            {
                _logger.LogWarning("Staging position for {Device} failed with {Code}.", adapter.DeviceName, code);
                return OperationResult.Fail($"{DisplayProfiles.ShortDeviceName(adapter.DeviceName)}: {DisplayText.DispChangeMessage(code)}");
            }
        }

        int commit = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        _logger.LogInformation("Set primary {Device}: {Code}", deviceName, commit);
        return DisplayText.IsDispChangeSuccess(commit)
            ? OperationResult.Ok($"{DisplayProfiles.ShortDeviceName(deviceName)} is now the main display.", commit == 1)
            : OperationResult.Fail(DisplayText.DispChangeMessage(commit));
    }

    private OperationResult ApplyHdr(string deviceName, bool enabled)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Display changes are only possible on Windows.");
        }

        DisplayTarget? target;
        lock (_gate)
        {
            _targets.TryGetValue(deviceName, out target);
        }

        if (target is null)
        {
            var warnings = new List<string>();
            var fresh = WindowsDisplayEnumerator.QueryTargets(_logger, warnings);
            fresh.TryGetValue(deviceName, out target);
        }

        if (target is null)
        {
            return OperationResult.Fail("Windows did not report an HDR-capable path for this display. Use Windows Settings → System → Display to change HDR.");
        }

        if (!target.HdrSupported)
        {
            return OperationResult.Fail("This display does not report HDR support, so Windows will not let it be turned on.");
        }

        try
        {
            var packet = DisplayConfigSetAdvancedColorState.Create(target.Adapter, target.TargetId, enabled);
            int code = DisplayConfigSetDeviceInfo(ref packet);
            _logger.LogInformation("Set HDR {Enabled} on {Device}: {Code}", enabled, deviceName, code);
            return code == 0
                ? OperationResult.Ok($"HDR is now {(enabled ? "on" : "off")} for {DisplayProfiles.ShortDeviceName(deviceName)}.")
                : OperationResult.Fail($"Windows refused to change HDR (error {code}). Use Windows Settings → System → Display instead.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HDR change failed for {Device}.", deviceName);
            return OperationResult.Fail("HDR could not be changed: " + ex.Message, ex);
        }
    }

    private OperationResult ApplyBrightness(string deviceName, int percent)
    {
        var display = Find(deviceName);
        if (display is { IsInternal: true } || display is { SupportsDdc: false })
        {
            var wmi = ApplyWmiBrightness(deviceName, percent);
            if (wmi.Success || display is { IsInternal: true })
            {
                return wmi;
            }
        }

        return ApplyDdc(deviceName, false, percent);
    }

    private OperationResult ApplyWmiBrightness(string deviceName, int percent)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Brightness control is only possible on Windows.");
        }

        string? instance;
        lock (_gate)
        {
            _wmiInstanceByDevice.TryGetValue(deviceName, out instance);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (var mo in searcher.Get())
            {
                string name = mo["InstanceName"] as string ?? string.Empty;
                if (instance is not null && !DisplayVendorTools.SameMonitor(name, instance))
                {
                    continue;
                }

                ((ManagementObject)mo).InvokeMethod("WmiSetBrightness", [(uint)1, (byte)percent]);
                _logger.LogInformation("WmiSetBrightness {Device} → {Percent}%", deviceName, percent);
                return OperationResult.Ok($"Brightness set to {percent}%.");
            }

            return OperationResult.Fail("Windows does not expose a brightness control for this panel.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WmiSetBrightness failed for {Device}.", deviceName);
            return OperationResult.Fail("Brightness could not be changed: " + ex.Message, ex);
        }
    }

    private OperationResult ApplyDdc(string deviceName, bool contrast, int percent)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Monitor control is only possible on Windows.");
        }

        try
        {
            var handles = WindowsDisplayEnumerator.EnumerateMonitorHandles();
            if (!handles.TryGetValue(deviceName, out var hMonitor))
            {
                return OperationResult.Fail($"{DisplayProfiles.ShortDeviceName(deviceName)} is not attached.");
            }

            var task = Task.Run(() =>
            {
                bool ok = WindowsDisplayEnumerator.WriteDdc(hMonitor, contrast, percent, out string error);
                return (ok, error);
            });
            if (!task.Wait(TimeSpan.FromSeconds(5)))
            {
                return OperationResult.Fail("The monitor did not answer the DDC/CI command in time. Check that DDC/CI is enabled in its on-screen menu.");
            }

            var (success, message) = task.Result;
            _logger.LogInformation("DDC {What} {Device} → {Percent}%: {Ok}", contrast ? "contrast" : "brightness", deviceName, percent, success);
            return success
                ? OperationResult.Ok($"{(contrast ? "Contrast" : "Brightness")} set to {percent}%.")
                : OperationResult.Fail(message);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DDC write failed for {Device}.", deviceName);
            return OperationResult.Fail($"{(contrast ? "Contrast" : "Brightness")} could not be changed: {ex.Message}", ex);
        }
    }

    // ----- helpers -----------------------------------------------------------------------------------------------

    /// <summary>The cached display with this device name, or the primary when <paramref name="deviceName"/> is null.</summary>
    private DisplayInfo? Find(string? deviceName)
    {
        lock (_gate)
        {
            return deviceName is null
                ? _last.FirstOrDefault(d => d.IsPrimary)
                : _last.FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private GraphicsVendor DetectGraphicsVendor()
    {
        if (!PlatformGuard.IsWindows)
        {
            return GraphicsVendor.Unknown;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
            var found = GraphicsVendor.Unknown;
            foreach (var mo in searcher.Get())
            {
                var vendor = DisplayVendorTools.VendorFrom(mo["PNPDeviceID"] as string, mo["Name"] as string);
                if (vendor == GraphicsVendor.Unknown)
                {
                    continue;
                }

                // Prefer a discrete card over integrated Intel graphics when both are present.
                if (found == GraphicsVendor.Unknown || (found == GraphicsVendor.Intel && vendor != GraphicsVendor.Intel))
                {
                    found = vendor;
                }
            }

            return found;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not detect the graphics vendor.");
            return GraphicsVendor.Unknown;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private static string? FirstNonEmptyOrNull(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
