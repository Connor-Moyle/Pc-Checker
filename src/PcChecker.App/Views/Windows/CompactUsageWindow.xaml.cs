using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Windows;

/// <summary>
/// The compact Live usage window (Task Manager's summary view): every resource with its sparkline, always on top.
/// Drag anywhere to move it, double-click or the open button to go back to the full page, Esc or the cross to close.
/// </summary>
public partial class CompactUsageWindow : Window
{
    public CompactUsageWindow(LiveUsageViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public LiveUsageViewModel ViewModel { get; }

    /// <summary>Raised when the user asks for the full page.</summary>
    public event EventHandler? OpenFullViewRequested;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag started.
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !IsInsideButton(e.OriginalSource as DependencyObject))
        {
            OpenFullViewRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnOpenFullView(object sender, RoutedEventArgs e) => OpenFullViewRequested?.Invoke(this, EventArgs.Empty);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase)
            {
                return true;
            }

            element = element is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }
}
