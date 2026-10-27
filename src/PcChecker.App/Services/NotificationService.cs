using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PcChecker.App.Services;

/// <summary>Shows notifications through the WPF-UI snackbar. Falls back to the log when no presenter is attached yet.</summary>
public sealed class NotificationService : INotificationService
{
    private readonly ISnackbarService _snackbar;

    public NotificationService(ISnackbarService snackbar)
    {
        _snackbar = snackbar;
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

        void Post()
        {
            if (_snackbar.GetSnackbarPresenter() is null)
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
            _snackbar.Show(title, message, appearance, new SymbolIcon(symbol), duration ?? TimeSpan.FromSeconds(4));
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
}
