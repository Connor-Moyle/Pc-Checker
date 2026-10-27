using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using PcChecker.App.Services;

namespace PcChecker.App.Controls;

/// <summary>
/// The one "Restart needed" bar every page uses: a Danger "Restart now" button that goes through
/// <see cref="RestartPromptService"/> (Danger confirmation, fixed countdown, Cancel restart), an optional "Later"
/// button, and the shared countdown bar. Hosts bind <see cref="IsOpen"/>, <see cref="Message"/> and <see cref="Reason"/>;
/// a host that only needs the countdown leaves <see cref="IsOpen"/> false.
/// </summary>
public partial class RestartPromptBar : UserControl
{
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(RestartPromptBar), new PropertyMetadata(false, (d, _) => ((RestartPromptBar)d).UpdateState()));

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(RestartPromptBar),
            new PropertyMetadata("A change you made finishes after Windows restarts. Save your work and restart when convenient."));

    public static readonly DependencyProperty ReasonProperty =
        DependencyProperty.Register(nameof(Reason), typeof(string), typeof(RestartPromptBar), new PropertyMetadata("to finish the changes you made"));

    public static readonly DependencyProperty DismissCommandProperty =
        DependencyProperty.Register(nameof(DismissCommand), typeof(ICommand), typeof(RestartPromptBar), new PropertyMetadata(null, (d, _) => ((RestartPromptBar)d).UpdateState()));

    private RestartPromptService? _service;

    public RestartPromptBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateState();
    }

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    /// <summary>Why the restart is needed, phrased "to …" (used in the confirmation and the Windows shutdown notice).</summary>
    public string? Reason { get => (string?)GetValue(ReasonProperty); set => SetValue(ReasonProperty, value); }

    /// <summary>Command for the "Later" button; the button is hidden when none is set.</summary>
    public ICommand? DismissCommand { get => (ICommand?)GetValue(DismissCommandProperty); set => SetValue(DismissCommandProperty, value); }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DesignerProperties.GetIsInDesignMode(this))
        {
            return;
        }

        if (_service is null)
        {
            try
            {
                _service = App.Services.GetRequiredService<RestartPromptService>();
            }
            catch (InvalidOperationException)
            {
                // Container not built yet (early startup); the bar stays inert.
                return;
            }

            CountdownBar.DataContext = _service;
        }

        _service.PropertyChanged -= OnServicePropertyChanged;
        _service.PropertyChanged += OnServicePropertyChanged;
        UpdateState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_service is not null)
        {
            _service.PropertyChanged -= OnServicePropertyChanged;
        }
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RestartPromptService.IsPending))
        {
            UpdateState();
        }
    }

    private void UpdateState()
    {
        if (NeededBar is null)
        {
            return;
        }

        bool pending = _service?.IsPending == true;
        NeededBar.IsOpen = IsOpen && !pending;
        LaterButton.Visibility = DismissCommand is null ? Visibility.Collapsed : Visibility.Visible;
        RestartButton.ToolTip = $"Asks first, then restarts Windows after a {RestartPromptService.CountdownSecondsText} countdown you can cancel.";
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (_service is null)
        {
            return;
        }

        RestartButton.IsEnabled = false;
        try
        {
            await _service.RequestAsync(Reason ?? string.Empty);
        }
        finally
        {
            RestartButton.IsEnabled = true;
        }
    }
}
