using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace PcChecker.App.Controls;

/// <summary>Two-column list of <see cref="KeyValuePair{TKey,TValue}"/> items (property bags).</summary>
public partial class KeyValueList : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(KeyValueList), new PropertyMetadata(null));

    public static readonly DependencyProperty KeyColumnWidthProperty =
        DependencyProperty.Register(nameof(KeyColumnWidth), typeof(GridLength), typeof(KeyValueList), new PropertyMetadata(new GridLength(200)));

    public KeyValueList()
    {
        InitializeComponent();
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public GridLength KeyColumnWidth
    {
        get => (GridLength)GetValue(KeyColumnWidthProperty);
        set => SetValue(KeyColumnWidthProperty, value);
    }
}
