using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;

namespace PcChecker.App.ViewModels;

/// <summary>GPU tuning card, profiles, CPU card, vendor tools and the advanced card.</summary>
public sealed partial class OverclockViewModel
{
    // GPU sliders (doubles so Slider/NumberBox bind without converters)
    [ObservableProperty] private double _coreOffset;
    [ObservableProperty] private double _memoryOffset;
    [ObservableProperty] private double _powerLimit = 100;
    [ObservableProperty] private double _tempLimit = 83;
    [ObservableProperty] private bool _tempLimitEnabled;
    [ObservableProperty] private double _fanPercent = 50;
    [ObservableProperty] private bool _fanManual;

    [ObservableProperty] private double _coreMin = -200;
    [ObservableProperty] private double _coreMax = 200;
    [ObservableProperty] private double _memoryMin = -1000;
    [ObservableProperty] private double _memoryMax = 1000;
    [ObservableProperty] private double _powerMin = 50;
    [ObservableProperty] private double _powerMax = 115;
    [ObservableProperty] private double _tempMin = 60;
    [ObservableProperty] private double _tempMax = 87;
    [ObservableProperty] private bool _powerLimitSupported;
    [ObservableProperty] private bool _tempLimitSupported;
    [ObservableProperty] private bool _fanSupported;

    // Radeon RX 7000/9000 voltage offset (Overdrive8); hidden on other cards.
    [ObservableProperty] private double _voltageOffset;
    [ObservableProperty] private double _voltageMin = -200;
    [ObservableProperty] private double _voltageMax;
    [ObservableProperty] private bool _voltageSupported;
    [ObservableProperty] private bool _isAmdGpu;

    /// <summary>AMD calls the core control "Max frequency" (an offset on RDNA4, the boost ceiling on RDNA3).</summary>
    public string CoreLabel => IsAmdGpu ? "Max frequency offset (MHz)" : "Core clock offset (MHz)";

    partial void OnIsAmdGpuChanged(bool value) => OnPropertyChanged(nameof(CoreLabel));

    [ObservableProperty] private OverclockProfile? _selectedProfile;
    [ObservableProperty] private string _newProfileName = "My profile";

    // CPU
    [ObservableProperty] private bool _cpuRatioEnabled;
    [ObservableProperty] private double _cpuRatio = 50;
    [ObservableProperty] private bool _cpuVoltageEnabled;
    [ObservableProperty] private double _cpuVoltageOffset;
    [ObservableProperty] private bool _cpuPowerEnabled;
    [ObservableProperty] private double _cpuPowerLimit = 125;
    [ObservableProperty] private bool _cpuPowerOnly;

    // Vendor tools
    [ObservableProperty] private string _gpuToolName = "NVIDIA app";
    [ObservableProperty] private string _gpuToolPurpose = string.Empty;
    [ObservableProperty] private bool _gpuToolInstalled;
    [ObservableProperty] private string _cpuToolName = "Intel XTU";
    [ObservableProperty] private string _cpuToolPurpose = string.Empty;
    [ObservableProperty] private bool _cpuToolInstalled;
    [ObservableProperty] private bool _cpuToolKnown;

    // Advanced
    [ObservableProperty] private string _rawLimitsText = string.Empty;
    [ObservableProperty] private string _sessionFileText = "No session file.";
    [ObservableProperty] private string _sessionFilePath = string.Empty;
    [ObservableProperty] private string _userLimitsText = string.Empty;

    private VendorSoftware? _gpuTool;
    private VendorSoftware? _cpuTool;
    private string? _gpuToolPath;
    private string? _cpuToolPath;

    public ObservableCollection<OverclockProfile> Profiles { get; } = [];
    public IReadOnlyList<string> BiosSteps => VendorSoftwareCatalog.BiosSteps(CpuVendorText, Capabilities?.CpuName);

    public string FanButtonHint => "Fan curves and per-fan control live on the Fans page; here you can only pin the GPU fan to a fixed speed.";

