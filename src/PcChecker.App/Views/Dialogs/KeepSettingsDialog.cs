using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace PcChecker.App.Views.Dialogs;

/// <summary>
/// "Keep these settings?" dialog shown after a display change. Counts down and closes on its own (which the caller
/// treats as "revert") so a screen that went blank never leaves the user stuck with an unusable layout.
/// </summary>
public sealed class KeepSettingsDialog : ContentDialog
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Controls.TextBlock _countdown = new() { Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeights.SemiBold };
    private int _remaining;

    public KeepSettingsDialog(string title, string detail, int seconds)
    {
        _remaining = Math.Max(1, seconds);
        Title = title;
        PrimaryButtonText = "Keep";
        CloseButtonText = "Revert";
        DefaultButton = ContentDialogButton.Primary;
        IsSecondaryButtonEnabled = false;

        var panel = new StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "If this looks right choose Keep. If the screen is unreadable, wait: the previous settings come back automatically.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.75,
        });
        panel.Children.Add(_countdown);
        Content = panel;
        UpdateCountdownText();

        _timer.Tick += OnTick;
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            Hide(ContentDialogResult.None);
            return;
        }

        UpdateCountdownText();
    }

    private void UpdateCountdownText() =>
        _countdown.Text = $"Reverting automatically in {_remaining} second{(_remaining == 1 ? string.Empty : "s")}…";

    protected override void OnButtonClick(ContentDialogButton button)
    {
        _timer.Stop();
        base.OnButtonClick(button);
    }
}
