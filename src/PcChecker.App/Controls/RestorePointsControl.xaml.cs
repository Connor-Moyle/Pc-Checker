using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Controls;

/// <summary>
/// Embeddable restore-point panel (list, create, enable System Protection, open System Restore). Resolves its own
/// <see cref="RestorePointsViewModel"/> from the app container unless a host sets one as DataContext.
/// </summary>
public partial class RestorePointsControl : UserControl
{
    public RestorePointsControl()
    {
        InitializeComponent();
        if (!DesignerProperties.GetIsInDesignMode(this))
        {
            DataContext = CreateViewModel();
        }

        Loaded += OnLoaded;
    }

    private static RestorePointsViewModel? CreateViewModel()
    {
        try
        {
            return ActivatorUtilities.CreateInstance<RestorePointsViewModel>(App.Services);
        }
        catch (Exception)
        {
            // No container yet (designer or early startup); the host can still assign a DataContext.
            return null;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is RestorePointsViewModel viewModel)
        {
            _ = viewModel.EnsureInitializedAsync();
        }
    }
}
