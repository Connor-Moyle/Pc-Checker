using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using PcChecker.Core.Accessibility;

namespace PcChecker.App.Helpers;

/// <summary>
/// Gives controls a screen-reader name when the XAML does not set one, from the text a sighted user reads as their
/// label: the setting card a switch sits in, the heading above a list, the words next to a box, or the text inside a
/// button. The name is bound to that text, so it follows translation and changing values. A text tooltip becomes
/// the help text (or the name, for a control with nothing else to go on). Names written in the XAML always win; this
/// only fills gaps, the same way <see cref="Localization.UiTranslator"/> translates without touching the pages.
/// </summary>
public static class AccessibleNames
{
    private const int MaxClimb = 3;
    private static bool _registered;

    /// <summary>Starts naming controls as they load, in every window.</summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(typeof(Control), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Control control || !ReferenceEquals(e.OriginalSource, sender) || IsTemplatePart(control))
        {
            return;
        }

        try
        {
            Describe(control);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A name is a courtesy; never let it break a page.
        }
    }

    /// <summary>Fills in the name and help text of <paramref name="control"/> where they are missing.</summary>
    public static void Describe(Control control)
    {
        bool nameFromToolTip = false;
        if (NeedsName(control))
        {
            if (FindNameText(control) is { } label)
            {
                BindingOperations.SetBinding(control, AutomationProperties.NameProperty,
                    new Binding(label.Property.Name) { Source = label.Source, Mode = BindingMode.OneWay, Converter = NameConverter.Instance });
            }
            else if (control.ToolTip is string)
            {
                BindingOperations.SetBinding(control, AutomationProperties.NameProperty,
                    new Binding(nameof(FrameworkElement.ToolTip)) { Source = control, Mode = BindingMode.OneWay, Converter = NameConverter.Instance });
                nameFromToolTip = true;
            }
        }

        if (!nameFromToolTip && control.ToolTip is string &&
            string.IsNullOrEmpty(AutomationProperties.GetHelpText(control)) &&
            !BindingOperations.IsDataBound(control, AutomationProperties.HelpTextProperty))
        {
            BindingOperations.SetBinding(control, AutomationProperties.HelpTextProperty,
                new Binding(nameof(FrameworkElement.ToolTip)) { Source = control, Mode = BindingMode.OneWay, Converter = HelpTextConverter.Instance });
        }
    }

    /// <summary>True for the controls people operate whose name WPF cannot work out on its own.</summary>
    private static bool NeedsName(Control control)
    {
        if (!string.IsNullOrEmpty(AutomationProperties.GetName(control)) ||
            BindingOperations.IsDataBound(control, AutomationProperties.NameProperty) ||
            AutomationProperties.GetLabeledBy(control) is not null)
        {
            return false;
        }

        return control switch
        {
            // Items are named by their content; menus by their header.
            ListBoxItem or ComboBoxItem or TreeViewItem or DataGridCell or DataGridRow or DataGridColumnHeader or MenuItem or TabItem => false,
            // A setting card's peer already reads a text header.
            Wpf.Ui.Controls.CardControl card => card.Header is not string,
            HeaderedContentControl headered => headered.Header is not string,
            ButtonBase button => button.Content is not string,
            TextBoxBase or PasswordBox or ComboBox or RangeBase or Selector or TreeView or DatePicker => true,
            _ => false,
        };
    }

    /// <summary>The text element and property to take the name from, or null.</summary>
    private static (DependencyObject Source, DependencyProperty Property)? FindNameText(Control control)
    {
        switch (control)
        {
            case Wpf.Ui.Controls.CardControl card:
                return TextIn(card.Header as DependencyObject);
            case HeaderedContentControl headered:
                return TextIn(headered.Header as DependencyObject);
            case ButtonBase { Content: AccessText access }:
                return (access, AccessText.TextProperty);
            case ButtonBase { Content: DependencyObject content } when TextIn(content) is { } inner:
                return inner;
            case ButtonBase and not ToggleButton:
                // A button with only an icon: its tooltip says what it does (Describe falls back to it).
                return null;
        }

        if (OwningCard(control) is { } owner && TextIn(owner.Header as DependencyObject) is { } cardLabel)
        {
            return cardLabel;
        }

        bool labelFollows = control is ToggleButton;
        return NearbyLabel(control, labelFollows) ?? Placeholder(control);
    }

    /// <summary>A setting card (header on the left, the control on the right) that holds <paramref name="control"/>.</summary>
    private static Wpf.Ui.Controls.CardControl? OwningCard(Control control)
    {
        DependencyObject? node = control;
        for (int i = 0; i < 4 && node is not null; i++)
        {
            node = Parent(node);
            switch (node)
            {
                case Wpf.Ui.Controls.CardControl card when card.Content is DependencyObject content && IsWithin(control, content):
                    return card;
                case ItemsControl or ListBoxItem or Wpf.Ui.Controls.Card:
                    return null;
            }
        }

        return null;
    }

    /// <summary>The label next to <paramref name="control"/>: before it in a row or stack, or above it in a grid.</summary>
    private static (DependencyObject, DependencyProperty)? NearbyLabel(FrameworkElement control, bool labelFollows)
    {
        FrameworkElement child = control;
        for (int level = 0; level < MaxClimb; level++)
        {
            if (Parent(child) is not Panel panel || panel is VirtualizingPanel)
            {
                return null;
            }

            var siblings = panel.Children.OfType<FrameworkElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
            int index = siblings.IndexOf(child);
            IEnumerable<FrameworkElement> candidates;
            if (panel is Grid)
            {
                int row = Grid.GetRow(child), column = Grid.GetColumn(child);
                var sameRowBefore = siblings.Where(s => Grid.GetRow(s) == row && Grid.GetColumn(s) < column).OrderByDescending(Grid.GetColumn);
                var sameRowAfter = siblings.Where(s => Grid.GetRow(s) == row && Grid.GetColumn(s) > column).OrderBy(Grid.GetColumn);
                var above = siblings.Where(s => Grid.GetRow(s) == row - 1 && Grid.GetColumn(s) == column);
                candidates = labelFollows ? sameRowAfter.Concat(sameRowBefore).Concat(above) : sameRowBefore.Concat(above);
            }
            else
            {
                var before = siblings.Take(Math.Max(0, index)).Reverse();
                var after = siblings.Skip(index + 1);
                candidates = labelFollows ? after.Take(1).Concat(before) : before;
            }

            foreach (var candidate in candidates)
            {
                if (IsInteractive(candidate))
                {
                    // Another control sits in between: its label is not ours.
                    return null;
                }

                if (TextIn(candidate) is { } found)
                {
                    return found;
                }
            }

            // Climb only out of a stack the control opens (a group whose label sits just before the group).
            if (panel is Grid || index > 0)
            {
                return null;
            }

            child = panel;
        }

        return null;
    }

    /// <summary>The placeholder of a WPF UI text box ("Search…"), when nothing else names it.</summary>
    private static (DependencyObject, DependencyProperty)? Placeholder(Control control) =>
        control is Wpf.Ui.Controls.TextBox box && !string.IsNullOrWhiteSpace(box.PlaceholderText)
            ? (box, Wpf.Ui.Controls.TextBox.PlaceholderTextProperty)
            : null;

    /// <summary>The first readable text block in <paramref name="root"/> (itself, or a few levels inside it).</summary>
    private static (DependencyObject, DependencyProperty)? TextIn(DependencyObject? root, int depth = 0)
    {
        switch (root)
        {
            case null or Controls.IconGlyph or Wpf.Ui.Controls.IconElement:
                return null;
            case TextBlock text when HasText(text):
                return (text, TextBlock.TextProperty);
            case AccessText access when !string.IsNullOrWhiteSpace(access.Text):
                return (access, AccessText.TextProperty);
            case Label { Content: string } label:
                return (label, ContentControl.ContentProperty);
        }

        if (depth >= 3 || root is Control and not (Label or UserControl))
        {
            return null;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is FrameworkElement { Visibility: Visibility.Collapsed })
            {
                continue;
            }

            if (TextIn(child, depth + 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool HasText(TextBlock text) =>
        AccessibleText.ToName(text.Text) is not null || BindingOperations.IsDataBound(text, TextBlock.TextProperty);

    private static bool IsInteractive(FrameworkElement element) => element is Control and not (Label or UserControl);

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? node = element; node is not null; node = Parent(node))
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Controls inside another control's template (the clear button of a text box, scroll bar arrows).</summary>
    private static bool IsTemplatePart(FrameworkElement element) =>
        element.TemplatedParent is not null and not ContentPresenter;

    private static DependencyObject? Parent(DependencyObject node) =>
        LogicalTreeHelper.GetParent(node) ?? (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null);

    /// <summary>Text → a short screen-reader name (no access key marker, no trailing colon).</summary>
    private sealed class NameConverter : IValueConverter
    {
        public static readonly NameConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            AccessibleText.ToName(value as string) ?? string.Empty;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>A text tooltip → help text; anything else (a tooltip with its own layout) → none.</summary>
    private sealed class HelpTextConverter : IValueConverter
    {
        public static readonly HelpTextConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string text ? text.Trim() : string.Empty;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
