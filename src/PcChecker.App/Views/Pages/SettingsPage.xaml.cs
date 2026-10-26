using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class SettingsPage
{
    private const string RestorePointsControlTypeName = "PcChecker.App.Controls.RestorePointsControl";

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        RestorePointsHost.Content = CreateRestorePointsContent();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// The restore point tools control belongs to another module. It is embedded when the type exists in this build and
    /// replaced by a short note otherwise, so the settings page never depends on it at compile time.
    /// </summary>
    private static object CreateRestorePointsContent()
    {
        try
        {
            var type = Type.GetType(RestorePointsControlTypeName) ?? typeof(SettingsPage).Assembly.GetType(RestorePointsControlTypeName);
            if (type is not null && typeof(FrameworkElement).IsAssignableFrom(type))
            {
                var provider = App.Services;
                return provider.GetService(type) ?? ActivatorUtilities.CreateInstance(provider, type);
            }
        }
        catch (Exception)
        {
            // Fall through to the placeholder; the settings page must always open.
        }

        return new Wpf.Ui.Controls.TextBlock
        {
            Appearance = Wpf.Ui.Controls.TextColor.Secondary,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = "Restore point tools (list, create and enable System Restore) are on the Windows Features page.",
        };
    }
}
