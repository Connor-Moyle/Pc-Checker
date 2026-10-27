using PcChecker.Core.Common;
using PcChecker.Core.Power;

namespace PcChecker.Core.Gaming;

/// <summary>
/// The system-facing side of game profiles: power plan switches made on behalf of a game (journaled through the
/// change guard) and the sample profiles a new installation can start from. Profile storage lives in
/// <c>AppSettings.Gaming</c>; matching and switching decisions are pure (<see cref="GameProfileMatcher"/>,
/// <see cref="ProfileSwitchPlanner"/>).
/// </summary>
public interface IGameProfileService
{
    /// <summary>The power plans a profile can choose from, with the active one marked.</summary>
    Task<IReadOnlyList<PowerPlanInfo>> GetPowerPlansAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Switches the power plan because a game started or stopped. Runs through the change guard and is recorded in
    /// the action journal (module "Gaming"); no restore point is made, because a plan switch is instantly reversible
    /// and happens every time a game starts.
    /// </summary>
    Task<OperationResult> SwitchPowerPlanAsync(Guid planId, string reason, CancellationToken cancellationToken);

    /// <summary>Profiles offered when the list is empty (empty on a real PC; two examples in demo mode).</summary>
    IReadOnlyList<GameProfile> SampleProfiles { get; }
}
