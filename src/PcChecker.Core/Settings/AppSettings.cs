using PcChecker.Core.Displays;
using PcChecker.Core.Fans;
using PcChecker.Core.Input;
using PcChecker.Core.Lighting;
using PcChecker.Core.Logging;
using PcChecker.Core.Overclocking;

namespace PcChecker.Core.Settings;

/// <summary>Root of all user settings, persisted as JSON.</summary>
public sealed class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public RestorePointSettings RestorePoints { get; set; } = new();
    public MonitoringSettings Monitoring { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public ClaudeSettings Claude { get; set; } = new();
    public CleanupSettings Cleanup { get; set; } = new();
    public OverclockSettings Overclock { get; set; } = new();
    public FanSettings Fans { get; set; } = new();
    public LightingSettings Lighting { get; set; } = new();
    public DisplaySettings Displays { get; set; } = new();
    public InputSettings Input { get; set; } = new();
    public MemorySettings Memory { get; set; } = new();
    public LatencySettings Latency { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();

    /// <summary>Returns a fresh instance with every value at its default.</summary>
    public static AppSettings CreateDefault() => new();
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class GeneralSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool AdvancedMode { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool CheckHealthOnStartup { get; set; } = true;
    public double PricePerKwh { get; set; } = 0.30;
    public string CurrencySymbol { get; set; } = "$";
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}

public enum UpdateChannel
{
    Stable,
    Beta,
}

public sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;
    public bool AutoDownload { get; set; }
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
    public DateTimeOffset? LastCheckedUtc { get; set; }
}

public sealed class RestorePointSettings
{
    public bool CreateBeforeChanges { get; set; } = true;
    public bool CreateAfterChanges { get; set; }
    public bool BypassFrequencyLimit { get; set; } = true;
}

public sealed class MonitoringSettings
{
    public int PollIntervalMs { get; set; } = 1000;
    public int HistoryMinutes { get; set; } = 30;
    public double CpuWarnC { get; set; } = 85;
    public double CpuCritC { get; set; } = 95;
    public double GpuWarnC { get; set; } = 83;
    public double GpuCritC { get; set; } = 92;
    public double DiskWarnC { get; set; } = 55;
    public double DiskCritC { get; set; } = 65;
    public bool AlertsEnabled { get; set; } = true;
}

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Custom,
}

public enum OverlayOrientation
{
    Vertical,
    Horizontal,
}

/// <summary>One metric line shown in the overlay.</summary>
public sealed class OverlayMetric
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Label { get; set; } = string.Empty;
    public int Order { get; set; }
}

/// <summary>Named snapshot of overlay settings the user can switch between.</summary>
public sealed class OverlayPreset
{
    public string Name { get; set; } = string.Empty;
    public List<OverlayMetric> Metrics { get; set; } = [];
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;
    public double Opacity { get; set; } = 0.85;
    public double Scale { get; set; } = 1.0;
    public OverlayOrientation Orientation { get; set; } = OverlayOrientation.Vertical;
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+O";
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;
    public double X { get; set; }
    public double Y { get; set; }
    public double Opacity { get; set; } = 0.85;
    public double Scale { get; set; } = 1.0;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 14;
    public OverlayOrientation Orientation { get; set; } = OverlayOrientation.Vertical;
    public bool ShowBackground { get; set; } = true;
    public string BackgroundColor { get; set; } = "#CC101010";
    public string TextColor { get; set; } = "#FFFFFFFF";
    public string AccentColor { get; set; } = "#FF4CC2FF";
    public bool ShowSparklines { get; set; }
    public int UpdateIntervalMs { get; set; } = 1000;
    public List<OverlayMetric> Metrics { get; set; } = OverlayMetricIds.CreateDefaultMetrics();
    public List<OverlayPreset> Presets { get; set; } = [];
}

