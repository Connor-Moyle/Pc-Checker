using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>Monitoring thresholds, electricity price, Claude, fans, lighting, latency, memory and startup sections.</summary>
public sealed partial class SettingsViewModel
{
    // Monitoring (also editable on the Temperatures page; both bind to the same settings).
    [ObservableProperty] private double _pollIntervalMs;
    [ObservableProperty] private double _historyMinutes;
    [ObservableProperty] private double _cpuWarnC;
    [ObservableProperty] private double _cpuCritC;
    [ObservableProperty] private double _gpuWarnC;
    [ObservableProperty] private double _gpuCritC;
    [ObservableProperty] private double _diskWarnC;
    [ObservableProperty] private double _diskCritC;
    [ObservableProperty] private bool _alertsEnabled;

    // Electricity
    [ObservableProperty] private double _pricePerKwh;
    [ObservableProperty] private string _currencySymbol = "$";

    // Fans
    [ObservableProperty] private bool _fansRevertToAutoOnExit;
    [ObservableProperty] private double _fansMinimumPercent;
    [ObservableProperty] private bool _fansApplyOnStartup;

    // Lighting
    [ObservableProperty] private string _openRgbHost = "127.0.0.1";
    [ObservableProperty] private double _openRgbPort;
    [ObservableProperty] private bool _openRgbAutoConnect;

    // Latency
    [ObservableProperty] private string _pingTargets = string.Empty;
    [ObservableProperty] private double _pingCount;

    // Memory and startup warnings
    [ObservableProperty] private bool _warnOnLowMemory;
    [ObservableProperty] private double _lowMemoryPercent;
    [ObservableProperty] private bool _warnOnManyStartupItems;
    [ObservableProperty] private double _manyStartupItems;

    // Claude (read-only summary; the key and model are managed on the Ask Claude page)
    [ObservableProperty] private string _claudeSummary = string.Empty;
    [ObservableProperty] private bool _claudeShareScanContext;

    private void LoadSectionSettings(AppSettings s)
    {
        PollIntervalMs = s.Monitoring.PollIntervalMs;
        HistoryMinutes = s.Monitoring.HistoryMinutes;
        CpuWarnC = s.Monitoring.CpuWarnC;
        CpuCritC = s.Monitoring.CpuCritC;
        GpuWarnC = s.Monitoring.GpuWarnC;
        GpuCritC = s.Monitoring.GpuCritC;
        DiskWarnC = s.Monitoring.DiskWarnC;
        DiskCritC = s.Monitoring.DiskCritC;
        AlertsEnabled = s.Monitoring.AlertsEnabled;

        PricePerKwh = s.General.PricePerKwh;
        CurrencySymbol = s.General.CurrencySymbol;

        FansRevertToAutoOnExit = s.Fans.RevertToAutoOnExit;
        FansMinimumPercent = s.Fans.MinimumPercent;
        FansApplyOnStartup = s.Fans.ApplyOnStartup;

        OpenRgbHost = s.Lighting.OpenRgbHost;
        OpenRgbPort = s.Lighting.OpenRgbPort;
        OpenRgbAutoConnect = s.Lighting.AutoConnect;

        PingTargets = string.Join(", ", s.Latency.PingTargets);
        PingCount = s.Latency.PingCount;

        WarnOnLowMemory = s.Memory.WarnOnLowMemory;
        LowMemoryPercent = s.Memory.LowMemoryPercent;
        WarnOnManyStartupItems = s.Startup.WarnOnManyStartupItems;
        ManyStartupItems = s.Startup.ManyStartupItems;

        ClaudeShareScanContext = s.Claude.ShareScanContext;
        ClaudeSummary = (string.IsNullOrEmpty(s.Claude.ProtectedApiKey) ? "No API key saved yet. " : "API key saved. ")
            + $"Model {s.Claude.Model}, effort {s.Claude.Effort}.";
    }

