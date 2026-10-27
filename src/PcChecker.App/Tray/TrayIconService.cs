using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.App.Views.Windows;
using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Power;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.Tray;

/// <summary>
/// The notification-area icon: live CPU/GPU temperatures in the tooltip (optionally the hottest one drawn as the icon),
/// the compact popup on left-click, a menu on right-click, minimize/close to tray, temperature-alert and scheduled-job
/// notifications, and an Exit that runs the normal shutdown path.
/// </summary>
public sealed partial class TrayIconService : ITrayActions, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PopupReopenGuard = TimeSpan.FromMilliseconds(350);

    private readonly ISettingsService _settings;
    private readonly ISensorService _sensors;
    private readonly IFanControlService _fans;
    private readonly IOverlayService _overlay;
    private readonly ScheduledRunner _runner;
    private readonly INavigationService _navigation;
    private readonly ILogger<TrayIconService> _logger;
    private readonly NotificationThrottle _alertThrottle = new(TimeSpan.FromMinutes(10));
    private readonly DispatcherTimer _timer;
    private readonly TrayPopupViewModel _popupViewModel;

    private TaskbarIcon? _icon;
    private TrayPopupWindow? _popup;
    private MainWindow? _window;
    private WindowState _restoreState = WindowState.Normal;
    private bool _startedHidden;
    private bool _hiddenToTray;
    private bool _hiddenHintShown;
    private bool _fanConsentGiven;
    private string? _iconText;
    private HealthStatus _iconLevel;
    private Action? _notificationClick;

    public TrayIconService(ISettingsService settings, ISensorService sensors, IPowerService power, IFanControlService fans,
        IOverlayService overlay, ScheduledRunner runner, INavigationService navigation, ILogger<TrayIconService> logger)
    {
        _settings = settings;
        _sensors = sensors;
        _fans = fans;
        _overlay = overlay;
        _runner = runner;
        _navigation = navigation;
        _logger = logger;
        _popupViewModel = new TrayPopupViewModel(settings, sensors, power, fans, this);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>True once Exit was chosen (or Windows is signing out), so closing the window really exits.</summary>
    public bool IsExiting { get; private set; }

    public bool IsIconVisible => _icon is not null;

    private TraySettings Tray => _settings.Current.Tray ??= new TraySettings();

    /// <summary>The app logo as an image source for tray icons.</summary>
    public static ImageSource LoadLogo()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/icon.ico", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Hooks the main window and creates the icon when the settings (or <paramref name="startHidden"/>) want it.</summary>
    public void Initialize(MainWindow window, bool startHidden)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _startedHidden = startHidden;
        window.StateChanged += OnWindowStateChanged;
        window.Closing += OnWindowClosing;
        if (Application.Current is { } app)
        {
            app.SessionEnding += (_, _) => IsExiting = true;
        }

        _settings.Changed += (_, _) => DispatcherHelper.PostToUi(ApplySettings);
        _sensors.AlertRaised += OnAlertRaised;
        _runner.Executor.Completed += OnJobCompleted;
        ApplySettings();
        if (startHidden)
        {
            // Without an icon a hidden start would leave no way back to the window; App then shows it normally.
            if (_icon is null)
            {
                throw new InvalidOperationException("The tray icon could not be created.");
            }

            _sensors.Start();
        }
    }

    /// <summary>Creates or removes the icon and switches between logo and temperature icon to match the settings.</summary>
    public void ApplySettings()
    {
        if (_window is null)
        {
            return;
        }

        var general = _settings.Current.General;
        bool wanted = Tray.ShowIcon || general.MinimizeToTray || Tray.CloseToTray || _hiddenToTray || _startedHidden;
        try
        {
            if (wanted && _icon is null)
            {
                CreateIcon();
            }
            else if (!wanted && _icon is not null)
            {
                RemoveIcon();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The tray icon could not be updated.");
        }

        if (_icon is not null && !Tray.ShowTemperatureInIcon && _iconText is not null)
        {
            _iconText = null;
            _icon.IconSource = LoadLogo();
        }

        Refresh();
    }

    private void CreateIcon()
    {
        var icon = new TaskbarIcon
        {
            ToolTipText = TrayText.AppName,
            IconSource = LoadLogo(),
            NoLeftClickDelay = true,
            MenuActivation = PopupActivationMode.RightClick,
            PopupActivation = PopupActivationMode.None,
            ContextMenu = BuildMenu(),
        };
        icon.TrayLeftMouseUp += (_, _) => TogglePopup();
        icon.TrayBalloonTipClicked += (_, _) => OnNotificationClicked();
        icon.ForceCreate(false);
        _icon = icon;
        _iconText = null;
        _sensors.Start();
        _timer.Start();
        _logger.LogInformation("Tray icon shown.");
    }

    private void RemoveIcon()
    {
        _timer.Stop();
        _popup?.Hide();
        _icon?.Dispose();
        _icon = null;
        _logger.LogInformation("Tray icon removed.");
    }

    /// <summary>Updates the tooltip, the temperature icon and (when open) the popup.</summary>
    private void Refresh()
    {
        if (_icon is null)
        {
            return;
        }

        var readings = TrayReadings.From(_sensors.Latest);
        var monitoring = _settings.Current.Monitoring;
        int alerts = TrayText.ActiveAlerts(readings, monitoring);
        _icon.ToolTipText = TrayText.Tooltip(readings.CpuTempC, readings.GpuTempC, alerts > 0 ? TrayText.Alerts(alerts) : null);

        if (Tray.ShowTemperatureInIcon)
        {
            UpdateTemperatureIcon(readings, monitoring);
        }

        if (_popup is { IsVisible: true })
        {
            _popupViewModel.Refresh(_overlay.IsVisible);
        }
    }

    private void UpdateTemperatureIcon(TrayReadings readings, MonitoringSettings monitoring)
    {
        var hottest = TrayText.Hottest(readings.CpuTempC, readings.GpuTempC);
        var text = TrayText.IconText(hottest);
        var level = (HealthStatus)Math.Max(
            (int)TrayText.LevelFor(OverlayMetricIds.CpuTemp, readings.CpuTempC, monitoring, readings.CpuName),
            (int)TrayText.LevelFor(OverlayMetricIds.GpuTemp, readings.GpuTempC, monitoring, readings.CpuName));
        if (text == _iconText && level == _iconLevel)
        {
            return;
        }

        _iconText = text;
        _iconLevel = level;
        try
        {
            _icon!.IconSource = new GeneratedIconSource
            {
                Text = text,
                Foreground = level switch
                {
                    HealthStatus.Critical => new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)),
                    HealthStatus.Warning => new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)),
                    _ => Brushes.White,
                },
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
                BackgroundType = BackgroundType.RoundedRectangle,
                FontWeight = FontWeights.Bold,
                FontSize = text.Length >= 3 ? 56 : 80,
                Size = 128,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not draw the temperature icon; showing the logo instead.");
            _icon!.IconSource = LoadLogo();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            if (_settings.Current.General.MinimizeToTray && !AppOptions.Current.ScreenshotsRequested)
            {
                HideToTray();
            }
        }
        else
        {
            _restoreState = _window.WindowState;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!IsExiting && Tray.CloseToTray && !AppOptions.Current.ScreenshotsRequested)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    /// <summary>Hides the window; PC Checker keeps running in the tray.</summary>
    public void HideToTray()
    {
        if (_window is null)
        {
            return;
        }

        if (_icon is null)
        {
            CreateIcon();
        }

        _hiddenToTray = true;
        _window.Hide();
        if (!_hiddenHintShown)
        {
            _hiddenHintShown = true;
            Notify("PC Checker is still running", "Monitoring, alerts and the overlay keep working. Click the tray icon for temperatures, or right-click it to exit.",
                NotificationKind.Info, null);
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _popup?.Hide();
        _startedHidden = false;
        _hiddenToTray = false;
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = _restoreState;
        }

        _window.Activate();

        // Activate alone does not always win focus from a background process; a brief Topmost flip does.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
        ApplySettings();
    }

    public void ToggleOverlay()
    {
        try
        {
            _overlay.Toggle();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not toggle the overlay from the tray.");
        }

        _popupViewModel.Refresh(_overlay.IsVisible);
    }

    /// <summary>Runs the normal shutdown path (fans revert, overclock marked clean, logs flushed in App.OnExit).</summary>
    public void ExitApplication()
    {
        IsExiting = true;
        ClosePopup();
        Application.Current?.Shutdown();
    }

    private void ClosePopup()
    {
        if (_popup is { } popup)
        {
            _popup = null;
            popup.AllowClose = true;
            popup.Close();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _sensors.AlertRaised -= OnAlertRaised;
        _runner.Executor.Completed -= OnJobCompleted;
        try
        {
            ClosePopup();
            _icon?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tray icon cleanup failed.");
        }

        _icon = null;
    }
}
