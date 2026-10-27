using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Power;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Gaming;

/// <summary>
/// Windows side of game profiles: lists power plans through the power service and switches them with
/// <c>powercfg /setactive</c> inside the change guard (journaled as module "Gaming", no restore point).
/// </summary>
public sealed class WindowsGameProfileService : IGameProfileService
{
    public const string JournalModule = "Gaming";

    private readonly IPowerService _power;
    private readonly IChangeGuard _changeGuard;
    private readonly ILogger<WindowsGameProfileService> _logger;

    public WindowsGameProfileService(IPowerService power, IChangeGuard changeGuard, ILogger<WindowsGameProfileService> logger)
    {
        _power = power;
        _changeGuard = changeGuard;
        _logger = logger;
    }

    public IReadOnlyList<GameProfile> SampleProfiles { get; } = [];

    public async Task<IReadOnlyList<PowerPlanInfo>> GetPowerPlansAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _power.GetPowerPlansAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Power plans could not be listed for game profiles.");
            return [];
        }
    }

    public async Task<OperationResult> SwitchPowerPlanAsync(Guid planId, string reason, CancellationToken cancellationToken)
    {
        var plans = await GetPowerPlansAsync(cancellationToken).ConfigureAwait(false);
        var previous = plans.FirstOrDefault(p => p.IsActive);
        string target = plans.FirstOrDefault(p => p.Id == planId)?.Name ?? PowerPlanParser.DescribeKnown(planId) ?? planId.ToString();
        if (previous?.Id == planId)
        {
            return OperationResult.Ok($"\"{target}\" is already the active power plan.");
        }

        if (plans.Count > 0 && plans.All(p => p.Id != planId))
        {
            return OperationResult.Fail($"The power plan chosen in the game profile ({target}) no longer exists on this PC. Edit the profile and pick another plan.");
        }

        // A plan switch is instantly reversible and happens on every game start, so no restore point is made for it.
        var options = new ChangeOptions(false, false, JournalModule, target, Reversible: true,
            previous is null ? "Choose the previous plan on the Power page." : $"Switch back to \"{previous.Name}\" (powercfg /setactive {previous.Id})");

        return await _changeGuard.RunGuardedAsync($"Switch power plan to \"{target}\" ({reason})", options, async ct =>
        {
            var result = await PowerCfg.RunAsync($"/setactive {planId}", ct).ConfigureAwait(false);
            if (!result.Success)
            {
                string error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
                return OperationResult.Fail($"Windows refused to switch the power plan: {error}");
            }

            return OperationResult.Ok($"Power plan switched to \"{target}\".");
        }, null, cancellationToken).ConfigureAwait(false);
    }
}
