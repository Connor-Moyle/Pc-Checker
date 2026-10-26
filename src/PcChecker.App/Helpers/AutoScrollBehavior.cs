using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace PcChecker.App.Helpers;

/// <summary>
/// Attached property for list controls: when <c>AutoScroll="True"</c> the list scrolls to its newest item whenever items are
/// added, so a live log viewer follows the stream without code-behind.
/// </summary>
public static class AutoScrollBehavior
{
    public static readonly DependencyProperty AutoScrollProperty = DependencyProperty.RegisterAttached(
        "AutoScroll", typeof(bool), typeof(AutoScrollBehavior), new PropertyMetadata(false, OnAutoScrollChanged));

    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(NotifyCollectionChangedEventHandler), typeof(AutoScrollBehavior), new PropertyMetadata(null));

    public static bool GetAutoScroll(DependencyObject obj) => (bool)obj.GetValue(AutoScrollProperty);

    public static void SetAutoScroll(DependencyObject obj, bool value) => obj.SetValue(AutoScrollProperty, value);

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list)
        {
            return;
        }

        Detach(list);
        if (e.NewValue is true)
        {
            Attach(list);
            ScrollToEnd(list);
        }
    }

    private static void Attach(ListBox list)
    {
        if (list.ItemsSource is not INotifyCollectionChanged source)
        {
            // The ItemsSource may be bound after the property is set; try again once it is.
            list.Loaded += OnLoadedOnce;
            return;
        }

        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            if (args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            {
                ScrollToEnd(list);
            }
        };
        source.CollectionChanged += handler;
        list.SetValue(HandlerProperty, handler);
    }

    private static void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox list)
        {
            list.Loaded -= OnLoadedOnce;
            if (GetAutoScroll(list))
            {
                Attach(list);
            }
        }
    }

    private static void Detach(ListBox list)
    {
        if (list.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler handler && list.ItemsSource is INotifyCollectionChanged source)
        {
            source.CollectionChanged -= handler;
        }

        list.ClearValue(HandlerProperty);
    }

    private static void ScrollToEnd(ListBox list)
    {
        if (list.Items.Count == 0)
        {
            return;
        }

        try
        {
            list.Dispatcher.BeginInvoke(() =>
            {
                if (list.Items.Count > 0)
                {
                    list.ScrollIntoView(list.Items[list.Items.Count - 1]);
                }
            });
        }
        catch
        {
            // Scrolling is a convenience; never fail the UI over it.
        }
    }
}
