using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Windows;
using PcChecker.Core.Common;
using PcChecker.Core.Power;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.App.Services;

/// <summary>
/// Owns the overlay window's lifetime: creates it on first use, shows/hides it, keeps it topmost, positions it by
/// corner or custom coordinates, handles the "edit position" drag mode, registers the global toggle hotkey and
/// pushes settings changes into the live overlay.
/// </summary>
public sealed class OverlayService : IOverlayService, IDisposable
{
    public const string ToggleHotkeyId = "overlay.toggle";
    private static readonly TimeSpan TopmostInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private readonly IHotkeyService _hotkeys;
    private readonly INotificationService _notifications;
    private readonly ILogger<OverlayService> _logger;
    private readonly DispatcherTimer _topmostTimer;
    private OverlayWindow? _window;
    private string? _registeredHotkey;
    private string? _lastReportedHotkeyError;
    private bool _syncingEnabled;

    public OverlayService(IServiceProvider services, ISettingsService settings, IHotkeyService hotkeys, INotificationService notifications,
        ISensorService sensors, IPowerService power, ILogger<OverlayService> logger, ILogger<OverlayWindowViewModel> windowLogger)
    {
        _services = services;
        _settings = settings;
        _hotkeys = hotkeys;
        _notifications = notifications;
        _logger = logger;
        WindowViewModel = new OverlayWindowViewModel(sensors, power, windowLogger);
        WindowViewModel.ApplySettings(settings.Current.Overlay, settings.Current.Monitoring);

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TopmostInterval };
        _topmostTimer.Tick += (_, _) => _window?.ReassertTopmost();
        settings.Changed += OnSettingsChanged;
    }

    public bool IsVisible { get; private set; }

    public bool IsEditMode { get; private set; }

    public OverlayWindowViewModel WindowViewModel { get; }

    public string? HotkeyError { get; private set; }

    public event EventHandler? VisibilityChanged;

    public event EventHandler? EditModeChanged;

    public void Initialize()
    {
        DispatcherHelper.RunOnUi(() =>
        {
            RegisterHotkey();
            if (_settings.Current.Overlay.Enabled && !AppOptions.Current.ScreenshotsRequested)
            {
                Show();
            }
        });
    }

    public void Show()
    {
        DispatcherHelper.RunOnUi(() =>
        {
            if (IsVisible)
            {
                return;
            }

            try
            {
                var window = GetWindow();
                WindowViewModel.ApplySettings(_settings.Current.Overlay, _settings.Current.Monitoring);
                WindowViewModel.Start();
                window.SetClickThrough(!IsEditMode);
                Reposition(window);
                window.Show();
                Reposition(window);
                window.ReassertTopmost();
                _topmostTimer.Start();
                IsVisible = true;
                _logger.LogInformation("Overlay shown.");
                VisibilityChanged?.Invoke(this, EventArgs.Empty);
                PersistEnabled(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The overlay could not be shown.");
                _notifications.Error("Overlay", "The overlay could not be shown: " + ex.Message);
            }
        });
    }

    public void Hide()
    {
        DispatcherHelper.RunOnUi(() =>
        {
            if (!IsVisible)
            {
                return;
            }

            ExitEditMode();
            try
            {
                _topmostTimer.Stop();
                _window?.Hide();
                WindowViewModel.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hiding the overlay failed.");
            }

            IsVisible = false;
            _logger.LogInformation("Overlay hidden.");
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
            PersistEnabled(false);
        });
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public void EnterEditMode()
    {
        DispatcherHelper.RunOnUi(() =>
        {
            if (IsEditMode)
            {
                return;
            }

            if (!IsVisible)
            {
                Show();
            }

            IsEditMode = true;
            WindowViewModel.IsEditMode = true;
            _window?.SetClickThrough(false);
            _window?.ReassertTopmost();
            EditModeChanged?.Invoke(this, EventArgs.Empty);
            _notifications.Info("Overlay", "Drag the overlay to where you want it, then turn off \"Edit position\".");
        });
    }

    public void ExitEditMode()
    {
        DispatcherHelper.RunOnUi(() =>
        {
            if (!IsEditMode)
            {
                return;
            }

            IsEditMode = false;
            WindowViewModel.IsEditMode = false;
            _window?.SetClickThrough(true);
            EditModeChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void ApplySettings()
    {
        DispatcherHelper.RunOnUi(() =>
        {
            var overlay = _settings.Current.Overlay;
            try
            {
                WindowViewModel.ApplySettings(overlay, _settings.Current.Monitoring);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Overlay settings could not be applied.");
            }

            if (!string.Equals(_registeredHotkey, overlay.Hotkey, StringComparison.OrdinalIgnoreCase))
            {
                RegisterHotkey();
            }

            if (IsVisible && _window is not null)
            {
                Reposition(_window);
                _window.ReassertTopmost();
            }

            // Settings are the source of truth for "overlay on/off" (the page, the hotkey and startup all go through them).
            if (!_syncingEnabled && !AppOptions.Current.ScreenshotsRequested)
            {
                if (overlay.Enabled && !IsVisible)
                {
                    Show();
                }
                else if (!overlay.Enabled && IsVisible)
                {
                    Hide();
                }
            }
        });
    }

    public bool RegisterHotkey()
    {
        var gesture = _settings.Current.Overlay.Hotkey;
        bool ok;
        try
        {
            ok = _hotkeys.Register(ToggleHotkeyId, gesture, () => DispatcherHelper.PostToUi(Toggle));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hotkey registration threw.");
            ok = false;
        }

        if (ok)
        {
            _registeredHotkey = gesture;
            HotkeyError = null;
            _lastReportedHotkeyError = null;
            return true;
        }

        _registeredHotkey = null;
        HotkeyError = _hotkeys.LastError ?? $"The hotkey \"{gesture}\" could not be registered.";
        if (!string.Equals(_lastReportedHotkeyError, HotkeyError, StringComparison.Ordinal))
        {
            _lastReportedHotkeyError = HotkeyError;
            _notifications.Warning("Overlay hotkey", HotkeyError);
        }

        return false;
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _topmostTimer.Stop();
        try
        {
            _hotkeys.Unregister(ToggleHotkeyId);
        }
        catch
        {
            // Shutdown must not fail because of the hotkey.
        }

        WindowViewModel.Dispose();
    }

    private OverlayWindow GetWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        _window = _services.GetRequiredService<OverlayWindow>();
        _window.SizeChanged += (_, _) =>
        {
            if (IsVisible && !IsEditMode)
            {
                Reposition(_window);
            }
        };
        _window.DragCompleted += OnDragCompleted;
        return _window;
    }

    private void Reposition(OverlayWindow window)
    {
        if (IsEditMode)
        {
            return;
        }

        try
        {
            var overlay = _settings.Current.Overlay;
            var area = window.GetWorkAreaDips();
            double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            var (x, y) = OverlayLayout.Place(overlay.Corner, overlay.X, overlay.Y, area, double.IsNaN(width) ? 0 : width, double.IsNaN(height) ? 0 : height);
            if (Math.Abs(window.Left - x) > 0.5 || Math.Abs(window.Top - y) > 0.5)
            {
                window.Left = x;
                window.Top = y;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Overlay could not be positioned.");
        }
    }

    private void OnDragCompleted(object? sender, EventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        var overlay = _settings.Current.Overlay;
        var area = _window.GetWorkAreaDips();
        var (x, y) = OverlayLayout.Clamp(_window.Left, _window.Top, area, _window.ActualWidth, _window.ActualHeight);
        overlay.Corner = OverlayCorner.Custom;
        overlay.X = Math.Round(x);
        overlay.Y = Math.Round(y);
        _window.Left = x;
        _window.Top = y;
        _logger.LogInformation("Overlay moved to custom position {X},{Y}.", overlay.X, overlay.Y);
        SaveQuietly();
    }

    private void PersistEnabled(bool enabled)
    {
        if (_settings.Current.Overlay.Enabled == enabled || AppOptions.Current.ScreenshotsRequested)
        {
            return;
        }

        _settings.Current.Overlay.Enabled = enabled;
        SaveQuietly();
    }

    private void SaveQuietly()
    {
        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                _syncingEnabled = true;
                await _settings.SaveAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Overlay settings could not be saved.");
            }
            finally
            {
                _syncingEnabled = false;
            }
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (Application.Current?.Dispatcher is null)
        {
            return;
        }

        DispatcherHelper.PostToUi(ApplySettings);
    }
}
