using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PcChecker.App.Services;

/// <summary>
/// A page view model whose header has a refresh or scan button implements this, so F5 presses the same button. The
/// command's own CanExecute (busy, nothing to do) decides whether anything happens.
/// </summary>
public interface IRefreshablePage
{
    /// <summary>The command F5 runs (usually the header's Refresh, Scan or Check button).</summary>
    ICommand RefreshKeyCommand { get; }
}

/// <summary>Requests from controls deep in a page to the shell window (the shortcuts overlay lives in MainWindow).</summary>
public static class ShellRequests
{
    /// <summary>Raised when something asks for the keyboard shortcuts overlay.</summary>
    public static event EventHandler? ShortcutsRequested;

    public static void ShowShortcuts() => ShortcutsRequested?.Invoke(null, EventArgs.Empty);
}

/// <summary>Finds the page an element sits on and the page's key in <see cref="NavigationCatalog"/>.</summary>
public static class PageHelpLocator
{
    /// <summary>The navigation key (file name) of <paramref name="pageType"/>, or null for a page that is not in the menu.</summary>
    public static string? KeyOf(Type? pageType) =>
        pageType is null ? null : NavigationCatalog.Pages.FirstOrDefault(p => p.PageType == pageType)?.FileName;

    /// <summary>The page type for <paramref name="key"/>, or null.</summary>
    public static Type? PageTypeOf(string? key) =>
        key is null ? null : NavigationCatalog.Pages.FirstOrDefault(p => string.Equals(p.FileName, key, StringComparison.OrdinalIgnoreCase))?.PageType;

    /// <summary>The <see cref="System.Windows.Controls.Page"/> that contains <paramref name="element"/>, or null.</summary>
    public static System.Windows.Controls.Page? PageOf(DependencyObject? element)
    {
        for (var node = element; node is not null; node = Parent(node))
        {
            if (node is System.Windows.Controls.Page page)
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>The first element of type <typeparamref name="T"/> under <paramref name="root"/> (depth first), or null.</summary>
    public static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        int count = root is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node);
}
