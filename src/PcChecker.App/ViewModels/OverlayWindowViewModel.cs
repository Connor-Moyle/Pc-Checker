using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Power;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>One line in the overlay: label, formatted value, colour and a short history for the sparkline.</summary>
public sealed partial class OverlayMetricItem : ObservableObject
{
    public const int HistoryLength = 60;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _valueText = OverlayMetricFormatter.Placeholder;

    [ObservableProperty]
    private Brush _valueBrush = Brushes.White;

    [ObservableProperty]
    private bool _showSparkline;

    public OverlayMetricItem(string id, string label)
    {
        Id = id;
        _label = label;
        IsTemperature = OverlayMetricFormatter.IsTemperature(id);
        IsClock = id == OverlayMetricIds.Time;
    }

    public string Id { get; }

    public bool IsTemperature { get; }

    public bool IsClock { get; }

    /// <summary>Last values, oldest first (bound by the sparkline).</summary>
    public ObservableCollection<double> History { get; } = [];

    /// <summary>The most recent raw value (null when the metric has no source).</summary>
    public double? RawValue { get; private set; }

    public void Push(double? value)
    {
        RawValue = value;
        if (value is null || double.IsNaN(value.Value))
        {
            return;
        }

        History.Add(value.Value);
        while (History.Count > HistoryLength)
        {
            History.RemoveAt(0);
        }
    }
}

