namespace PcChecker.App.Services;

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Non-blocking notifications (snackbar in the window; tray/toast fallback when minimized).</summary>
public interface INotificationService
{
    void Show(string title, string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null);
    void Info(string title, string message) => Show(title, message);
    void Success(string title, string message) => Show(title, message, NotificationKind.Success);
    void Warning(string title, string message) => Show(title, message, NotificationKind.Warning);
    void Error(string title, string message) => Show(title, message, NotificationKind.Error, TimeSpan.FromSeconds(8));
}