/// <summary>Well-known overlay metric identifiers.</summary>
public static class OverlayMetricIds
{
    public const string CpuTemp = "cpu.temp";
    public const string CpuLoad = "cpu.load";
    public const string CpuClock = "cpu.clock";
    public const string CpuPower = "cpu.power";
    public const string GpuTemp = "gpu.temp";
    public const string GpuHotspot = "gpu.hotspot";
    public const string GpuLoad = "gpu.load";
    public const string GpuClock = "gpu.clock";
    public const string GpuMemClock = "gpu.memclock";
    public const string GpuVram = "gpu.vram";
    public const string GpuPower = "gpu.power";
    public const string GpuFan = "gpu.fan";
    public const string RamUsed = "ram.used";
    public const string RamPercent = "ram.percent";
    public const string DiskTemp = "disk.temp";
    public const string DiskActivity = "disk.activity";
    public const string NetDown = "net.down";
    public const string NetUp = "net.up";
    public const string BatteryPercent = "battery.percent";
    public const string BatteryWatts = "battery.watts";
    public const string SystemPower = "system.power";
    public const string Time = "time";

    /// <summary>Every metric id with a friendly default label.</summary>
    public static IReadOnlyList<(string Id, string Label)> All { get; } =
    [
        (CpuTemp, "CPU temp"), (CpuLoad, "CPU load"), (CpuClock, "CPU clock"), (CpuPower, "CPU power"),
        (GpuTemp, "GPU temp"), (GpuHotspot, "GPU hot spot"), (GpuLoad, "GPU load"), (GpuClock, "GPU clock"),
        (GpuMemClock, "GPU memory clock"), (GpuVram, "VRAM used"), (GpuPower, "GPU power"), (GpuFan, "GPU fan"),
        (RamUsed, "RAM used"), (RamPercent, "RAM %"), (DiskTemp, "Disk temp"), (DiskActivity, "Disk activity"),
        (NetDown, "Download"), (NetUp, "Upload"), (BatteryPercent, "Battery"), (BatteryWatts, "Battery draw"),
        (SystemPower, "System power"), (Time, "Clock"),
    ];

    public static List<OverlayMetric> CreateDefaultMetrics()
    {
        string[] enabled = [CpuTemp, CpuLoad, GpuTemp, GpuLoad, RamUsed];
        var list = new List<OverlayMetric>();
        int order = 0;
        foreach (var (id, label) in All)
        {
            list.Add(new OverlayMetric { Id = id, Label = label, Enabled = enabled.Contains(id), Order = order++ });
        }

        return list;
    }
}

public enum AssistantEffort
{
    Low,
    Medium,
    High,
}

public sealed class ClaudeSettings
{
    public string Model { get; set; } = "claude-opus-5-5";
    public AssistantEffort Effort { get; set; } = AssistantEffort.Medium;
    public bool ShareScanContext { get; set; } = true;
    public int MaxContextChars { get; set; } = 12000;

    /// <summary>API key protected with DPAPI (base64). Null when not configured.</summary>
    public string? ProtectedApiKey { get; set; }
}

public sealed class CleanupSettings
{
    public List<string> EnabledCategoryIds { get; set; } = [];
    public int SkipFilesNewerThanMinutes { get; set; } = 60;
}

public sealed class OverclockSettings
{
    public bool ConsentAccepted { get; set; }
    public DateTimeOffset? ConsentAcceptedUtc { get; set; }
    public int MaxGpuCoreOffsetMhz { get; set; } = 200;
    public int MaxGpuMemoryOffsetMhz { get; set; } = 1000;
    public int MaxGpuPowerLimitPercent { get; set; } = 115;
    public int MaxGpuTempLimitC { get; set; } = 87;
    public int MaxCpuTempC { get; set; } = 90;
    public List<OverclockProfile> Profiles { get; set; } = [];
    public string? ActiveProfileName { get; set; }
    public bool LastSessionClean { get; set; } = true;
}

public sealed class MemorySettings
{
    public bool WarnOnLowMemory { get; set; } = true;
    public int LowMemoryPercent { get; set; } = 10;
}

public sealed class LatencySettings
{
    /// <summary>Hosts to ping; "gateway" means the default gateway.</summary>
    public List<string> PingTargets { get; set; } = ["gateway", "1.1.1.1", "8.8.8.8"];
    public int PingCount { get; set; } = 10;
}

public sealed class StartupSettings
{
    public bool WarnOnManyStartupItems { get; set; } = true;
    public int ManyStartupItems { get; set; } = 10;
}