/// <summary>
/// Shared state for the overlay window and the page's live preview: the metric lines with live values, plus every
/// appearance setting as bindable properties. Updated on a UI timer at the configured interval.
/// </summary>
public sealed partial class OverlayWindowViewModel : ObservableObject, IDisposable
{
    private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)));
    private static readonly Brush CritBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));

    private readonly ISensorService _sensors;
    private readonly ILogger<OverlayWindowViewModel> _logger;
    private readonly OverlayMetricResolver _resolver;
    private readonly DispatcherTimer _timer;
    private MonitoringSettings _thresholds = new();
    private int _consumers;
    private bool _sensorsStarted;

    [ObservableProperty]
    private double _opacity = 0.85;

    [ObservableProperty]
    private double _scale = 1.0;

    [ObservableProperty]
    private FontFamily _fontFamily = new("Segoe UI");

    [ObservableProperty]
    private double _fontSize = 14;

    [ObservableProperty]
    private bool _isHorizontal;

    [ObservableProperty]
    private bool _showBackground = true;

    [ObservableProperty]
    private Brush _backgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x10)));

    [ObservableProperty]
    private Brush _textBrush = Brushes.White;

    [ObservableProperty]
    private Brush _accentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)));

    [ObservableProperty]
    private bool _showSparklines;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private int _updateIntervalMs = 1000;

    public OverlayWindowViewModel(ISensorService sensors, IPowerService power, ILogger<OverlayWindowViewModel> logger)
    {
        _sensors = sensors;
        _logger = logger;
        _resolver = new OverlayMetricResolver(sensors, power);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Enabled metrics in display order.</summary>
    public ObservableCollection<OverlayMetricItem> Items { get; } = [];

    /// <summary>True while at least one consumer (window or preview) wants live updates.</summary>
    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Applies the user's overlay settings: rebuilds the metric list and refreshes the appearance.</summary>
    public void ApplySettings(OverlaySettings overlay, MonitoringSettings monitoring)
    {
        _thresholds = monitoring;
        Opacity = Math.Clamp(overlay.Opacity, 0.1, 1.0);
        Scale = Math.Clamp(overlay.Scale, 0.5, 3.0);
        FontFamily = SafeFont(overlay.FontFamily);
        FontSize = Math.Clamp(overlay.FontSize, 8, 48);
        IsHorizontal = overlay.Orientation == OverlayOrientation.Horizontal;
        ShowBackground = overlay.ShowBackground;
        BackgroundBrush = ParseBrush(overlay.BackgroundColor, Color.FromArgb(0xCC, 0x10, 0x10, 0x10));
        TextBrush = ParseBrush(overlay.TextColor, Colors.White);
        AccentBrush = ParseBrush(overlay.AccentColor, Color.FromRgb(0x4C, 0xC2, 0xFF));
        ShowSparklines = overlay.ShowSparklines;
        UpdateIntervalMs = Math.Clamp(overlay.UpdateIntervalMs, 250, 10000);
        _timer.Interval = TimeSpan.FromMilliseconds(UpdateIntervalMs);

        var wanted = OverlayPresetCatalog.EnabledInOrder(overlay.Metrics);
        bool sameIds = wanted.Count == Items.Count && wanted.Select(m => m.Id).SequenceEqual(Items.Select(i => i.Id));
        if (sameIds)
        {
            for (int i = 0; i < wanted.Count; i++)
            {
                Items[i].Label = wanted[i].Label;
                Items[i].ShowSparkline = ShowSparklines;
            }
        }
        else
        {
            var existing = Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
            Items.Clear();
            foreach (var metric in wanted)
            {
                var item = existing.TryGetValue(metric.Id, out var old) ? old : new OverlayMetricItem(metric.Id, metric.Label);
                item.Label = metric.Label;
                item.ShowSparkline = ShowSparklines;
                Items.Add(item);
            }
        }

        HasItems = Items.Count > 0;
        Tick();
    }

    /// <summary>Starts live updates. Each caller must later call <see cref="Stop"/> once.</summary>
    public void Start()
    {
        _consumers++;
        if (!_sensorsStarted)
        {
            _sensorsStarted = true;
            try
            {
                _sensors.Start();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sensor service could not be started for the overlay.");
            }
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
            Tick();
        }
    }

    /// <summary>Stops live updates once the last consumer lets go.</summary>
    public void Stop()
    {
        _consumers = Math.Max(0, _consumers - 1);
        if (_consumers == 0)
        {
            _timer.Stop();
        }
    }

    /// <summary>Refreshes every metric line now.</summary>
    public void Tick()
    {
        if (Items.Count == 0)
        {
            return;
        }

        try
        {
            var values = _resolver.ResolveAll(Items.Where(i => !i.IsClock).Select(i => i.Id));
            foreach (var item in Items)
            {
                if (item.IsClock)
                {
                    item.ValueText = OverlayMetricFormatter.FormatTime(DateTime.Now);
                    item.ValueBrush = AccentBrush;
                    continue;
                }

                var value = values.TryGetValue(item.Id, out var v) ? v : OverlayMetricValue.None;
                item.Push(value.Value);
                item.ValueText = OverlayMetricFormatter.Format(item.Id, value.Value, value.Unit);
                item.ValueBrush = BrushFor(item, value.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Overlay tick failed.");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _resolver.Dispose();
    }

    /// <summary>Threshold colour for temperatures (yellow at warn, red at critical); accent otherwise.</summary>
    public Brush BrushFor(OverlayMetricItem item, double? value)
    {
        if (!item.IsTemperature || value is null)
        {
            return AccentBrush;
        }

        var (warn, crit) = item.Id switch
        {
            OverlayMetricIds.CpuTemp => (_thresholds.CpuWarnC, _thresholds.CpuCritC),
            OverlayMetricIds.GpuTemp => (_thresholds.GpuWarnC, _thresholds.GpuCritC),
            OverlayMetricIds.GpuHotspot => (_thresholds.GpuWarnC + 10, _thresholds.GpuCritC + 10),
            OverlayMetricIds.DiskTemp => (_thresholds.DiskWarnC, _thresholds.DiskCritC),
            _ => (double.MaxValue, double.MaxValue),
        };

        if (value >= crit)
        {
            return CritBrush;
        }

        return value >= warn ? WarnBrush : AccentBrush;
    }

    partial void OnShowSparklinesChanged(bool value)
    {
        foreach (var item in Items)
        {
            item.ShowSparkline = value;
        }
    }

    private static FontFamily SafeFont(string? name)
    {
        try
        {
            return string.IsNullOrWhiteSpace(name) ? new FontFamily("Segoe UI") : new FontFamily(name);
        }
        catch
        {
            return new FontFamily("Segoe UI");
        }
    }

    /// <summary>Parses "#AARRGGBB"/"#RRGGBB" text into a frozen brush; falls back to the default colour.</summary>
    public static Brush ParseBrush(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex.Trim()) is Color color)
            {
                return Freeze(new SolidColorBrush(color));
            }
        }
        catch
        {
            // Invalid text: use the fallback.
        }

        return Freeze(new SolidColorBrush(fallback));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
