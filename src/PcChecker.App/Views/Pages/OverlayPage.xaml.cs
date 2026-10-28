using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class OverlayPage
{
    public OverlayPage(OverlayViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.OnPageLoaded();
        Unloaded += (_, _) => ViewModel.OnPageUnloaded();
        // A profile sent from the Games page opens the editor further down; scroll to it once it is laid out.
        ViewModel.ProfileEditorRequested += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, () => GameProfileEditor.BringIntoView());
    }

    public OverlayViewModel ViewModel { get; }
}
