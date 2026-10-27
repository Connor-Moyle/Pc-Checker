using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Startup;

namespace PcChecker.App.Services;

/// <summary>
/// The single "Restart now" flow for every page: a Danger confirmation, the fixed <see cref="RestartPrompt.CountdownSeconds"/>
/// countdown and one Cancel restart button (shutdown /a). Pages show it through <see cref="Controls.RestartPromptBar"/>,
/// so the countdown stays visible on any page that hosts the bar while it runs.
/// </summary>
public sealed partial class RestartPromptService : ObservableObject
{
    private readonly IRestartScheduler _scheduler;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<RestartPromptService> _logger;
    private CancellationTokenSource? _countdownCts;
    private Func<CancellationToken, Task<OperationResult>>? _cancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountdownTitle))]
    private int _secondsLeft;

    [ObservableProperty]
    private bool _isPending;

    [ObservableProperty]
    private string? _message;

    public RestartPromptService(IRestartScheduler scheduler, IDialogService dialogs, INotificationService notifications, ILogger<RestartPromptService> logger)
    {
        _scheduler = scheduler;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
    }

    public int CountdownSeconds => RestartPrompt.CountdownSeconds;

    /// <summary>"10-second", for tooltips.</summary>
    public static string CountdownSecondsText => $"{RestartPrompt.CountdownSeconds}-second";

    public string CountdownTitle => SecondsLeft > 0 ? $"Restarting in {SecondsLeft} seconds" : "Restarting now";

    /// <summary>Asks to restart <paramref name="reason"/> ("to finish …") and schedules a plain restart when confirmed.</summary>
    public Task<bool> RequestAsync(string reason) => RequestAsync(
        "Restart Windows now?",
        RestartPrompt.ConfirmMessage(reason),
        RestartPrompt.ConfirmButtonText(),
        ct => _scheduler.ScheduleAsync(reason, ct));

    /// <summary>
    /// Confirms with a Danger dialog, runs <paramref name="schedule"/> (which must restart after
    /// <see cref="RestartPrompt.CountdownSeconds"/>) and shows the countdown. <paramref name="cancel"/> aborts it; by
    /// default shutdown /a is used.
    /// </summary>
    public async Task<bool> RequestAsync(string title, string message, string confirmText,
        Func<CancellationToken, Task<OperationResult>> schedule, Func<CancellationToken, Task<OperationResult>>? cancel = null)
    {
        if (IsPending)
        {
            _notifications.Warning("A restart is already counting down", "Cancel it first if you changed your mind.");
            return false;
        }

        if (!await _dialogs.ConfirmAsync(title, message, confirmText, isDanger: true))
        {
            return false;
        }

        OperationResult result;
        try
        {
            result = await schedule(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduling the restart failed.");
            result = OperationResult.FromException(ex, "The restart could not be scheduled: " + ex.Message);
        }

        if (!result.Success)
        {
            _notifications.Error("Restart not scheduled", result.Message);
            return false;
        }

        _logger.LogInformation("Restart scheduled: {Message}", result.Message);
        _cancel = cancel ?? _scheduler.CancelAsync;
        Message = result.Message;
        StartCountdown(RestartPrompt.CountdownSeconds);
        return true;
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        _countdownCts?.Cancel();
        try
        {
            var result = await (_cancel ?? _scheduler.CancelAsync)(CancellationToken.None);
            _notifications.Show("Restart", result.Message, result.Success ? NotificationKind.Success : NotificationKind.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancelling the restart failed.");
            _notifications.Error("Could not cancel the restart", ex.Message + " Run 'shutdown /a' in a command prompt right away.");
        }
        finally
        {
            IsPending = false;
            Message = null;
            _cancel = null;
        }
    }

    private void StartCountdown(int seconds)
    {
        _countdownCts?.Cancel();
        _countdownCts = new CancellationTokenSource();
        var token = _countdownCts.Token;
        SecondsLeft = seconds;
        IsPending = true;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int left = seconds; left > 0 && !token.IsCancellationRequested;)
                {
                    await Task.Delay(1000, token);
                    left--;
                    int value = left;
                    DispatcherHelper.PostToUi(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            SecondsLeft = value;
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }
}
