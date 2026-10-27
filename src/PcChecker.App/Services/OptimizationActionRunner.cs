using Microsoft.Extensions.Logging;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Optimization;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.Services;

/// <summary>Outcome of one optimization run, raised through <see cref="OptimizationActionRunner.ActionCompleted"/>.</summary>
public sealed record OptimizationRunResult(string ActionId, bool Reverted, OperationResult Result);

/// <summary>
/// The one place that applies or reverts an optimization action from the UI: confirmation dialog (explaining risk,
/// reversibility, restore point and restart), the guarded change through <see cref="IOptimizationService"/>, a
/// snackbar with the outcome, and the "restart required" flag pages show in an InfoBar. Shared by the Performance,
/// Optimize, Latency and Health pages.
/// </summary>
public sealed class OptimizationActionRunner
{
    private readonly IOptimizationService _optimization;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private readonly ILogger<OptimizationActionRunner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OptimizationActionRunner(IOptimizationService optimization, IDialogService dialogs, INotificationService notifications,
        ISettingsService settings, INavigationService navigation, ILogger<OptimizationActionRunner> logger)
    {
        _optimization = optimization;
        _dialogs = dialogs;
        _notifications = notifications;
        _settings = settings;
        _navigation = navigation;
        _logger = logger;
    }

    /// <summary>True once any action in this session reported that a restart is needed.</summary>
    public bool IsRebootRequired { get; private set; }

    /// <summary>True while an action is running; pages observe <see cref="RunningChanged"/> and disable their Apply/Fix buttons.</summary>
    public bool IsRunning { get; private set; }

    public event EventHandler? RebootRequiredChanged;

    /// <summary>Raised when <see cref="IsRunning"/> changes (may be raised on a background thread).</summary>
    public event EventHandler? RunningChanged;

    public event EventHandler<OptimizationRunResult>? ActionCompleted;

    /// <summary>One-line explanation of the restore point policy for confirmation dialogs.</summary>
    public string RestorePointNote => PcChecker.Core.Restore.RestorePointPolicyText.Note(_settings.Current.RestorePoints.CreateBeforeChanges);

    /// <summary>Confirms with the user, runs the action and notifies. Returns true when the change succeeded.</summary>
    public Task<bool> RunAsync(string actionId, bool revert = false) => RunAsync(actionId, revert, confirm: true, CancellationToken.None);

    /// <summary>Same as <see cref="RunAsync(string, bool)"/> but lets a batch skip the per-action dialog.</summary>
    public async Task<bool> RunAsync(string actionId, bool revert, bool confirm, CancellationToken cancellationToken)
    {
        var action = _optimization.Actions.FirstOrDefault(a => a.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));
        if (action is null)
        {
            _notifications.Warning("Not available", $"\"{actionId}\" is not an optimization this version offers.");
            return false;
        }

        if (action.Id.Equals("tempfiles.clean", StringComparison.OrdinalIgnoreCase))
        {
            _navigation.Navigate(typeof(CleanupPage));
            return true;
        }

        // Dual-CCD Ryzen 9 X3D: actions that undo AMD's CCD parking (High/Ultimate plan, no core parking, Game Bar
        // off) always ask, even inside a batch, and the dialog says why.
        var x3dWarning = revert ? null : AmdPlatformRules.ActionWarning(action.Id, LocalCpu.Name);
        if (x3dWarning is not null && !confirm)
        {
            _notifications.Warning(action.Title, x3dWarning);
            return false;
        }

        if (confirm && !await _dialogs.ConfirmAsync(
                revert ? $"Undo \"{action.Title}\"?" : $"{action.Title}?",
                BuildConfirmation(action, revert),
                revert ? "Undo" : "Apply",
                isDanger: OptimizationRisk.IsDanger(action) || x3dWarning is not null))
        {
            return false;
        }

        if (IsRunning && confirm)
        {
            _notifications.Info("Please wait", "Another optimization is still running. Try again when it has finished.");
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        SetRunning(true);
        OperationResult result;
        try
        {
            _logger.LogInformation("{Verb} optimization {Action}.", revert ? "Reverting" : "Applying", action.Id);
            result = revert
                ? await _optimization.RevertAsync(action.Id, cancellationToken)
                : await _optimization.ApplyAsync(action.Id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = OperationResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Optimization {Action} threw.", action.Id);
            result = OperationResult.FromException(ex);
        }
        finally
        {
            SetRunning(false);
            _gate.Release();
        }

        if (result.Success)
        {
            _notifications.Success(revert ? "Undone" : "Applied", result.Message);
            if (result.RebootRequired || action.RequiresRestart)
            {
                IsRebootRequired = true;
                RebootRequiredChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _notifications.Error(revert ? "Could not undo" : "Could not apply", result.Message);
        }

        ActionCompleted?.Invoke(this, new OptimizationRunResult(action.Id, revert, result));
        return result.Success;
    }

    private void SetRunning(bool running)
    {
        if (IsRunning == running)
        {
            return;
        }

        IsRunning = running;
        try
        {
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A RunningChanged handler threw.");
        }
    }

    /// <summary>The plain-English confirmation text for an action.</summary>
    public string BuildConfirmation(OptimizationAction action, bool revert)
    {
        var lines = new List<string> { action.Description, string.Empty };
        if (!revert && AmdPlatformRules.ActionWarning(action.Id, LocalCpu.Name) is { } x3d)
        {
            lines.Insert(0, x3d);
            lines.Insert(1, string.Empty);
        }

        lines.Add("Risk: " + action.Risk switch
        {
            RiskLevel.Safe => "safe — a standard Windows setting.",
            RiskLevel.Moderate => "moderate — changes how Windows behaves; read the description first.",
            _ => "advanced — only continue if you understand what this does.",
        });
        lines.Add(action.Reversible ? "Reversible: yes, with the Revert button." : "Reversible: no — this runs a one-off tool or repair.");
        if (action.RequiresRestart)
        {
            lines.Add("A restart is needed afterwards.");
        }

        if (!revert)
        {
            lines.Add(RestorePointNote);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
