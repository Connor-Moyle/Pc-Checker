using System.Windows;
using System.Windows.Threading;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class SensorsPage
{
    public SensorsPage(SensorsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public SensorsViewModel ViewModel { get; }

    // Options sit at the end of the page; when they open from the More menu, scroll them into view.
    private void OptionsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, OptionsExpander))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => OptionsExpander.BringIntoView());
        }
    }
}
