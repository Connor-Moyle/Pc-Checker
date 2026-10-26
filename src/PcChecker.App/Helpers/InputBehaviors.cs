using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PcChecker.App.Helpers;

/// <summary>
/// Small attached behaviors so pages stay free of code-behind: <see cref="EnterCommandProperty"/> runs a command when
/// Enter is pressed in a multi-line text box (Shift+Enter still inserts a new line), and
/// <see cref="AutoScrollToEndProperty"/> keeps a scroll viewer pinned to the bottom while its content grows.
/// </summary>
public static class InputBehaviors
{
    public static readonly DependencyProperty EnterCommandProperty = DependencyProperty.RegisterAttached(
        "EnterCommand", typeof(ICommand), typeof(InputBehaviors), new PropertyMetadata(null, OnEnterCommandChanged));

    public static readonly DependencyProperty AutoScrollToEndProperty = DependencyProperty.RegisterAttached(
        "AutoScrollToEnd", typeof(bool), typeof(InputBehaviors), new PropertyMetadata(false, OnAutoScrollToEndChanged));

    public static ICommand? GetEnterCommand(DependencyObject obj) => (ICommand?)obj.GetValue(EnterCommandProperty);

    public static void SetEnterCommand(DependencyObject obj, ICommand? value) => obj.SetValue(EnterCommandProperty, value);

    public static bool GetAutoScrollToEnd(DependencyObject obj) => (bool)obj.GetValue(AutoScrollToEndProperty);

    public static void SetAutoScrollToEnd(DependencyObject obj, bool value) => obj.SetValue(AutoScrollToEndProperty, value);

    private static void OnEnterCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox)
        {
            return;
        }

        textBox.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is not null)
        {
            textBox.PreviewKeyDown += OnPreviewKeyDown;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        if (sender is TextBox textBox && GetEnterCommand(textBox) is { } command)
        {
            // Push the current text to the binding first so the command sees what was typed.
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            e.Handled = true;
        }
    }

    private static void OnAutoScrollToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
        {
            return;
        }

        viewer.ScrollChanged -= OnScrollChanged;
        if (e.NewValue is true)
        {
            viewer.ScrollChanged += OnScrollChanged;
        }
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer viewer && e.ExtentHeightChange > 0)
        {
            viewer.ScrollToEnd();
        }
    }
}
