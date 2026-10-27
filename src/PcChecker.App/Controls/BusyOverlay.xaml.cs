using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PcChecker.App.Controls;

/// <summary>Translucent overlay with a spinner, a status line and an optional cancel button.</summary>
public partial class BusyOverlay : UserControl
{
    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(BusyOverlay), new PropertyMetadata(false));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(BusyOverlay), new PropertyMetadata("Working…"));

    public static readonly DependencyProperty CancelCommandProperty =
        DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(BusyOverlay), new PropertyMetadata(null));

    /// <summary>False hides the cancel button for an operation that cannot be stopped part-way.</summary>
    public static readonly DependencyProperty CanCancelProperty =
        DependencyProperty.Register(nameof(CanCancel), typeof(bool), typeof(BusyOverlay), new PropertyMetadata(true));

    /// <summary>Overall progress 0–100 for runs made of several parts; null (the default) shows no progress bar.</summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double?), typeof(BusyOverlay), new PropertyMetadata(null, OnProgressChanged));

    private static readonly DependencyPropertyKey ProgressVisibilityKey =
        DependencyProperty.RegisterReadOnly(nameof(ProgressVisibility), typeof(Visibility), typeof(BusyOverlay), new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty ProgressVisibilityProperty = ProgressVisibilityKey.DependencyProperty;

    public BusyOverlay()
    {
        InitializeComponent();
    }

    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public ICommand? CancelCommand { get => (ICommand?)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }
    public bool CanCancel { get => (bool)GetValue(CanCancelProperty); set => SetValue(CanCancelProperty, value); }
    public double? Progress { get => (double?)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Visibility ProgressVisibility => (Visibility)GetValue(ProgressVisibilityProperty);

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(ProgressVisibilityKey, e.NewValue is double ? Visibility.Visible : Visibility.Collapsed);
}
