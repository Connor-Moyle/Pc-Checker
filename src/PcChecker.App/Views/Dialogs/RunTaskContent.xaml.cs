using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PcChecker.App.Views.Dialogs;

/// <summary>Body of the Processes → Run new task dialog; the command box has the keyboard focus when it opens.</summary>
public partial class RunTaskContent : UserControl
{
    public RunTaskContent()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CommandBox.Focus();
        Keyboard.Focus(CommandBox);
    }
}
