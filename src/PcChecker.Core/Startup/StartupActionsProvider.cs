using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Startup;

/// <summary>
/// Optimization actions the Startup module contributes to the Optimize page: fast startup on/off and a 3-second boot
/// menu. Changes go through <see cref="IStartupService"/>, which runs them through the change guard.
/// </summary>
public sealed class StartupActionsProvider : IOptimizationActionProvider
{
    public const string FastStartupOn = "startup.faststartup.on";
    public const string FastStartupOff = "startup.faststartup.off";
    public const string Timeout3s = "startup.timeout.3s";
    public const int ShortTimeoutSeconds = 3;
    public const int DefaultTimeoutSeconds = 30;

    private readonly IStartupService _startup;

    public StartupActionsProvider(IStartupService startup)
    {
        _startup = startup;
    }

    public IReadOnlyList<OptimizationAction> Actions { get; } =
    [
        new(FastStartupOn, "Turn fast startup on",
            "Saves the Windows kernel to disk at shutdown so the next boot is quicker. Uses hibernation (turned on if needed). Not recommended if you dual-boot another operating system.",
            "Startup", RiskLevel.Safe, Reversible: true, RequiresRestart: false, AdvancedOnly: false),
        new(FastStartupOff, "Turn fast startup off",
            "Makes every boot a complete fresh start. Fixes odd problems after shutdown and is the safe choice for dual-boot PCs. Boots take a few seconds longer.",
            "Startup", RiskLevel.Safe, Reversible: true, RequiresRestart: false, AdvancedOnly: false),
        new(Timeout3s, "Shorten the boot menu wait to 3 seconds",
            "When more than one operating system is installed, Windows waits 30 seconds at the boot menu by default. 3 seconds is enough to pick another entry.",
            "Startup", RiskLevel.Safe, Reversible: true, RequiresRestart: false, AdvancedOnly: false),
    ];

    public bool Handles(string actionId) => Actions.Any(a => a.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));

    public async Task<ActionState> GetStateAsync(string id, CancellationToken cancellationToken)
    {
        BootConfig boot;
        try
        {
            boot = await _startup.GetBootConfigAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new ActionState(id, null, null);
        }

        return id.ToLowerInvariant() switch
        {
            FastStartupOn => new ActionState(id, boot.FastStartup, boot.FastStartup ? "On" : "Off"),
            FastStartupOff => new ActionState(id, !boot.FastStartup, boot.FastStartup ? "On" : "Off"),
            Timeout3s => new ActionState(id, boot.TimeoutSeconds <= ShortTimeoutSeconds, $"{boot.TimeoutSeconds} s"),
            _ => new ActionState(id, null, null),
        };
    }

    public Task<OperationResult> ApplyAsync(string id, CancellationToken cancellationToken) => id.ToLowerInvariant() switch
    {
        FastStartupOn => _startup.SetFastStartupAsync(true, cancellationToken),
        FastStartupOff => _startup.SetFastStartupAsync(false, cancellationToken),
        Timeout3s => _startup.SetBootTimeoutAsync(ShortTimeoutSeconds, cancellationToken),
        _ => Task.FromResult(OperationResult.Fail($"Unknown startup action '{id}'.")),
    };

    public Task<OperationResult> RevertAsync(string id, CancellationToken cancellationToken) => id.ToLowerInvariant() switch
    {
        FastStartupOn => _startup.SetFastStartupAsync(false, cancellationToken),
        FastStartupOff => _startup.SetFastStartupAsync(true, cancellationToken),
        Timeout3s => _startup.SetBootTimeoutAsync(DefaultTimeoutSeconds, cancellationToken),
        _ => Task.FromResult(OperationResult.Fail($"Unknown startup action '{id}'.")),
    };
}
