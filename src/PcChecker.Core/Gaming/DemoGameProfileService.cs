using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Power;

namespace PcChecker.Core.Gaming;

/// <summary>Demo game profiles: two sample profiles and power plan switches handled by the (demo) power service.</summary>
public sealed class DemoGameProfileService : IGameProfileService
{
    private readonly IPowerService _power;

    public DemoGameProfileService(IPowerService power)
    {
        _power = power;
    }

    public IReadOnlyList<GameProfile> SampleProfiles { get; } =
    [
        new GameProfile
        {
            Name = "Cyberpunk 2077",
            ExeName = "Cyberpunk2077.exe",
            ExePath = @"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
            OverlayPresetName = OverlayPresetCatalog.Full,
            ShowOverlay = true,
            FanProfileName = FanCurveMath.PresetPerformance,
            PowerPlanId = PowerPlanParser.HighPerformance,
        },
        new GameProfile
        {
            Name = "Elden Ring",
            ExeName = "eldenring.exe",
            ExePath = @"C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING\Game\eldenring.exe",
            OverlayPresetName = OverlayPresetCatalog.Minimal,
            ShowOverlay = true,
            FanProfileName = FanCurveMath.PresetBalanced,
        },
    ];

    public async Task<IReadOnlyList<PowerPlanInfo>> GetPowerPlansAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 150).ConfigureAwait(false);
        return await _power.GetPowerPlansAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> SwitchPowerPlanAsync(Guid planId, string reason, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 150).ConfigureAwait(false);
        return await _power.SetActivePowerPlanAsync(planId, cancellationToken).ConfigureAwait(false);
    }
}
