using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Audio;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Drivers;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Reports;

namespace PcChecker.App.ViewModels;

/// <summary>Quick actions, full scan, report export, the controls row, hardware support and the system summary.</summary>
public sealed partial class DashboardViewModel
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(6);
    private DriverScanResult? _lastDrivers;

    // Controls row
    [ObservableProperty] private string _fansText = "—";
    [ObservableProperty] private string? _fansSubtitle;
    [ObservableProperty] private string _lightingText = "—";
    [ObservableProperty] private string? _lightingSubtitle;
    [ObservableProperty] private string _displaysText = "—";
    [ObservableProperty] private string? _displaysSubtitle;
    [ObservableProperty] private string _audioText = "—";
    [ObservableProperty] private string? _audioSubtitle;

    // Hardware support card
    [ObservableProperty] private string _supportGpuText = "Not detected yet";
    [ObservableProperty] private string _supportCpuText = "Not detected yet";
    [ObservableProperty] private string _supportMemoryText = "Not detected yet";
    [ObservableProperty] private string? _supportNote;

    [RelayCommand]
    private Task FullScanAsync() => RunBusyAsync(async ct =>
    {
        var steps = new (string Name, Func<CancellationToken, Task> Run)[]
        {
            ("Hardware", c => _hardware.ScanAsync(Progress("Hardware", 1), c)),
            ("Health", c => _health.AnalyzeAsync(Progress("Health", 2), c)),
            ("Performance", c => _performance.AnalyzeAsync(Progress("Performance", 3), c)),
            ("Software", c => _software.ScanAsync(Progress("Software", 4), c)),
            ("Drivers", async c => _lastDrivers = await _drivers.ScanInstalledAsync(Progress("Drivers", 5), c)),
        };
        var failed = new List<string>();
        for (int i = 0; i < steps.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            BusyText = $"Full scan {i + 1}/{steps.Length}: {steps[i].Name}…";
            try
            {
                await steps[i].Run(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Full scan step {Step} failed.", steps[i].Name);
                failed.Add(steps[i].Name);
            }
        }

        ApplyHealth(_health.Last);
        ApplySummary();
        ApplyLiveStats(_sensors.Latest);
        RebuildAlerts();
        await RefreshRecentActionsAsync();
        LastScanText = "Full scan finished " + Converters.RelativeTimeConverter.Format(DateTimeOffset.Now, DateTimeOffset.Now);
        if (failed.Count == 0)
        {
            _notifications.Success("Full scan complete", _health.Last is { } h ? $"Health score {h.Score}/100 — {ReportText.ScoreLabel(h.Score)}." : "All scans finished.");
        }
        else
        {
            _notifications.Warning("Full scan finished with gaps", "Could not complete: " + string.Join(", ", failed) + ". See Logs for details.");
        }

        IProgress<ProgressUpdate> Progress(string name, int index) => new Progress<ProgressUpdate>(p =>
            DispatcherHelper.PostToUi(() => BusyText = $"Full scan {index}/5: {name} — {p.Stage}" + (p.Percent is { } pc ? $" ({pc:0}%)" : string.Empty)));
    }, "Starting full scan…");

    [RelayCommand]
    private Task RefreshNowAsync() => RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private Task RunHealthCheckAsync() => RunBusyAsync(async ct =>
    {
        await _health.AnalyzeAsync(new Progress<ProgressUpdate>(p => DispatcherHelper.PostToUi(() => BusyText = "Checking health: " + p.Stage)), ct);
        ApplyHealth(_health.Last);
        RebuildAlerts();
    }, "Checking health…");

    [RelayCommand]
    private Task ExportReportAsync() => RunBusyAsync(async ct =>
    {
        var bundle = BuildBundle();
        if (bundle.Hardware is null && bundle.Health is null && bundle.Performance is null && bundle.Software is null && bundle.Drivers is null && bundle.Sensors is null)
        {
            _notifications.Warning("Nothing to export yet", "Run a full scan first so the report has something to say.");
            return;
        }

        var stem = ReportText.SuggestedFileName(bundle.Hardware?.System?.Name ?? Environment.MachineName, bundle.Generated);
        var path = await _dialogs.SaveFileAsync("Export full report", stem + ".html",
            "HTML report (*.html)|*.html|JSON data (*.json)|*.json|Markdown (*.md)|*.md");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var format = HtmlReportService.FormatFromPath(path);
        BusyText = $"Writing {format} report…";
        var written = await _reports.ExportAsync(bundle, format, path, ct);
        _notifications.Success("Report saved", written);
        bool open = await _dialogs.ConfirmAsync("Report saved", $"The report was written to:\n{written}\n\nOpen it now?", "Open");
        if (open)
        {
            var result = ShellLauncher.Open(written);
            if (!result.Success)
            {
                ErrorMessage = result.Message;
            }
        }
    }, "Building report…");

    [RelayCommand] private void GoToCleanup() => _navigation.Navigate(typeof(CleanupPage));
    [RelayCommand] private void GoToDrivers() => _navigation.Navigate(typeof(DriversPage));
    [RelayCommand] private void GoToOptimize() => _navigation.Navigate(typeof(OptimizePage));
    [RelayCommand] private void GoToSensors() => _navigation.Navigate(typeof(SensorsPage));
    [RelayCommand] private void GoToHealth() => _navigation.Navigate(typeof(HealthPage));
    [RelayCommand] private void GoToSettings() => _navigation.Navigate(typeof(SettingsPage));
    [RelayCommand] private void GoToOverclock() => _navigation.Navigate(typeof(OverclockPage));
    [RelayCommand] private void GoToLighting() => _navigation.Navigate(typeof(LightingPage));
    [RelayCommand] private void GoToLogs() => _navigation.Navigate(typeof(LogsPage));
    [RelayCommand] private void GoToHardware() => _navigation.Navigate(typeof(HardwarePage));

    [RelayCommand]
    private void OpenAlert(DashboardAlert? alert)
    {
        if (alert?.Page is { } page)
        {
            _navigation.Navigate(page);
        }
    }

    [RelayCommand]
    private void DismissAlert(DashboardAlert? alert)
    {
        if (alert is null)
        {
            return;
        }

        foreach (var notice in StartupNotices.All.Where(n => n.Title == alert.Title && n.Message == alert.Message).ToList())
        {
            StartupNotices.Remove(notice);
        }

        Alerts.Remove(alert);
        OnPropertyChanged(nameof(HasAlerts));
        OnPropertyChanged(nameof(HasNoAlerts));
    }

    private ReportBundle BuildBundle() =>
        new(_hardware.Last, _health.Last, _performance.Last, _software.Last, _lastDrivers, _sensors.Latest, DateTimeOffset.Now, _updates.CurrentVersion);

    private async Task RefreshControlsAsync(CancellationToken ct)
    {
        try
        {
            if (!_fans.IsAvailable)
            {
                FansText = "Not available";
                FansSubtitle = _fans.UnavailableReason ?? "No controllable fans were found.";
            }
            else
            {
                var fans = await _fans.GetFansAsync(ct).WaitAsync(ControlTimeout, ct);
                FansText = _fans.ActiveProfile?.Name ?? "Auto";
                var cpuFan = fans.FirstOrDefault(f => f.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)) ?? fans.FirstOrDefault();
                FansSubtitle = fans.Count == 0 ? "No fans reported" : $"{fans.Count} fan{(fans.Count == 1 ? string.Empty : "s")}" + (cpuFan?.Rpm is { } rpm ? $" · {cpuFan.Name} {rpm:0} RPM" : string.Empty);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FansText = "Unknown";
            FansSubtitle = "Fan information could not be read.";
        }

        try
        {
            LightingText = _lighting.IsConnected ? "Connected" : _lighting.IsOpenRgbInstalled ? "OpenRGB not running" : "OpenRGB not installed";
            LightingSubtitle = _lighting.Status;
            if (_lighting.IsConnected)
            {
                var devices = await _lighting.GetDevicesAsync(ct).WaitAsync(ControlTimeout, ct);
                LightingSubtitle = $"{devices.Count} RGB device{(devices.Count == 1 ? string.Empty : "s")}" + (Settings.Current.Lighting.ActiveProfileName is { } p ? $" · profile {p}" : string.Empty);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LightingText = "Unknown";
            LightingSubtitle = "Lighting status could not be read.";
        }

        try
        {
            var displays = await _displays.GetDisplaysAsync(ct).WaitAsync(ControlTimeout, ct);
            DisplaysText = displays.Count == 0 ? "None found" : $"{displays.Count} display{(displays.Count == 1 ? string.Empty : "s")}";
            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            DisplaysSubtitle = primary is null ? null : $"{primary.Current} · {(primary.HdrEnabled ? "HDR on" : "HDR off")}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            DisplaysText = "Unknown";
            DisplaysSubtitle = "Display information could not be read.";
        }

        try
        {
            var devices = await _audio.GetDevicesAsync(ct).WaitAsync(ControlTimeout, ct);
            var output = devices.FirstOrDefault(d => d.Flow == AudioFlow.Render && d.IsDefault);
            if (output is null)
            {
                AudioText = "No output device";
                AudioSubtitle = "Check the Audio page.";
            }
            else
            {
                AudioText = output.Name;
                AudioSubtitle = output.Muted ? "Muted" : $"{output.VolumePercent:0} % volume" + (output.InterfaceName is { } i ? " · " + i : string.Empty);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AudioText = "Unknown";
            AudioSubtitle = "Audio devices could not be read.";
        }
    }

    private async Task RefreshRecentActionsAsync()
    {
        try
        {
            var records = await _journal.ReadAsync(5);
            DispatcherHelper.RunOnUi(() =>
            {
                RecentActions.Clear();
                foreach (var record in records.OrderByDescending(r => r.Timestamp).Take(5))
                {
                    RecentActions.Add(record);
                }

                OnPropertyChanged(nameof(HasRecentActions));
                OnPropertyChanged(nameof(HasNoRecentActions));
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Action journal could not be read.");
        }
    }

    private void ApplySummary()
    {
        SystemSummary.Clear();
        var hw = _hardware.Last;
        if (hw is not null)
        {
            Add("Computer", ReportText.Join(hw.System?.Name, hw.System?.Manufacturer is { } m && hw.System?.Model is { } mo ? $"({m} {mo})" : null));
            Add("Processor", hw.Processors.FirstOrDefault() is { } cpu ? cpu.Name + (cpu.Cores is { } c ? $" · {c} cores / {cpu.LogicalProcessors} threads" : string.Empty) : null);
            Add("Graphics", string.Join(", ", hw.Graphics.Where(g => !g.IsIntegrated).Concat(hw.Graphics.Where(g => g.IsIntegrated)).Select(g => g.Name + (g.AdapterRamBytes is { } vram ? $" ({ByteSize.Format(vram)})" : string.Empty))));
            Add("Memory", hw.TotalMemoryBytes is { } total ? $"{ByteSize.Format(total)} · {hw.MemoryModules.Count} module{(hw.MemoryModules.Count == 1 ? string.Empty : "s")}" + (hw.MemoryModules.FirstOrDefault()?.ConfiguredClockSpeedMhz is { } mhz ? $" at {mhz} MT/s" : string.Empty) : null);
            Add("Storage", string.Join(", ", hw.PhysicalDisks.Select(d => $"{ReportText.Or(d.Model, d.Name)} {ByteSize.Format(d.SizeBytes)}")));
            Add("Motherboard", ReportText.Join(hw.Motherboard?.Manufacturer, hw.Motherboard?.Product));
            Add("Windows", ReportText.Join(hw.OperatingSystem?.Caption ?? hw.OperatingSystem?.Name, hw.OperatingSystem?.DisplayVersion, hw.OperatingSystem?.BuildNumber is { } b ? $"(build {b})" : null));
        }

        UptimeText = ComputeUptime();
        Add("Up for", UptimeText);
        ApplyDiskFree();
        OnPropertyChanged(nameof(HasSystemSummary));

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                SystemSummary.Add(new KeyValuePair<string, string>(key, value));
            }
        }
    }

    /// <summary>
    /// Fallback for the fullest drive tile when the Disk space service has not answered: the drives from the hardware
    /// scan, or from the last speed check.
    /// </summary>
    private void ApplyDiskFree()
    {
        if (HasFullestDrive)
        {
            return;
        }

        var drives = (_hardware.Last?.LogicalDrives ?? [])
            .Where(d => d is { SizeBytes: > 0, FreeSpaceBytes: not null })
            .Select(d => new DriveSpaceInfo(d.Name.TrimEnd('\\') + "\\", d.VolumeName ?? string.Empty, d.FileSystem ?? string.Empty, d.SizeBytes!.Value,
                d.FreeSpaceBytes!.Value, d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (drives.Count == 0 && _performance.Last is { } perf)
        {
            drives = perf.Drives.Where(d => d.Total > 0)
                .Select(d => new DriveSpaceInfo(d.Drive.TrimEnd('\\') + "\\", string.Empty, string.Empty, d.Total, d.Free, d.Drive.StartsWith("C", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        ApplyFullestDrive(drives);
    }

    private void ApplySupport(OverclockCapabilities? caps)
    {
        SupportRows.Clear();
        if (caps is null)
        {
            SupportGpuText = "Open the Overclock page to detect";
            SupportCpuText = "Open the Overclock page to detect";
            SupportMemoryText = "Unknown";
            SupportNote = null;
        }
        else
        {
            SupportGpuText = (caps.GpuName ?? caps.GpuVendor.ToString()) + " — " + caps.GpuMethod switch
            {
                GpuControlMethod.NvApi => "full control (NVAPI)",
                GpuControlMethod.AmdAdl => "experimental control (AMD ADL)",
                GpuControlMethod.IntelIgcl => "experimental control (Intel IGCL)",
                _ => "monitor only",
            };
            SupportCpuText = (caps.CpuName ?? "CPU") + " — " + caps.CpuMethod switch
            {
                CpuControlMethod.IntelXtuCli => "tuning through Intel XTU",
                CpuControlMethod.RyzenMaster => "tuning through AMD Ryzen Master",
                CpuControlMethod.RyzenAdj => "power limits through ryzenadj",
                CpuControlMethod.RyzenMasterCli => "tuning through AMD Ryzen Master",
                _ => "set in the BIOS (monitor only)",
            };
            SupportMemoryText = caps.XmpState switch
            {
                "Enabled" => "XMP/EXPO on" + (caps.MemoryConfiguredMhz is { } on ? $" · running at {on} MT/s" : string.Empty),
                "Disabled" => "XMP/EXPO off" + (caps.MemoryConfiguredMhz is { } cfg && caps.MemoryMaxMhz is { } max ? $" · {cfg} of {max} MT/s" : string.Empty),
                _ => "XMP/EXPO state unknown",
            };
            foreach (var (feature, _, level, note) in caps.SupportMatrix.Take(6))
            {
                SupportRows.Add(new SupportRow(feature, level, note));
            }

            SupportNote = caps.Notes.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasSupportRows));
    }
}
