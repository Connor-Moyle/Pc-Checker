using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace PcChecker.App.Localization;

/// <summary>
/// Translates the fixed English text written into the XAML, without changing any page. Each window's rendered tree is
/// walked when it loads and again shortly after its layout changes, so pages, expanders, lists, pop-ups and menus that
/// appear later are covered. Text properties (and the screen-reader name and help text) are translated when their
/// value is plain text from the XAML (set directly, by a style or by a template); bound values from view models are
/// left alone, as is text with no translation.
/// </summary>
public static class UiTranslator
{
    private static readonly string[] TextPropertyNames =
        ["Text", "Content", "Header", "ToolTip", "Title", "Subtitle", "Message", "PlaceholderText", "Description", "Label", "Unit", "Caption", "Reason", "ActionText"];

    private static readonly ConcurrentDictionary<Type, DependencyProperty[]> PropertiesByType = new();
    private static readonly ConditionalWeakTable<DependencyObject, object> Done = new();
    private static readonly object Marker = new();
    private static bool _registered;

    /// <summary>Starts translating every window as it loads. Does nothing in English.</summary>
    public static void Register()
    {
        if (_registered || Loc.Current.IsEnglish)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnPopupOpened), true);
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnPopupOpened), true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(OnToolTipOpening), true);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        TranslateTree(window);

        // Re-walk shortly after layout settles: new pages, expanded sections, generated list items. LayoutUpdated fires
        // for layout in any window, so a hidden window (the main window in the tray) is left alone until it shows again.
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { }, window.Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (window.IsVisible)
            {
                TranslateTree(window);
            }
        };
        window.LayoutUpdated += (_, _) =>
        {
            if (window.IsVisible && !timer.IsEnabled)
            {
                timer.Start();
            }
        };
        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible && !timer.IsEnabled)
            {
                timer.Start();
            }
        };
        window.Closed += (_, _) => timer.Stop();
    }

    private static void OnPopupOpened(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject popup)
        {
            TranslateTree(popup);
            // Menu items are generated as the menu opens; catch them once they exist.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () => TranslateTree(popup));
        }
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is DependencyObject element)
        {
            TranslateProperties(element);
        }
    }

    /// <summary>Translates <paramref name="root"/> and everything under it, rendered (visual) and declared (logical).</summary>
    public static void TranslateTree(DependencyObject root)
    {
        if (Loc.Current.IsEnglish)
        {
            return;
        }

        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            TranslateProperties(node);

            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++)
                {
                    stack.Push(VisualTreeHelper.GetChild(node, i));
                }
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                stack.Push(child);
            }

            switch (node)
            {
                case TextBlock text:
                    foreach (var inline in text.Inlines)
                    {
                        stack.Push(inline);
                    }

                    break;
                case Span span:
                    foreach (var inline in span.Inlines)
                    {
                        stack.Push(inline);
                    }

                    break;
                case DataGrid grid:
                    foreach (var column in grid.Columns)
                    {
                        TranslateProperties(column);
                    }

                    break;
            }

            if (node is FrameworkElement { ContextMenu: { } menu })
            {
                stack.Push(menu);
            }

            if (node is Wpf.Ui.Controls.DropDownButton { Flyout: DependencyObject flyout })
            {
                stack.Push(flyout);
            }

            if (node is Wpf.Ui.Controls.SplitButton { Flyout: DependencyObject splitFlyout })
            {
                stack.Push(splitFlyout);
            }

            if (node is FrameworkElement { ToolTip: DependencyObject tip })
            {
                stack.Push(tip);
            }
        }
    }

    /// <summary>Translates the text properties of one element whose value is plain XAML text.</summary>
    public static void TranslateProperties(DependencyObject node)
    {
        // Elements that had no text when first seen are skipped on later passes.
        if (Done.TryGetValue(node, out _))
        {
            return;
        }

        bool anyText = false;
        foreach (var property in PropertiesFor(node.GetType()))
        {
            // What people type is never interface text.
            if (property.Name == "Text" && node is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or PasswordBox)
            {
                continue;
            }

            object value;
            try
            {
                value = node.ReadLocalValue(property) is string local ? local : node.GetValue(property);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (value is not string text || text.Length == 0)
            {
                continue;
            }

            anyText = true;
            var source = DependencyPropertyHelper.GetValueSource(node, property);
            if (source.IsExpression || source.IsCurrent || source.BaseValueSource is BaseValueSource.Inherited or BaseValueSource.Default)
            {
                continue;
            }

            if (!Loc.Current.Has(text))
            {
                continue;
            }

            try
            {
                node.SetCurrentValue(property, Loc.T(text));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // A property that rejects the new value keeps its English text.
            }
        }

        if (!anyText)
        {
            Done.AddOrUpdate(node, Marker);
        }
    }

    private static DependencyProperty[] PropertiesFor(Type type) => PropertiesByType.GetOrAdd(type, static t =>
    {
        var found = new List<DependencyProperty>();
        for (var current = t; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType != typeof(DependencyProperty) || !field.Name.EndsWith("Property", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = field.Name[..^"Property".Length];
                if (!TextPropertyNames.Contains(name) || field.GetValue(null) is not DependencyProperty dp || dp.ReadOnly)
                {
                    continue;
                }

                if (dp.PropertyType == typeof(string) || dp.PropertyType == typeof(object))
                {
                    found.Add(dp);
                }
            }
        }

        // Attached ToolTip lives on FrameworkElement/FrameworkContentElement under ToolTipService.
        if (typeof(FrameworkElement).IsAssignableFrom(t) && !found.Contains(FrameworkElement.ToolTipProperty))
        {
            found.Add(FrameworkElement.ToolTipProperty);
        }

        // Screen-reader names and help text are interface text too.
        if (typeof(UIElement).IsAssignableFrom(t))
        {
            found.Add(System.Windows.Automation.AutomationProperties.NameProperty);
            found.Add(System.Windows.Automation.AutomationProperties.HelpTextProperty);
        }

        // Screen-reader names and help written in the XAML (AutomationProperties.Name="Search").
        if (typeof(FrameworkElement).IsAssignableFrom(t))
        {
            found.Add(System.Windows.Automation.AutomationProperties.NameProperty);
            found.Add(System.Windows.Automation.AutomationProperties.HelpTextProperty);
        }

        return found.Distinct().ToArray();
    });
}
