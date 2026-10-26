using System.Windows;
using System.Windows.Threading;

namespace PcChecker.App.Services;

/// <summary>Small helpers for marshalling work onto the UI thread.</summary>
public static class DispatcherHelper
{
    public static Dispatcher? UiDispatcher => Application.Current?.Dispatcher;

    /// <summary>Runs <paramref name="action"/> on the UI thread (synchronously when already there).</summary>
    public static void RunOnUi(Action action)
    {
        var d = UiDispatcher;
        if (d is null || d.CheckAccess())
        {
            action();
        }
        else
        {
            d.Invoke(action);
        }
    }

    /// <summary>Queues <paramref name="action"/> on the UI thread without waiting.</summary>
    public static void PostToUi(Action action)
    {
        var d = UiDispatcher;
        if (d is null)
        {
            action();
        }
        else
        {
            d.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }

    public static Task RunOnUiAsync(Action action)
    {
        var d = UiDispatcher;
        if (d is null || d.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return d.InvokeAsync(action).Task;
    }
}
