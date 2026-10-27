using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PcChecker.App.Views.Dialogs;

/// <summary>Body of the Hosts file add and edit box; the host names box has the keyboard focus when it opens.</summary>
public partial class HostsEntryContent : UserControl
{
    public HostsEntryContent()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NamesBox.Focus();
        Keyboard.Focus(NamesBox);
    }
}