    partial void OnPollIntervalMsChanged(double value) => Persist(s => s.Monitoring.PollIntervalMs = (int)Math.Clamp(value, 250, 10_000));
    partial void OnHistoryMinutesChanged(double value) => Persist(s => s.Monitoring.HistoryMinutes = (int)Math.Clamp(value, 1, 24 * 60));
    partial void OnCpuWarnCChanged(double value) => Persist(s => s.Monitoring.CpuWarnC = Math.Clamp(value, 30, 120));
    partial void OnCpuCritCChanged(double value) => Persist(s => s.Monitoring.CpuCritC = Math.Clamp(value, 30, 125));
    partial void OnGpuWarnCChanged(double value) => Persist(s => s.Monitoring.GpuWarnC = Math.Clamp(value, 30, 120));
    partial void OnGpuCritCChanged(double value) => Persist(s => s.Monitoring.GpuCritC = Math.Clamp(value, 30, 125));
    partial void OnDiskWarnCChanged(double value) => Persist(s => s.Monitoring.DiskWarnC = Math.Clamp(value, 25, 90));
    partial void OnDiskCritCChanged(double value) => Persist(s => s.Monitoring.DiskCritC = Math.Clamp(value, 25, 95));
    partial void OnAlertsEnabledChanged(bool value) => Persist(s => s.Monitoring.AlertsEnabled = value);

    partial void OnPricePerKwhChanged(double value) => Persist(s => s.General.PricePerKwh = Math.Clamp(value, 0, 100));
    partial void OnCurrencySymbolChanged(string value) => Persist(s => s.General.CurrencySymbol = string.IsNullOrWhiteSpace(value) ? "$" : value.Trim());

    partial void OnFansRevertToAutoOnExitChanged(bool value) => Persist(s => s.Fans.RevertToAutoOnExit = value);
    partial void OnFansMinimumPercentChanged(double value) => Persist(s => s.Fans.MinimumPercent = Math.Clamp(value, 0, 100));
    partial void OnFansApplyOnStartupChanged(bool value) => Persist(s => s.Fans.ApplyOnStartup = value);

    partial void OnOpenRgbHostChanged(string value) => Persist(s => s.Lighting.OpenRgbHost = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim());
    partial void OnOpenRgbPortChanged(double value) => Persist(s => s.Lighting.OpenRgbPort = (int)Math.Clamp(value, 1, 65535));
    partial void OnOpenRgbAutoConnectChanged(bool value) => Persist(s => s.Lighting.AutoConnect = value);

    partial void OnPingTargetsChanged(string value) => Persist(s => s.Latency.PingTargets = ParsePingTargets(value));
    partial void OnPingCountChanged(double value) => Persist(s => s.Latency.PingCount = (int)Math.Clamp(value, 1, 100));

    partial void OnWarnOnLowMemoryChanged(bool value) => Persist(s => s.Memory.WarnOnLowMemory = value);
    partial void OnLowMemoryPercentChanged(double value) => Persist(s => s.Memory.LowMemoryPercent = (int)Math.Clamp(value, 1, 50));
    partial void OnWarnOnManyStartupItemsChanged(bool value) => Persist(s => s.Startup.WarnOnManyStartupItems = value);
    partial void OnManyStartupItemsChanged(double value) => Persist(s => s.Startup.ManyStartupItems = (int)Math.Clamp(value, 1, 100));

    partial void OnClaudeShareScanContextChanged(bool value) => Persist(s => s.Claude.ShareScanContext = value);

    /// <summary>"gateway, 1.1.1.1; 8.8.8.8" → ["gateway", "1.1.1.1", "8.8.8.8"]; empty input restores the defaults.</summary>
    public static List<string> ParsePingTargets(string? text)
    {
        var targets = (text ?? string.Empty)
            .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return targets.Count == 0 ? ["gateway", "1.1.1.1", "8.8.8.8"] : targets;
    }
}
