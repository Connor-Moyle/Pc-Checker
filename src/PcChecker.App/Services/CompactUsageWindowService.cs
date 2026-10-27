using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.Tray;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Pages;
using PcChecker.App.Views.Windows;
using PcChecker.Core.LiveUsage;
using Wpf.Ui;

namespace PcChecker.App.Services;

/// <summary>
/// Opens and closes the compact Live usage window from the page and the tray menu, keeps the graphs running while it
/// is open and remembers where it was.
/// </summary>
public sealed class CompactUsageWindowService
{
    private readonly LiveUsageViewModel _viewModel;
    private readonly IServiceProvider _services;
    private readonly ILogger<CompactUsageWindowService> _logger;
    private CompactUsageWindow? _window;

    public CompactUsageWindowService(LiveUsageViewModel viewModel, IServiceProvider services, ILogger<CompactUsageWindowService> logger)
    {
        _viewModel = viewModel;
        _services = services;
        _logger = logger;
        _viewModel.CompactViewRequested += (_, _) => Toggle();
    }

    public bool IsOpen => _window is not null;

    public void Toggle()
    {
        if (_window is null)
        {
            Show();
        }
        else
        {
            _window.Close();
        }
    }

    public void Show()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        try
        {
            _ = _viewModel.EnsureInitializedAsync();
            var window = new CompactUsageWindow(_viewModel);
            var prefs = _viewModel.Preferences;
            var area = SystemParameters.WorkArea;
            var virtualScreen = (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var (left, top, width) = CompactWindowPlacement.Fit(prefs.CompactLeft, prefs.CompactTop, prefs.CompactWidth, 360,
                (area.Left, area.Top, area.Width, area.Height), [virtualScreen]);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Closed += OnClosed;
            window.OpenFullViewRequested += (_, _) => OpenFullView();
            _window = window;
            _viewModel.StartLive(window);
            _viewModel.IsCompactOpen = true;
            window.Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The compact Live usage window could not be opened.");
            _window = null;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not CompactUsageWindow window)
        {
            return;
        }

        var prefs = _viewModel.Preferences;
        prefs.CompactLeft = window.Left;
        prefs.CompactTop = window.Top;
        prefs.CompactWidth = window.Width;
        _viewModel.SavePreferences();
        _viewModel.StopLive(window);
        _viewModel.IsCompactOpen = false;
        _window = null;
    }

    private void OpenFullView()
    {
        try
        {
            if (_services.GetService<TrayIconService>() is { } tray && tray.IsIconVisible)
            {
                tray.ShowMainWindow();
            }
            else if (Application.Current?.MainWindow is { } main)
            {
                if (!main.IsVisible)
                {
                    main.Show();
                }

                if (main.WindowState == WindowState.Minimized)
                {
                    main.WindowState = WindowState.Normal;
                }

                main.Activate();
            }

            _services.GetService<INavigationService>()?.Navigate(typeof(LiveUsagePage));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Opening the Live usage page from the compact window failed.");
        }
    }
}