    /// <summary>The profile described by the current slider values.</summary>
    public OverclockProfile BuildProfile(string name)
    {
        var p = new OverclockProfile
        {
            Name = name,
            GpuCoreOffsetMhz = (int)Math.Round(CoreOffset),
            GpuMemoryOffsetMhz = (int)Math.Round(MemoryOffset),
            GpuPowerLimitPercent = PowerLimitSupported ? (int)Math.Round(PowerLimit) : 100,
            GpuTempLimitC = TempLimitSupported && TempLimitEnabled ? (int)Math.Round(TempLimit) : null,
            GpuFanPercent = FanSupported && FanManual ? (int)Math.Round(FanPercent) : null,
            GpuVoltageOffsetMv = VoltageSupported ? (int)Math.Round(VoltageOffset) : 0,
        };
        if (CanTuneCpu)
        {
            p.CpuAllCoreRatio = CpuRatioEnabled && !CpuPowerOnly ? (int)Math.Round(CpuRatio) : null;
            p.CpuVoltageOffsetMv = CpuVoltageEnabled && !CpuPowerOnly ? (int)Math.Round(CpuVoltageOffset) : null;
            p.CpuPowerLimitW = CpuPowerEnabled ? (int)Math.Round(CpuPowerLimit) : null;
        }

        return p;
    }

    /// <summary>Loads a profile into the sliders (clamped to the current ranges).</summary>
    public void LoadIntoSliders(OverclockProfile profile)
    {
        var clamped = OverclockSafetyValidator.Clamp(profile, Settings.Current.Overclock, Capabilities?.GpuLimits);
        CoreOffset = Math.Clamp(clamped.GpuCoreOffsetMhz, CoreMin, CoreMax);
        MemoryOffset = Math.Clamp(clamped.GpuMemoryOffsetMhz, MemoryMin, MemoryMax);
        PowerLimit = Math.Clamp(clamped.GpuPowerLimitPercent, PowerMin, PowerMax);
        VoltageOffset = VoltageSupported ? Math.Clamp(clamped.GpuVoltageOffsetMv, VoltageMin, VoltageMax) : 0;
        TempLimitEnabled = clamped.GpuTempLimitC is not null;
        if (clamped.GpuTempLimitC is { } t)
        {
            TempLimit = Math.Clamp(t, TempMin, TempMax);
        }

        FanManual = clamped.GpuFanPercent is not null;
        if (clamped.GpuFanPercent is { } f)
        {
            FanPercent = f;
        }

        CpuRatioEnabled = clamped.CpuAllCoreRatio is not null;
        if (clamped.CpuAllCoreRatio is { } r)
        {
            CpuRatio = r;
        }

        CpuVoltageEnabled = clamped.CpuVoltageOffsetMv is not null;
        if (clamped.CpuVoltageOffsetMv is { } mv)
        {
            CpuVoltageOffset = mv;
        }

        CpuPowerEnabled = clamped.CpuPowerLimitW is not null;
        if (clamped.CpuPowerLimitW is { } w)
        {
            CpuPowerLimit = w;
        }
    }

    partial void OnSelectedProfileChanged(OverclockProfile? value)
    {
        if (value is not null)
        {
            LoadIntoSliders(value);
            if (!OverclockPresets.IsPreset(value.Name))
            {
                NewProfileName = value.Name;
            }
        }
    }

