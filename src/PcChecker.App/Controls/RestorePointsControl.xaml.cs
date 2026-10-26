using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Controls;

/// <summary>
/// Embeddable restore-point panel (list, create, enable System Protection, open System Restore).
/// Hosts may set a <see cref="RestorePointsViewModel"/> as DataContext; when none is set by the time the control loads,
/// a single shared instance is resolved from the app container, so every embed (Features page, Settings, Dashboard)
/// shows the same list and only one settings subscription exists for the app lifetime.
/// </summary>
public partial class RestorePointsControl : UserControl
{
    private static RestorePointsViewModel? _shared;

    public RestorePointsControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DesignerProperties.GetIsInDesignMode(this))
        {
            return;
        }

        if (DataContext is not RestorePointsViewModel)
        {
            // The inherited DataContext is the host page's view model; replace it with the shared panel view model.
            DataContext = SharedViewModel();
        }

        if (DataContext is RestorePointsViewModel viewModel)
        {
            _ = viewModel.EnsureInitializedAsync();
        }
    }

    private static RestorePointsViewModel? SharedViewModel()
    {
        if (_shared is not null)
        {
            return _shared;
        }

        try
        {
            _shared = ActivatorUtilities.CreateInstance<RestorePointsViewModel>(App.Services);
        }
        catch (InvalidOperationException)
        {
            // Container not built yet (early startup); the host can still assign a DataContext later.
        }

        return _shared;
    }
}
