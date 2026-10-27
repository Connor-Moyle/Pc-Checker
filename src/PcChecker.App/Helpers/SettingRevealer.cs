using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PcChecker.App.Helpers;

/// <summary>
/// Finds an option on a page by its label, opens any collapsed section around it, scrolls it into view and flashes it
/// briefly, so a quick search result for a setting lands right on that setting. Does nothing when the label is not
/// on the page.
/// </summary>
public static class SettingRevealer
{
    /// <summary>Reveals the element labelled <paramref name="englishLabel"/> (or its translation) once the page is loaded.</summary>
    public static void Reveal(FrameworkElement page, string englishLabel)
    {
        if (string.IsNullOrWhiteSpace(englishLabel))
        {
            return;
        }

        if (page.IsLoaded)
        {
            Schedule(page, englishLabel);
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            page.Loaded -= OnLoaded;
            Schedule(page, englishLabel);
        }

        page.Loaded += OnLoaded;
    }

    private static void Schedule(FrameworkElement page, string englishLabel) =>
        page.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealNow(page, englishLabel));

    private static void RevealNow(FrameworkElement page, string englishLabel)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal) { englishLabel, Localization.Loc.T(englishLabel) };
        var label = FindLabel(page, labels);
        if (label is null)
        {
            return;
        }

        // Open the expanders around the option first; their content is laid out only after that.
        bool expanded = false;
        for (DependencyObject? node = label; node is not null && !ReferenceEquals(node, page); node = Parent(node))
        {
            if (node is Expander { IsExpanded: false } expander)
            {
                expander.SetCurrentValue(Expander.IsExpandedProperty, true);
                expanded = true;
            }
        }

        var target = RowOf(label, page);
        page.Dispatcher.BeginInvoke(expanded ? DispatcherPriority.Background : DispatcherPriority.Loaded, () =>
        {
            target.BringIntoView(new Rect(0, -24, Math.Max(1, target.ActualWidth), target.ActualHeight + 120));
            Flash(target);
        });
    }

    /// <summary>The first element (in page order) whose Text, Content or Header is one of <paramref name="labels"/>.</summary>
    private static FrameworkElement? FindLabel(DependencyObject root, HashSet<string> labels)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        var seen = new HashSet<DependencyObject>();
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!seen.Add(node))
            {
                continue;
            }

            if (node is FrameworkElement element && labels.Contains(LabelOf(element) ?? string.Empty))
            {
                return element;
            }

            // The logical tree reaches content inside collapsed expanders, which the visual tree has not built yet.
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                queue.Enqueue(child);
            }
        }

        return null;
    }

    private static string? LabelOf(FrameworkElement element) => element switch
    {
        TextBlock t => t.Text,
        HeaderedContentControl { Header: string header } => header,
        ContentControl { Content: string content } => content,
        _ => null,
    };

    /// <summary>The settings row that holds the label (a CardControl, or the CardExpander it heads), or the label itself.</summary>
    private static FrameworkElement RowOf(FrameworkElement label, FrameworkElement page)
    {
        for (DependencyObject? node = label; node is not null && !ReferenceEquals(node, page); node = Parent(node))
        {
            if (node is Wpf.Ui.Controls.CardControl or Wpf.Ui.Controls.CardExpander)
            {
                return (FrameworkElement)node;
            }
        }

        return label;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        LogicalTreeHelper.GetParent(node) ?? (node is FrameworkElement fe ? fe.TemplatedParent ?? System.Windows.Media.VisualTreeHelper.GetParent(fe) : null);

    private static void Flash(UIElement element)
    {
        var animation = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(450))
        {
            RepeatBehavior = new RepeatBehavior(2),
            FillBehavior = FillBehavior.Stop,
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }
}