    [RelayCommand]
    private void ApplyPreset(string? name)
    {
        var preset = OverclockPresets.Get(name);
        if (preset is not null)
        {
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == preset.Name);
            LoadIntoSliders(preset);
        }
    }

    [RelayCommand]
    private async Task ApplyGpu()
    {
        // Only the GPU values from this card are sent; the CPU keeps whatever is applied right now. A profile without
        // CPU values would return CPU settings an earlier apply left live (Intel XTU) to their defaults.
        var profile = BuildProfile(SelectedProfile?.Name ?? "Custom");
        var current = await CurrentlyAppliedAsync();
        profile.CpuAllCoreRatio = current.CpuAllCoreRatio;
        profile.CpuVoltageOffsetMv = current.CpuVoltageOffsetMv;
        profile.CpuPowerLimitW = current.CpuPowerLimitW;
        await ApplyProfileAsync(profile, gpuOnly: true);
    }

    [RelayCommand]
    private async Task ApplyCpu()
    {
        var profile = BuildProfile(SelectedProfile?.Name ?? "Custom");
        if (profile.CpuAllCoreRatio is null && profile.CpuVoltageOffsetMv is null && profile.CpuPowerLimitW is null)
        {
            ErrorMessage = "Tick at least one CPU setting first.";
            return;
        }

        // Only the CPU values from this card are sent; the GPU keeps whatever is applied right now.
        var current = await CurrentlyAppliedAsync();
        profile.GpuCoreOffsetMhz = current.GpuCoreOffsetMhz;
        profile.GpuMemoryOffsetMhz = current.GpuMemoryOffsetMhz;
        profile.GpuPowerLimitPercent = current.GpuPowerLimitPercent;
        profile.GpuTempLimitC = current.GpuTempLimitC;
        profile.GpuFanPercent = current.GpuFanPercent;
        profile.GpuVoltageOffsetMv = current.GpuVoltageOffsetMv;
        await ApplyProfileAsync(profile, gpuOnly: false, cpuOnly: true);
    }

    private async Task<OverclockProfile> CurrentlyAppliedAsync()
    {
        try
        {
            return (await _overclock.GetStateAsync(CancellationToken.None)).Current;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the applied profile; assuming stock.");
            return OverclockProfile.Stock();
        }
    }

    private async Task ApplyProfileAsync(OverclockProfile profile, bool gpuOnly, bool cpuOnly = false)
    {
        var caps = Capabilities;
        if (caps is null)
        {
            ErrorMessage = "Run detection first.";
            return;
        }

        var validation = OverclockSafetyValidator.Validate(profile, caps, Settings.Current.Overclock);
        if (!validation.Success)
        {
            ErrorMessage = validation.Message;
            return;
        }

        var described = profile;
        if (cpuOnly)
        {
            // The dialog lists only what changes; the GPU values are the ones already live.
            described = profile.Clone();
            described.GpuCoreOffsetMhz = 0;
            described.GpuMemoryOffsetMhz = 0;
            described.GpuPowerLimitPercent = 100;
            described.GpuTempLimitC = null;
            described.GpuFanPercent = null;
            described.GpuVoltageOffsetMv = 0;
        }
        else if (gpuOnly)
        {
            // The CPU values are the ones already live and stay as they are.
            described = profile.Clone();
            described.CpuAllCoreRatio = null;
            described.CpuVoltageOffsetMv = null;
            described.CpuPowerLimitW = null;
        }

        var changes = string.Join("\n• ", OverclockPresets.Describe(described));
        bool restore = Settings.Current.RestorePoints.CreateBeforeChanges;
        bool ryzenAdj = caps.CpuMethod == CpuControlMethod.RyzenAdj;
        string message = $"PC Checker will apply:\n\n• {changes}\n\n"
            + (cpuOnly ? "The GPU keeps its current settings. " : string.Empty)
            + (gpuOnly && (profile.CpuAllCoreRatio is not null || profile.CpuVoltageOffsetMv is not null || profile.CpuPowerLimitW is not null) ? "The CPU keeps its current settings. " : string.Empty)
            + (ryzenAdj && !gpuOnly && profile.CpuPowerLimitW is not null ? "CPU power limits set through ryzenadj stay until the next reboot; Reset cannot undo them. " : string.Empty)
            + (restore ? "A restore point is created first. " : string.Empty)
            + "A watchdog file is written so the change is undone automatically if the PC crashes and restarts. "
            + "Clock offsets do not survive a reboot. Save your work before continuing.";
        if (!await _dialogs.ConfirmAsync("Apply these settings?", message, "Apply", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _overclock.ApplyAsync(profile, ct);
            if (result.Success)
            {
                ActiveProfileName = profile.Name;
                StatusMessage = result.Message;
                _notifications.Success("Overclock applied", result.Message);
            }
            else
            {
                ErrorMessage = result.Message;
                _notifications.Error("Overclock not applied", result.Message);
            }

            await LiveTickAsync();
            UpdateAdvancedInfo(Capabilities);
        }, "Applying…");
    }

    [RelayCommand]
    private async Task ResetToDefaults()
    {
        string resetText = Capabilities?.CpuMethod == CpuControlMethod.RyzenAdj
            ? "Every GPU change made from this page is undone and the card returns to its stock settings. Fans return to automatic control. CPU power limits set through ryzenadj cannot be undone from here; they stay until the next reboot."
            : "Every GPU and CPU change made from this page is undone and the card returns to its stock settings. Fans return to automatic control.";
        if (!await _dialogs.ConfirmAsync("Reset to defaults?", resetText, "Reset", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _overclock.ResetAsync(ct);
            if (result.Success)
            {
                ActiveProfileName = null;
                StatusMessage = result.Message;
                LoadIntoSliders(OverclockProfile.Stock());
                _notifications.Success("Back to stock", result.Message);
            }
            else
            {
                ErrorMessage = result.Message;
            }

            await LiveTickAsync();
            UpdateAdvancedInfo(Capabilities);
        }, "Resetting…");
    }

    [RelayCommand]
    private async Task SaveProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0)
        {
            ErrorMessage = "Give the profile a name first.";
            return;
        }

        if (OverclockPresets.IsPreset(name))
        {
            ErrorMessage = "That name belongs to a built-in preset; choose another.";
            return;
        }

        var profile = BuildProfile(name);
        var list = Settings.Current.Overclock.Profiles;
        list.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        list.Add(profile);
        await Settings.SaveAsync();
        RebuildProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == name);
        _notifications.Success("Profile saved", $"\"{name}\" is stored in your settings.");
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (SelectedProfile is null || OverclockPresets.IsPreset(SelectedProfile.Name))
        {
            ErrorMessage = "Built-in presets cannot be deleted. Select one of your own profiles.";
            return;
        }

        var name = SelectedProfile.Name;
        Settings.Current.Overclock.Profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        await Settings.SaveAsync();
        RebuildProfiles();
        SelectedProfile = Profiles.FirstOrDefault();
        _notifications.Info("Profile deleted", $"\"{name}\" was removed.");
    }

    [RelayCommand]
    private void OpenGpuTool() => OpenTool(_gpuTool, _gpuToolPath);

    [RelayCommand]
    private void OpenCpuTool() => OpenTool(_cpuTool, _cpuToolPath);

    [RelayCommand]
    private async Task RaiseLimitsToDriver()
    {
        if (Capabilities?.GpuLimits is not { } limits)
        {
            ErrorMessage = "No driver ranges are available.";
            return;
        }

        int core = Math.Max(Math.Abs(limits.CoreOffsetMinMhz), Math.Abs(limits.CoreOffsetMaxMhz));
        int mem = Math.Max(Math.Abs(limits.MemoryOffsetMinMhz), Math.Abs(limits.MemoryOffsetMaxMhz));
        var msg = $"Your safety limits in Settings will be raised to what the driver allows: core ±{core} MHz, memory ±{mem} MHz, power up to {limits.PowerLimitMaxPercent} %, temperature up to {limits.TempLimitMaxC} °C. Higher values mean a higher chance of crashes. Continue?";
        if (!await _dialogs.ConfirmAsync("Raise safety limits?", msg, "Raise limits", isDanger: true))
        {
            return;
        }

        var s = Settings.Current.Overclock;
        s.MaxGpuCoreOffsetMhz = Math.Max(s.MaxGpuCoreOffsetMhz, core);
        s.MaxGpuMemoryOffsetMhz = Math.Max(s.MaxGpuMemoryOffsetMhz, mem);
        s.MaxGpuPowerLimitPercent = Math.Max(s.MaxGpuPowerLimitPercent, limits.PowerLimitMaxPercent);
        if (limits.TempLimitMaxC > 0)
        {
            s.MaxGpuTempLimitC = Math.Max(s.MaxGpuTempLimitC, limits.TempLimitMaxC);
        }

        await Settings.SaveAsync();
        ApplyGpuLimits(Capabilities);
        UpdateAdvancedInfo(Capabilities);
        _notifications.Warning("Limits raised", "The sliders now reach the driver's full ranges.");
    }

    private void OpenTool(VendorSoftware? tool, string? path)
    {
        if (tool is null)
        {
            return;
        }

        var result = ShellLauncher.Open(path ?? tool.DownloadUrl);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
        else if (path is null)
        {
            _notifications.Info("Download page opened", $"Install {tool.Name}, then press Detect again.");
        }
    }

    private void LoadLimitsFromSettings()
    {
        var s = Settings.Current.Overclock;
        CoreMin = -s.MaxGpuCoreOffsetMhz;
        CoreMax = s.MaxGpuCoreOffsetMhz;
        MemoryMin = -s.MaxGpuMemoryOffsetMhz;
        MemoryMax = s.MaxGpuMemoryOffsetMhz;
        PowerMin = OverclockSafetyValidator.MinGpuPowerLimitPercent;
        PowerMax = s.MaxGpuPowerLimitPercent;
        TempMin = OverclockSafetyValidator.MinGpuTempLimitC;
        TempMax = s.MaxGpuTempLimitC;
        UserLimitsText = $"Core ±{s.MaxGpuCoreOffsetMhz} MHz · Memory ±{s.MaxGpuMemoryOffsetMhz} MHz · Power ≤ {s.MaxGpuPowerLimitPercent} % · GPU temp ≤ {s.MaxGpuTempLimitC} °C · CPU temp ≤ {s.MaxCpuTempC} °C";
    }

    private void ApplyGpuLimits(OverclockCapabilities? caps)
    {
        LoadLimitsFromSettings();
        IsAmdGpu = caps?.GpuVendor == GpuVendor.Amd;
        var limits = caps?.GpuLimits;
        if (limits is null)
        {
            PowerLimitSupported = false;
            TempLimitSupported = false;
            FanSupported = false;
            VoltageSupported = false;
            return;
        }

        VoltageSupported = limits.VoltageOffsetSupported;
        if (VoltageSupported)
        {
            VoltageMin = limits.VoltageOffsetMinMv;
            VoltageMax = limits.VoltageOffsetMaxMv;
            VoltageOffset = Math.Clamp(VoltageOffset, VoltageMin, VoltageMax);
        }

        CoreMin = Math.Max(CoreMin, limits.CoreOffsetMinMhz);
        CoreMax = Math.Min(CoreMax, limits.CoreOffsetMaxMhz);
        MemoryMin = Math.Max(MemoryMin, limits.MemoryOffsetMinMhz);
        MemoryMax = Math.Min(MemoryMax, limits.MemoryOffsetMaxMhz);
        PowerLimitSupported = limits.PowerLimitMaxPercent > limits.PowerLimitMinPercent;
        if (PowerLimitSupported)
        {
            PowerMin = Math.Max(PowerMin, limits.PowerLimitMinPercent);
            PowerMax = Math.Min(PowerMax, limits.PowerLimitMaxPercent);
        }

        TempLimitSupported = limits.TempLimitMaxC > limits.TempLimitMinC;
        if (TempLimitSupported)
        {
            TempMin = Math.Max(TempMin, limits.TempLimitMinC);
            TempMax = Math.Min(TempMax, limits.TempLimitMaxC);
            TempLimit = Math.Clamp(TempLimit, TempMin, TempMax);
        }

        FanSupported = limits.FanControl;
        CoreOffset = Math.Clamp(CoreOffset, CoreMin, CoreMax);
        MemoryOffset = Math.Clamp(MemoryOffset, MemoryMin, MemoryMax);
        PowerLimit = Math.Clamp(PowerLimit, PowerMin, PowerMax);
        CpuPowerOnly = caps?.CpuMethod == CpuControlMethod.RyzenAdj;
    }

    private void UpdateVendorTools(OverclockCapabilities caps)
    {
        _gpuTool = VendorSoftwareCatalog.ForGpu(caps.GpuVendor);
        _gpuToolPath = _gpuTool.Locate(File.Exists);
        GpuToolName = _gpuTool.Name;
        GpuToolPurpose = _gpuTool.Purpose;
        GpuToolInstalled = _gpuToolPath is not null;

        _cpuTool = VendorSoftwareCatalog.ForCpu(caps.CpuName);
        _cpuToolPath = caps.CpuToolPath ?? _cpuTool?.Locate(File.Exists);
        CpuToolKnown = _cpuTool is not null;
        CpuToolName = _cpuTool?.Name ?? "Vendor software";
        CpuToolPurpose = _cpuTool?.Purpose ?? string.Empty;
        CpuToolInstalled = _cpuToolPath is not null;
        OnPropertyChanged(nameof(BiosSteps));
    }

    private void UpdateAdvancedInfo(OverclockCapabilities? caps)
    {
        var l = caps?.GpuLimits;
        RawLimitsText = l is null
            ? "The driver reported no tuning ranges."
            : $"Core offset {l.CoreOffsetMinMhz}…{l.CoreOffsetMaxMhz} MHz · Memory offset {l.MemoryOffsetMinMhz}…{l.MemoryOffsetMaxMhz} MHz · Power {l.PowerLimitMinPercent}…{l.PowerLimitMaxPercent} % · Temperature {l.TempLimitMinC}…{l.TempLimitMaxC} °C · Fan control {(l.FanControl ? (l.FanCurve ? "yes (Overdrive8 curve)" : "yes") : "no")}"
              + (l.VoltageOffsetSupported ? $" · Voltage offset {l.VoltageOffsetMinMv}…{l.VoltageOffsetMaxMv} mV" : string.Empty);
        SessionFilePath = OverclockSessionStore.PathFor(Settings.SettingsDirectory);
        var session = OverclockSessionStore.Load(SessionFilePath);
        SessionFileText = session is null ? "No session file: nothing is applied right now." : OverclockSessionStore.Serialize(session);
    }

    private void RebuildProfiles()
    {
        var current = SelectedProfile?.Name;
        Profiles.Clear();
        foreach (var preset in OverclockPresets.ForVendor(Capabilities?.GpuVendor ?? GpuVendor.Unknown))
        {
            Profiles.Add(preset.Clone());
        }

        foreach (var saved in Settings.Current.Overclock.Profiles.OrderBy(p => p.Name))
        {
            Profiles.Add(saved.Clone());
        }

        if (current is not null)
        {
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == current);
        }
    }
}
