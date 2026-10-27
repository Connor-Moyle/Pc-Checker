using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Gaming;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>One row in the metrics list on the Overlay page.</summary>
public sealed partial class OverlayMetricRow : ObservableObject
{
    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private int _order;

    public OverlayMetricRow(string id, string defaultLabel)
    {
        Id = id;
        DefaultLabel = defaultLabel;
        _label = defaultLabel;
    }

    public string Id { get; }

    public string DefaultLabel { get; }

    /// <summary>What the value looks like, e.g. "°C" — shown as a hint next to the label.</summary>
    public string UnitHint => Id == OverlayMetricIds.Time ? "HH:mm:ss" : OverlayMetricFormatter.Unit(Id);

    public event EventHandler? Changed;

    partial void OnEnabledChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnLabelChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>One preset (built-in or saved by the user) on the Overlay page.</summary>
public sealed record OverlayPresetRow(string Name, bool IsBuiltIn, string Summary);

/// <summary>
/// View model for the Overlay page: every overlay setting as a bindable property, the metric list with reordering,
/// presets, the hotkey and a live preview. Changes apply to the overlay immediately and are saved shortly after.
/// Game profiles live in <c>OverlayViewModel.GameProfiles.cs</c>, the FPS counter status in <c>OverlayViewModel.FrameRate.cs</c>.
/// </summary>
public sealed partial class OverlayViewModel : ViewModelBase
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly IOverlayService _overlay;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ILogger<OverlayViewModel> _logger;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;
    private bool _pageVisible;
    private bool _previewRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayStateText))]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    private bool _isOverlayVisible;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private string _hotkeyText = "Ctrl+Shift+O";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkeyError))]
    private string? _hotkeyError;

    [ObservableProperty]
    private string _hotkeyStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomCorner))]
    private OverlayCorner _corner;

    [ObservableProperty]
    private double _customX;

    [ObservableProperty]
    private double _customY;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityPercentText))]
    private double _opacity = 0.85;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleText))]
    private double _scale = 1.0;

    [ObservableProperty]
    private string _fontFamily = "Segoe UI";

    [ObservableProperty]
    private double _fontSize = 14;

    [ObservableProperty]
    private OverlayOrientation _orientation;

    [ObservableProperty]
    private bool _showBackground = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundPreview))]
    private string _backgroundColor = "#CC101010";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextPreview))]
    private string _textColor = "#FFFFFFFF";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccentPreview))]
    private string _accentColor = "#FF4CC2FF";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColorError))]
    private string? _colorError;

    [ObservableProperty]
    private bool _showSparklines;

    [ObservableProperty]
    private double _updateIntervalMs = 1000;

    [ObservableProperty]
    private string _newPresetName = string.Empty;

    [ObservableProperty]
    private int _enabledMetricCount;

    public OverlayViewModel(ISettingsService settings, IOverlayService overlay, INotificationService notifications, IDialogService dialogs, ILogger<OverlayViewModel> logger,
        GameProfileSwitcher switcher, IGameProfileService gameProfiles, IForegroundAppTracker tracker, IFrameRateMonitor frames, IFanControlService fans)
        : base(settings)
    {
        _overlay = overlay;
        _notifications = notifications;
        _dialogs = dialogs;
        _logger = logger;
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveNowAsync();
        };

        _overlay.VisibilityChanged += (_, _) => DispatcherHelper.PostToUi(() => IsOverlayVisible = _overlay.IsVisible);
        _overlay.EditModeChanged += (_, _) => DispatcherHelper.PostToUi(() => SetEditModeQuietly(_overlay.IsEditMode));
        settings.Changed += (_, s) => DispatcherHelper.PostToUi(() => OnExternalSettingsChanged(s));
        _isOverlayVisible = overlay.IsVisible;
        _isEditMode = overlay.IsEditMode;
        InitGaming(switcher, gameProfiles, tracker, frames, fans);
        ReloadWhenReplaced(s => s.Overlay, () =>
        {
            LoadFromSettings();
            LoadGameProfileOptions();
        });
        ReloadWhenReplaced(s => s.Gaming, RefreshGameProfiles);
        ReloadWhenReplaced(s => s.Fans, LoadGameProfileOptions);
    }

    /// <summary>The shared live overlay state, rendered by the preview on the page.</summary>
    public OverlayWindowViewModel Preview => _overlay.WindowViewModel;

    public ObservableCollection<OverlayMetricRow> Metrics { get; } = [];

    public ObservableCollection<OverlayPresetRow> Presets { get; } = [];

    public IReadOnlyList<OverlayCorner> Corners { get; } = Enum.GetValues<OverlayCorner>();

    public IReadOnlyList<OverlayOrientation> Orientations { get; } = Enum.GetValues<OverlayOrientation>();

    public IReadOnlyList<string> FontFamilies { get; } = ["Segoe UI", "Segoe UI Variable", "Consolas", "Cascadia Mono", "Arial", "Verdana", "Tahoma", "Calibri"];

    public bool IsCustomCorner => Corner == OverlayCorner.Custom;

    public bool HasHotkeyError => !string.IsNullOrWhiteSpace(HotkeyError);

    public bool HasColorError => !string.IsNullOrWhiteSpace(ColorError);

    public string OverlayStateText => IsOverlayVisible ? "The overlay is showing on your screen." : "The overlay is hidden.";

    public string ToggleButtonText => IsOverlayVisible ? "Hide overlay" : "Show overlay";

    public string OpacityPercentText => $"{Math.Round(Opacity * 100)} %";

    public string ScaleText => $"{Scale:0.00}×";

    public Brush BackgroundPreview => OverlayWindowViewModel.ParseBrush(BackgroundColor, Color.FromArgb(0xCC, 0x10, 0x10, 0x10));

    public Brush TextPreview => OverlayWindowViewModel.ParseBrush(TextColor, Colors.White);

    public Brush AccentPreview => OverlayWindowViewModel.ParseBrush(AccentColor, Color.FromRgb(0x4C, 0xC2, 0xFF));

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        LoadFromSettings();
        if (_pageVisible)
        {
            StartPreview();
        }

        await LoadGamingAsync(cancellationToken);
    }

    /// <summary>Page Loaded: the live preview polls sensors only while this page is on screen.</summary>
    public void OnPageLoaded()
    {
        _pageVisible = true;
        if (IsInitialized)
        {
            StartPreview();
        }

        StartFrameStatus();
    }

    /// <summary>
    /// Page Unloaded: release the preview's hold on the shared overlay timer (the view model is a singleton), so hiding
    /// the overlay actually stops live updates while another page is shown.
    /// </summary>
    public void OnPageUnloaded()
    {
        _pageVisible = false;
        StopPreview();
        StopFrameStatus();
    }

    private void StartPreview()
    {
        if (_previewRunning)
        {
            return;
        }

        _previewRunning = true;
        Preview.Start();
    }

    private void StopPreview()
    {
        if (!_previewRunning)
        {
            return;
        }

        _previewRunning = false;
        Preview.Stop();
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        LoadFromSettings();
        await LoadGamingAsync(cancellationToken);
    }

    // ----- Overlay on/off and edit mode -----

    [RelayCommand]
    private void ToggleOverlay() => _overlay.Toggle();

    [RelayCommand]
    private void ShowOverlay() => _overlay.Show();

    [RelayCommand]
    private void HideOverlay() => _overlay.Hide();

    partial void OnIsEditModeChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (value)
        {
            _overlay.EnterEditMode();
        }
        else
        {
            _overlay.ExitEditMode();
        }
    }

    private void SetEditModeQuietly(bool value)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            IsEditMode = value;
        }
        finally
        {
            _loading = was;
        }
    }

    // ----- Hotkey -----

    [RelayCommand]
    private void ApplyHotkey()
    {
        if (!HotkeyGesture.TryParse(HotkeyText, out var gesture, out var error))
        {
            HotkeyError = error;
            HotkeyStatus = "Not active";
            return;
        }

        HotkeyText = gesture.ToString();
        Settings.Current.Overlay.Hotkey = HotkeyText;
        bool ok = _overlay.RegisterHotkey();
        HotkeyError = ok ? null : _overlay.HotkeyError;
        HotkeyStatus = ok ? $"Active: press {HotkeyText} anywhere to show or hide the overlay." : "Not active";
        ScheduleSave();
    }

    // ----- Metrics -----

    [RelayCommand]
    private void MoveMetricUp(OverlayMetricRow? row) => MoveMetric(row, -1);

    [RelayCommand]
    private void MoveMetricDown(OverlayMetricRow? row) => MoveMetric(row, +1);

    [RelayCommand]
    private void EnableAllMetrics() => SetAllMetrics(true);

    [RelayCommand]
    private void DisableAllMetrics() => SetAllMetrics(false);

    [RelayCommand]
    private void ResetMetricLabels()
    {
        _loading = true;
        try
        {
            foreach (var row in Metrics)
            {
                row.Label = row.DefaultLabel;
            }
        }
        finally
        {
            _loading = false;
        }

        OnMetricsChanged();
    }

    private void MoveMetric(OverlayMetricRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        int index = Metrics.IndexOf(row);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Metrics.Count)
        {
            return;
        }

        Metrics.Move(index, target);
        OnMetricsChanged();
    }

    private void SetAllMetrics(bool enabled)
    {
        _loading = true;
        try
        {
            foreach (var row in Metrics)
            {
                row.Enabled = enabled;
            }
        }
        finally
        {
            _loading = false;
        }

        OnMetricsChanged();
    }

    private void OnMetricsChanged()
    {
        if (_loading)
        {
            return;
        }

        for (int i = 0; i < Metrics.Count; i++)
        {
            Metrics[i].Order = i;
        }

        EnabledMetricCount = Metrics.Count(m => m.Enabled);
        Settings.Current.Overlay.Metrics = Metrics
            .Select(m => new OverlayMetric { Id = m.Id, Enabled = m.Enabled, Label = string.IsNullOrWhiteSpace(m.Label) ? m.DefaultLabel : m.Label.Trim(), Order = m.Order })
            .ToList();
        ApplyAndScheduleSave();
    }

    // ----- Presets -----

    [RelayCommand]
    private void SavePreset()
    {
        var name = NewPresetName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _notifications.Warning("Presets", "Type a name for the preset first.");
            return;
        }

        if (OverlayPresetCatalog.IsBuiltIn(name))
        {
            _notifications.Warning("Presets", $"\"{name}\" is a built-in preset. Choose another name.");
            return;
        }

        var preset = OverlayPresetCatalog.Capture(Settings.Current.Overlay, name);
        OverlayPresetCatalog.Save(Settings.Current.Overlay, preset);
        NewPresetName = string.Empty;
        RefreshPresets();
        ScheduleSave();
        _notifications.Success("Presets", $"Preset \"{name}\" saved.");
    }

    [RelayCommand]
    private void LoadPreset(OverlayPresetRow? row)
    {
        if (row is null)
        {
            return;
        }

        var preset = OverlayPresetCatalog.Find(row.Name, Settings.Current.Overlay.Presets);
        if (preset is null)
        {
            _notifications.Warning("Presets", $"Preset \"{row.Name}\" was not found.");
            RefreshPresets();
            return;
        }

        OverlayPresetCatalog.Apply(preset, Settings.Current.Overlay);
        LoadFromSettings();
        ApplyAndScheduleSave();
        _notifications.Info("Presets", $"Preset \"{row.Name}\" applied.");
    }

    [RelayCommand]
    private async Task DeletePresetAsync(OverlayPresetRow? row)
    {
        if (row is null || row.IsBuiltIn)
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync("Delete preset", $"Delete the preset \"{row.Name}\"? The current overlay layout is not changed.", "Delete", isDanger: true);
        if (!confirmed)
        {
            return;
        }

        OverlayPresetCatalog.Delete(Settings.Current.Overlay, row.Name);
        RefreshPresets();
        ScheduleSave();
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        bool confirmed = await _dialogs.ConfirmAsync("Reset overlay settings",
            "Put every overlay setting back to its default? Your saved presets are kept.", "Reset");
        if (!confirmed)
        {
            return;
        }

        var fresh = new OverlaySettings { Presets = Settings.Current.Overlay.Presets, Enabled = Settings.Current.Overlay.Enabled };
        Settings.Current.Overlay = fresh;
        LoadFromSettings();
        ApplyHotkey();
        ApplyAndScheduleSave();
    }

    // ----- Property change handlers (write-through to settings) -----

    partial void OnCornerChanged(OverlayCorner value) => Persist(o => o.Corner = value);

    partial void OnCustomXChanged(double value) => Persist(o => o.X = Math.Round(value));

    partial void OnCustomYChanged(double value) => Persist(o => o.Y = Math.Round(value));

    partial void OnOpacityChanged(double value) => Persist(o => o.Opacity = Math.Clamp(value, 0.1, 1.0));

    partial void OnScaleChanged(double value) => Persist(o => o.Scale = Math.Clamp(value, 0.5, 3.0));

    partial void OnFontFamilyChanged(string value) => Persist(o => o.FontFamily = string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value);

    partial void OnFontSizeChanged(double value) => Persist(o => o.FontSize = Math.Clamp(value, 8, 48));

    partial void OnOrientationChanged(OverlayOrientation value) => Persist(o => o.Orientation = value);

    partial void OnShowBackgroundChanged(bool value) => Persist(o => o.ShowBackground = value);

    partial void OnShowSparklinesChanged(bool value) => Persist(o => o.ShowSparklines = value);

    partial void OnUpdateIntervalMsChanged(double value) => Persist(o => o.UpdateIntervalMs = (int)Math.Clamp(Math.Round(value), 250, 10000));

    partial void OnBackgroundColorChanged(string value) => PersistColor(value, o => o.BackgroundColor = value);

    partial void OnTextColorChanged(string value) => PersistColor(value, o => o.TextColor = value);

    partial void OnAccentColorChanged(string value) => PersistColor(value, o => o.AccentColor = value);

    private void PersistColor(string value, Action<OverlaySettings> apply)
    {
        if (_loading)
        {
            return;
        }

        if (!IsValidColor(value))
        {
            ColorError = $"\"{value}\" is not a colour. Use #RRGGBB or #AARRGGBB, for example #CC101010.";
            return;
        }

        ColorError = null;
        Persist(apply);
    }

    private void Persist(Action<OverlaySettings> apply)
    {
        if (_loading)
        {
            return;
        }

        apply(Settings.Current.Overlay);
        ApplyAndScheduleSave();
    }

    private void ApplyAndScheduleSave()
    {
        try
        {
            _overlay.ApplySettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Overlay settings could not be applied live.");
        }

        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveNowAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Overlay settings could not be saved.");
            ErrorMessage = "Settings could not be saved: " + ex.Message;
        }
    }

    // ----- Loading -----

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var o = Settings.Current.Overlay;
            HotkeyText = o.Hotkey;
            HotkeyError = _overlay.HotkeyError;
            HotkeyStatus = _overlay.HotkeyError is null
                ? $"Active: press {o.Hotkey} anywhere to show or hide the overlay."
                : "Not active";
            Corner = o.Corner;
            CustomX = o.X;
            CustomY = o.Y;
            Opacity = Math.Clamp(o.Opacity, 0.1, 1.0);
            Scale = Math.Clamp(o.Scale, 0.5, 3.0);
            FontFamily = o.FontFamily;
            FontSize = o.FontSize;
            Orientation = o.Orientation;
            ShowBackground = o.ShowBackground;
            BackgroundColor = o.BackgroundColor;
            TextColor = o.TextColor;
            AccentColor = o.AccentColor;
            ColorError = null;
            ShowSparklines = o.ShowSparklines;
            UpdateIntervalMs = o.UpdateIntervalMs;
            IsOverlayVisible = _overlay.IsVisible;
            IsEditMode = _overlay.IsEditMode;

            foreach (var row in Metrics)
            {
                row.Changed -= OnRowChanged;
            }

            Metrics.Clear();
            foreach (var metric in OverlayPresetCatalog.Normalize(o.Metrics))
            {
                var defaultLabel = OverlayMetricIds.All.First(m => m.Id == metric.Id).Label;
                var row = new OverlayMetricRow(metric.Id, defaultLabel) { Enabled = metric.Enabled, Label = metric.Label, Order = metric.Order };
                row.Changed += OnRowChanged;
                Metrics.Add(row);
            }

            EnabledMetricCount = Metrics.Count(m => m.Enabled);
            RefreshPresets();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnRowChanged(object? sender, EventArgs e) => OnMetricsChanged();

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var preset in OverlayPresetCatalog.BuiltIn())
        {
            Presets.Add(new OverlayPresetRow(preset.Name, true, Describe(preset)));
        }

        foreach (var preset in Settings.Current.Overlay.Presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Presets.Add(new OverlayPresetRow(preset.Name, false, Describe(preset)));
        }
    }

    private static string Describe(OverlayPreset preset)
    {
        int count = OverlayPresetCatalog.EnabledInOrder(preset.Metrics).Count;
        string orientation = preset.Orientation == OverlayOrientation.Horizontal ? "horizontal strip" : "vertical list";
        string corner = Converters.EnumToStringConverter.SplitWords(preset.Corner.ToString()).ToLowerInvariant();
        return $"{count} metric{(count == 1 ? string.Empty : "s")} · {orientation} · {corner}";
    }

    private void OnExternalSettingsChanged(AppSettings settings)
    {
        if (_loading)
        {
            return;
        }

        // Only the position can change behind the page's back (dragging the overlay in edit mode).
        var o = settings.Overlay;
        _loading = true;
        try
        {
            Corner = o.Corner;
            CustomX = o.X;
            CustomY = o.Y;
            IsOverlayVisible = _overlay.IsVisible;
        }
        finally
        {
            _loading = false;
        }
    }

    private static bool IsValidColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            return ColorConverter.ConvertFromString(text.Trim()) is Color;
        }
        catch
        {
            return false;
        }
    }
}
