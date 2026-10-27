using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PcChecker.App.Localization;

/// <summary>
/// Translates the fixed text written into the XAML. Every element is translated as it loads, so pages, expanders,
/// menus, pop-ups and list items generated later are all covered without changing any page. Only plain local string
/// values are touched: bound values (from view models) are left alone, and so is text with no translation.
/// </summary>
public static class UiTranslator
{
    private static bool _registered;

    /// <summary>Hooks the Loaded event of every element. Does nothing in English.</summary>
    public static void Register()
    {
        if (_registered || Loc.Current.IsEnglish)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(OnToolTipOpening), true);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject element)
        {
            TranslateSubtree(element);
        }
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement { ToolTip: ToolTip tip })
        {
            TranslateLocalValues(tip);
        }
    }

    /// <summary>Translates <paramref name="root"/> and everything declared under it (logical tree).</summary>
    public static void TranslateSubtree(DependencyObject root)
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

            TranslateLocalValues(node);
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
                        TranslateLocalValues(column);
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
        }
    }

    /// <summary>Replaces every locally set string property of <paramref name="node"/> that has a translation.</summary>
    public static void TranslateLocalValues(DependencyObject node)
    {
        var updates = new List<(DependencyProperty Property, string Value)>();
        var values = node.GetLocalValueEnumerator();
        while (values.MoveNext())
        {
            var entry = values.Current;
            if (entry.Value is string text && entry.Property.PropertyType.IsAssignableFrom(typeof(string)) && !entry.Property.ReadOnly &&
                !IsIdentity(entry.Property) && Loc.Current.Has(text))
            {
                updates.Add((entry.Property, Loc.T(text)));
            }
        }

        foreach (var (property, value) in updates)
        {
            try
            {
                node.SetValue(property, value);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // A property that rejects the new value keeps its English text.
            }
        }
    }

    // Names, keys, paths and formats are not interface text even when they look like words.
    private static bool IsIdentity(DependencyProperty property) =>
        property == FrameworkElement.NameProperty ||
        property == FrameworkElement.TagProperty ||
        property == FrameworkElement.DataContextProperty ||
        property.Name is "Uid" or "Symbol" or "Icon" or "Glyph" or "FontFamily" or "StringFormat" or "ContentStringFormat" or
            "DisplayMemberPath" or "SelectedValuePath" or "SortMemberPath" or "GroupName" or "TargetPageTag" or "NavigateUri" or "Command";

}
