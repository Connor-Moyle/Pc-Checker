using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// Wi-Fi and network tuning: adapter driver properties, reversible system tweaks and saved Wi-Fi networks. Every
/// write goes through the change guard and remembers the previous value so Revert works after a restart.
/// </summary>
public interface INetworkTuningService
{
    /// <summary>Advanced properties of one adapter (or of every physical adapter when null) with recommendations.</summary>
    Task<IReadOnlyList<AdapterSetting>> GetAdapterSettingsAsync(string? adapterName, CancellationToken cancellationToken);

    /// <summary>Sets a driver property by registry keyword. The adapter restarts, so the connection drops briefly.</summary>
    Task<OperationResult> SetAdapterSettingAsync(string adapterName, string key, string value, CancellationToken cancellationToken);

    /// <summary>Puts back the value captured before PC Checker first changed this property.</summary>
    Task<OperationResult> RevertAdapterSettingAsync(string adapterName, string key, CancellationToken cancellationToken);

    /// <summary>True when a previous value was captured for this property, so <see cref="RevertAdapterSettingAsync"/> can run.</summary>
    bool CanRevertAdapterSetting(string adapterName, string key);

    IReadOnlyList<TuningAction> Actions { get; }

    Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken);

    Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken);

    Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WifiProfileInfo>> GetSavedNetworksAsync(CancellationToken cancellationToken);

    Task<OperationResult> SetNetworkPriorityAsync(string profileName, int priority, CancellationToken cancellationToken);

    /// <summary>Deletes a saved network (a backup of the profile is exported first so it can be added back).</summary>
    Task<OperationResult> ForgetNetworkAsync(string profileName, CancellationToken cancellationToken);

    Task<OperationResult> SetAutoConnectAsync(string profileName, bool enabled, CancellationToken cancellationToken);

    Task<OperationResult> SetMeteredAsync(string profileName, bool metered, CancellationToken cancellationToken);
}
