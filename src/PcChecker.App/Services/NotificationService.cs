using System.Windows;
using System.Windows.Controls;
using PcChecker.Core.Restore;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PcChecker.App.Services;

/// <summary>
/// Shows notifications through the WPF-UI snackbar. Falls back to the log when no presenter is attached yet. A success
/// notification that follows a reversible change offers "Undo" for a few seconds (see <see cref="IChangeUndoTracker"/>).
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly ISnackbarService _snackbar;
    private readonly IChangeUndoTracker? _undo;

    public NotificationService(ISnackbarService snackbar)
        : this(snackbar, null)
    {
    }

    public NotificationService(ISnackbarService snackbar, IChangeUndoTracker? undo)
    {
        _snackbar = snackbar;
        _undo = undo;
    }

    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null)
    {
        title = Localization.Loc.T(title);
        message = Localization.Loc.T(message);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        // Decide now, while the change is fresh and from the code that made it (its undo flow): a notification from
        // anywhere else never carries the Undo. The snackbar itself may be shown a moment later.
        var undoable = kind == NotificationKind.Success ? _undo?.TakeOffer(DateTimeOffset.Now, ChangeUndo.CurrentFlow) : null;

        void Post()
        {
            if (_snackbar.GetSnackbarPresenter() is not { } presenter)
            {
                return;
            }

            var (appearance, symbol) = kind switch
            {
                NotificationKind.Success => (ControlAppearance.Success, SymbolRegular.CheckmarkCircle24),
                NotificationKind.Warning => (ControlAppearance.Caution, SymbolRegular.Warning24),
                NotificationKind.Error => (ControlAppearance.Danger, SymbolRegular.ErrorCircle24),
                _ => (ControlAppearance.Secondary, SymbolRegular.Info24),
            };

            if (undoable is null || _undo is null)
            {
                _snackbar.Show(title, message, appearance, new SymbolIcon(symbol), duration ?? TimeSpan.FromSeconds(4));
                return;
            }

            ShowWithUndo(presenter, title, message, appearance, symbol, undoable);
        }

        if (dispatcher.CheckAccess())
        {
            Post();
        }
        else
        {
            dispatcher.BeginInvoke(Post);
        }
    }

    /// <summary>
    /// The success snackbar with an Undo button. The 10-second window starts when the snackbar actually appears (it may
    /// wait behind others; if it waited too long the button is hidden). A click asks first, then runs the change's undo
    /// once.
    /// </summary>
    private void ShowWithUndo(SnackbarPresenter presenter, string title, string message, ControlAppearance appearance, SymbolRegular symbol, UndoableChange change)
    {
        var undoButton = new Wpf.Ui.Controls.Button
        {
            Content = Localization.Loc.T("Undo"),
            Icon = new SymbolIcon(SymbolRegular.ArrowUndo24),
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Localization.Loc.T("Take this change back. The undo is recorded in the Action Journal too."),
        };
        System.Windows.Automation.AutomationProperties.SetName(undoButton, Localization.Loc.F("Undo: {0}", change.Description));

        var text = new Wpf.Ui.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(undoButton, 1);
        content.Children.Add(text);
        content.Children.Add(undoButton);

        var snackbar = new Snackbar(presenter)
        {
            Title = title,
            Content = content,
            Appearance = appearance,
            Icon = new SymbolIcon(symbol),
            Timeout = UndoOfferRules.UndoWindow,
        };

        snackbar.Loaded += (_, _) =>
        {
            if (!_undo!.MarkShown(change.Id, DateTimeOffset.Now))
            {
                undoButton.Visibility = Visibility.Collapsed;
            }
        };

        undoButton.Click += async (_, _) =>
        {
            var clickedAt = DateTimeOffset.Now;
            if (!_undo!.CanUndo(change.Id, clickedAt))
            {
                undoButton.Visibility = Visibility.Collapsed;
                return;
            }

            undoButton.IsEnabled = false;
            _ = presenter.HideCurrent();
            if (!await ConfirmUndoAsync(change))
            {
                return;
            }

            // The click time counts, so reading the question does not use up the window.
            var result = await _undo!.UndoAsync(change.Id, clickedAt, CancellationToken.None);
            if (result.Success)
            {
                Show(Localization.Loc.T("Undone"), Localization.Loc.F("{0} was taken back.", change.Description));
            }
            else
            {
                Show(Localization.Loc.T("Could not undo"), result.Message, NotificationKind.Error, TimeSpan.FromSeconds(8));
            }
        };

        snackbar.Show();
    }

    /// <summary>Every change asks first, an undo too. Without a dialog service (tests, early start-up) it goes ahead.</summary>
    private static async Task<bool> ConfirmUndoAsync(UndoableChange change)
    {
        IDialogService? dialogs;
        try
        {
            dialogs = App.Services.GetService(typeof(IDialogService)) as IDialogService;
        }
        catch (InvalidOperationException)
        {
            dialogs = null;
        }

        return dialogs is null || await dialogs.ConfirmAsync(
            Localization.Loc.F("Undo \"{0}\"?", change.Description),
            Localization.Loc.T("PC Checker puts this back the way it was a moment ago. The undo is recorded in the Action Journal too."),
            Localization.Loc.T("Undo"));
    }
}
