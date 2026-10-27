using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Sensors;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PcChecker.App.Tray;

/// <summary>Tray menu, popup placement, notifications and the actions behind the popup's buttons.</summary>
public sealed partial class TrayIconService
{
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open PC Checker", FontWeight = FontWeights.SemiBold, Icon = new SymbolIcon(SymbolRegular.Open24) };
        open.Click += (_, _) => ShowMainWindow();
        var overlay = new MenuItem { Header = "Show overlay", IsCheckable = true, Icon = new SymbolIcon(SymbolRegular.Layer24) };
        overlay.Click += (_, _) => ToggleOverlay();
        var fans = new MenuItem { Header = "Fan profile", Icon = new SymbolIcon(SymbolRegular.ArrowSyncCircle24) };
        var health = new MenuItem { Header = "Check health now", Icon = new SymbolIcon(SymbolRegular.Heart24) };
        health.Click += (_, _) => _ = RunFromTrayAsync(ScheduledJobKind.Health);
        var exit = new MenuItem { Header = "Exit PC Checker", Icon = new SymbolIcon(SymbolRegular.Power24) };
        exit.Click += (_, _) => ExitApplication();

        menu.Items.Add(open);
        menu.Items.Add(overlay);
        menu.Items.Add(fans);
        menu.Items.Add(health);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.Opened += (_, _) =>
        {
            overlay.IsChecked = _overlay.IsVisible;
            health.IsEnabled = !_runner.Executor.IsRunning(ScheduledJobKind.Health);
            health.Header = health.IsEnabled ? "Check health now" : "Checking health…";
            FillFanMenu(fans);
        };
        return menu;
    }

    private void FillFanMenu(MenuItem parent)
    {
        parent.Items.Clear();
        if (!_fans.IsAvailable)
        {
            parent.Items.Add(new MenuItem { Header = _fans.UnavailableReason ?? "Fan control is not available on this PC.", IsEnabled = false });
            return;
        }

        var active = _fans.ActiveProfile?.Name ?? _settings.Current.Fans.ActiveProfileName;
        var names = _fans.BuiltInProfiles.Select(p => p.Name)
            .Concat(_settings.Current.Fans.Profiles.Select(p => p.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = string.Equals(name, active, StringComparison.OrdinalIgnoreCase) };
            item.Click += async (_, _) => await SwitchFanProfileAsync(name);
            parent.Items.Add(item);
        }
    }

    /// <summary>The popup window, created on first use; forgotten again if it is ever closed for good.</summary>
    private TrayPopupWindow EnsurePopup()
    {
        if (_popup is { } existing)
        {
            return existing;
        }

        var popup = new TrayPopupWindow(_popupViewModel);
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_popup, popup))
            {
                _popup = null;
            }
        };
        _popup = popup;
        return popup;
    }

    private void TogglePopup()
    {
        try
        {
            _popup = EnsurePopup();
            if (_popup.IsVisible)
            {
                _popup.Hide();
                return;
            }

            // The click that deactivated (and hid) the popup also lands here; do not reopen it straight away.
            if (DateTime.UtcNow - _popup.LastHiddenUtc < PopupReopenGuard)
            {
                return;
            }

            _popupViewModel.Refresh(_overlay.IsVisible);
            _popup.Left = -10000;
            _popup.Top = -10000;
            _popup.Show();
            _popup.UpdateLayout();
            PlacePopup(_popup);
            _popup.Activate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the tray popup.");
        }
    }

    private static void PlacePopup(Window popup)
    {
        // Start from the primary work area, then switch to the work area of the monitor under the cursor, so a tray
        // on a second monitor's taskbar opens the popup on that monitor.
        var work = SystemParameters.WorkArea;
        var area = new ScreenRect(work.Left, work.Top, work.Width, work.Height);
        double x = area.Right, y = area.Bottom;
        if (GetCursorPos(out var cursor) && PresentationSource.FromVisual(popup)?.CompositionTarget is { } target)
        {
            var fromDevice = target.TransformFromDevice;
            var dip = fromDevice.Transform(new Point(cursor.X, cursor.Y));
            x = dip.X;
            y = dip.Y;
            if (TryGetMonitorWorkArea(cursor, out var monitorWork))
            {
                var topLeft = fromDevice.Transform(new Point(monitorWork.Left, monitorWork.Top));
                var bottomRight = fromDevice.Transform(new Point(monitorWork.Right, monitorWork.Bottom));
                area = new ScreenRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
            }
        }

        var (left, top) = TrayPopupPlacement.Compute(area, x, y, popup.ActualWidth, popup.ActualHeight, 0);
        popup.Left = left;
        popup.Top = top;
    }

    public async Task QuickCleanAsync()
    {
        int minutes = Math.Max(0, _settings.Current.Cleanup.SkipFilesNewerThanMinutes);
        bool ok = await ConfirmAsync("Quick clean",
            "PC Checker will delete temporary files from the Safe cleanup categories you use on the Cleanup page: temp folders, caches and update leftovers. "
            + $"Files changed in the last {minutes} minutes, files in use and the Recycle Bin are left alone. Deleted files do not go to the Recycle Bin.",
            "Clean now");
        if (!ok)
        {
            return;
        }

        _popupViewModel.Status = "Cleaning temporary files…";
        try
        {
            await RunFromTrayAsync(ScheduledJobKind.Cleanup, dryRunOverride: false);
        }
        finally
        {
            _popupViewModel.Status = null;
        }
    }

    public async Task<bool> SwitchFanProfileAsync(string profileName)
    {
        var profile = _fans.BuiltInProfiles.Concat(_settings.Current.Fans.Profiles)
            .FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null || !_fans.IsAvailable)
        {
            return false;
        }

        if (!_fanConsentGiven)
        {
            _fanConsentGiven = await ConfirmAsync("Let PC Checker control your fans?",
                $"Switching to \"{profile.Name}\" sets fan speeds through the motherboard or graphics driver instead of the firmware. "
                + "Fans never go below your minimum speed, are forced to 100 % at critical temperatures, and go back to automatic when PC Checker closes (if that option is on).",
                "Switch profile");
            if (!_fanConsentGiven)
            {
                return false;
            }
        }

        _popupViewModel.Status = $"Applying \"{profile.Name}\"…";
        try
        {
            var result = await _fans.ApplyProfileAsync(profile, CancellationToken.None);
            if (!result.Success)
            {
                Notify("Fan profile not applied", result.Message, NotificationKind.Warning, null);
                return false;
            }

            _settings.Current.Fans.ActiveProfileName = profile.Name;
            await _settings.SaveAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Switching the fan profile from the tray failed.");
            Notify("Fan profile not applied", ex.Message, NotificationKind.Error, null);
            return false;
        }
        finally
        {
            // The popup shows the outcome itself; a switch from the right-click menu must not leave "Applying" behind.
            _popupViewModel.Status = null;
        }
    }

    private async Task RunFromTrayAsync(ScheduledJobKind job, bool? dryRunOverride = null)
    {
        try
        {
            var result = await _runner.RunAsync(job, "tray", dryRunOverride: dryRunOverride);
            if (result is null)
            {
                Notify(ScheduledJobIds.DisplayName(job), "This is already running; you will be told when it finishes.", NotificationKind.Info, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray job {Job} failed.", job);
        }
    }

    /// <summary>A standalone themed confirmation (the window's dialog host may be hidden while PC Checker is in the tray).</summary>
    /// <remarks>
    /// When the popup is open it stays open behind the dialog (which is made topmost so the popup cannot cover it),
    /// then takes focus back, so the progress line it shows after "Yes" is actually seen.
    /// </remarks>
    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var popup = _popup is { IsVisible: true } open ? open : null;
        if (popup is not null)
        {
            popup.KeepOpen = true;
        }

        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                PrimaryButtonText = confirmText,
                CloseButtonText = "Cancel",
                IsSecondaryButtonEnabled = false,
            };
            if (popup is not null)
            {
                // Above the topmost popup, centred on the screen rather than squeezed into the tray corner.
                box.Topmost = true;
                box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            var result = await box.ShowDialogAsync();
            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Themed message box unavailable; using the standard one.");
            return System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question)
                == System.Windows.MessageBoxResult.OK;
        }
        finally
        {
            if (popup is not null)
            {
                popup.KeepOpen = false;
                if (popup.IsVisible)
                {
                    popup.Activate();
                }
            }
        }
    }

    /// <summary>Shows a tray notification. <paramref name="onClick"/> runs when the user clicks it.</summary>
    public void Notify(string title, string message, NotificationKind kind, Action? onClick)
    {
        DispatcherHelper.PostToUi(() =>
        {
            var icon = kind switch
            {
                NotificationKind.Error => NotificationIcon.Error,
                NotificationKind.Warning => NotificationIcon.Warning,
                _ => NotificationIcon.Info,
            };
            title = JobResultSummarizer.Truncate(title, JobResultSummarizer.MaxTitleLength);
            message = JobResultSummarizer.Truncate(message, JobResultSummarizer.MaxMessageLength);
            try
            {
                if (_icon is null)
                {
                    // The user hid the tray icon: borrow a temporary one for the notification, like a headless run does.
                    ShowWithTemporaryIcon(title, message, icon);
                    return;
                }

                _notificationClick = onClick;
                _icon.ShowNotification(title, message, icon);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tray notification failed.");
            }
        });
    }

    private void ShowWithTemporaryIcon(string title, string message, NotificationIcon icon)
    {
        var temporary = new H.NotifyIcon.TaskbarIcon { ToolTipText = TrayText.AppName, IconSource = LoadLogo() };
        temporary.ForceCreate(false);
        temporary.ShowNotification(title, message, icon);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = ScheduledRunner.HeadlessNotificationTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            temporary.Dispose();
        };
        timer.Start();
    }

    private void OnNotificationClicked()
    {
        var action = _notificationClick ?? ShowMainWindow;
        _notificationClick = null;
        action();
    }

    private void OnAlertRaised(object? sender, TemperatureAlert alert)
    {
        if (!Tray.NotifyTemperatureAlerts || !_alertThrottle.ShouldShow(alert.SensorId, DateTimeOffset.Now))
        {
            return;
        }

        DispatcherHelper.PostToUi(() =>
        {
            // While the window is in front, the Dashboard and Temperatures page show the alert themselves.
            if (_window is { IsVisible: true, IsActive: true } && _window.WindowState != WindowState.Minimized)
            {
                return;
            }

            var (title, message) = TrayText.AlertNotification(alert);
            Notify(title, message, alert.Severity >= Severity.Critical ? NotificationKind.Error : NotificationKind.Warning, OpenPage(typeof(SensorsPage)));
        });
    }

    // Raised on a thread-pool thread: read settings and notify on the UI thread, where settings are also edited and saved.
    private void OnJobCompleted(object? sender, ScheduledJobResult result)
    {
        if (result.Trigger != "manual")
        {
            DispatcherHelper.PostToUi(() => NotifyJobCompleted(result));
        }

        // A manual run is shown inline in Settings → Schedules.
    }

    private void NotifyJobCompleted(ScheduledJobResult result)
    {
        bool onlyAttention = _settings.Current.Schedules?.NotifyOnlyWhenAttention ?? new SchedulesSettings().NotifyOnlyWhenAttention;
        if (result.Trigger == "schedule" && !JobResultSummarizer.ShouldNotify(result, onlyAttention))
        {
            return;
        }

        var (title, message) = JobResultSummarizer.NotificationText(result);
        var kind = !result.Success ? NotificationKind.Error : result.NeedsAttention ? NotificationKind.Warning : NotificationKind.Success;
        Action? click = result.Job switch
        {
            ScheduledJobKind.Health => OpenPage(typeof(HealthPage)),
            ScheduledJobKind.Drivers => OpenPage(typeof(DriversPage)),
            ScheduledJobKind.Cleanup => OpenPage(typeof(CleanupPage)),
            ScheduledJobKind.Report when result.OutputPath is { } path && File.Exists(path) => () => ShellLauncher.Open(path),
            _ => null,
        };
        Notify(title, message, kind, click);
    }

    private Action OpenPage(Type pageType) => () =>
    {
        ShowMainWindow();
        try
        {
            _navigation.Navigate(pageType);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not open {Page} from a notification.", pageType.Name);
        }
    };

    /// <summary>Shows the popup off-screen with fresh values and returns it (used by the screenshot runner).</summary>
    public TrayPopupWindow ShowPopupForCapture()
    {
        _popup = EnsurePopup();
        _popupViewModel.Refresh(_overlay.IsVisible);
        _popup.ShowActivated = false;
        _popup.Left = -10000;
        _popup.Top = -10000;
        _popup.Show();
        _popup.UpdateLayout();
        return _popup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Work area (screen minus taskbar) of the monitor nearest to <paramref name="point"/>, in device pixels.</summary>
    private static bool TryGetMonitorWorkArea(NativePoint point, out NativeRect work)
    {
        work = default;
        try
        {
            var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || info.Work.Right <= info.Work.Left || info.Work.Bottom <= info.Work.Top)
            {
                return false;
            }

            work = info.Work;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
