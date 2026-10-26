using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Input;

/// <summary>Ids of the optimization actions the Input module contributes.</summary>
public static class InputActionIds
{
    public const string PointerPrecisionOff = "input.pointerprecision.off";
    public const string PointerPrecisionOn = "input.pointerprecision.on";
    public const string GamingProfileApply = "input.gamingprofile.apply";

    public static IReadOnlyList<string> All { get; } = [PointerPrecisionOff, PointerPrecisionOn, GamingProfileApply];
}

/// <summary>
/// Exposes the keyboard &amp; mouse tweaks to the Optimize page. Every change goes through <see cref="IInputService"/>,
/// whose Windows implementation runs it under the change guard.
/// </summary>
public sealed class InputActionsProvider : IOptimizationActionProvider
{
    private const string Category = "Keyboard & Mouse";

    private readonly IInputService _input;

    public InputActionsProvider(IInputService input)
    {
        _input = input;
    }

    public IReadOnlyList<OptimizationAction> Actions { get; } =
    [
        new(InputActionIds.PointerPrecisionOff, "Turn off Enhance pointer precision",
            "Disables Windows mouse acceleration so the pointer moves the same distance for the same hand movement. Most gamers prefer this; you can turn it back on at any time.",
            Category, RiskLevel.Safe, Reversible: true, RequiresRestart: false, AdvancedOnly: false),
        new(InputActionIds.PointerPrecisionOn, "Turn on Enhance pointer precision",
            "Re-enables Windows mouse acceleration (the default): slow movements become more precise and fast flicks travel further.",
            Category, RiskLevel.Safe, Reversible: true, RequiresRestart: false, AdvancedOnly: false),
        new(InputActionIds.GamingProfileApply, "Apply the Gaming keyboard & mouse profile",
            "Pointer speed 6/11, acceleration off, no pointer trails and the shortest key repeat delay. Reverting applies the Windows default profile.",
            Category, RiskLevel.Safe, Reversible: true, RequiresRestart: false, AdvancedOnly: false),
    ];

    public bool Handles(string actionId) => InputActionIds.All.Contains(actionId, StringComparer.Ordinal);

    public async Task<ActionState> GetStateAsync(string id, CancellationToken cancellationToken)
    {
        var mouse = await _input.GetMouseAsync(cancellationToken).ConfigureAwait(false);
        string precision = mouse.EnhancePointerPrecision ? "Enhance pointer precision is on" : "Enhance pointer precision is off";
        return id switch
        {
            InputActionIds.PointerPrecisionOff => new ActionState(id, !mouse.EnhancePointerPrecision, precision),
            InputActionIds.PointerPrecisionOn => new ActionState(id, mouse.EnhancePointerPrecision, precision),
            InputActionIds.GamingProfileApply => new ActionState(id, LooksLikeGaming(mouse),
                $"Pointer speed {InputMath.SpeedToSliderText(mouse.Speed)}, {precision.ToLowerInvariant()}, trails {InputMath.TrailsText(mouse.PointerTrails).ToLowerInvariant()}"),
            _ => new ActionState(id, null, null),
        };
    }

    public Task<OperationResult> ApplyAsync(string id, CancellationToken cancellationToken) => id switch
    {
        InputActionIds.PointerPrecisionOff => SetPrecisionAsync(false, cancellationToken),
        InputActionIds.PointerPrecisionOn => SetPrecisionAsync(true, cancellationToken),
        InputActionIds.GamingProfileApply => ApplyProfileAsync("input.profile.gaming", cancellationToken),
        _ => Task.FromResult(OperationResult.Fail($"Unknown action '{id}'.")),
    };

    public Task<OperationResult> RevertAsync(string id, CancellationToken cancellationToken) => id switch
    {
        InputActionIds.PointerPrecisionOff => SetPrecisionAsync(true, cancellationToken),
        InputActionIds.PointerPrecisionOn => SetPrecisionAsync(false, cancellationToken),
        InputActionIds.GamingProfileApply => ApplyProfileAsync("input.profile.default", cancellationToken),
        _ => Task.FromResult(OperationResult.Fail($"Unknown action '{id}'.")),
    };

    private async Task<OperationResult> SetPrecisionAsync(bool enabled, CancellationToken cancellationToken)
    {
        var mouse = await _input.GetMouseAsync(cancellationToken).ConfigureAwait(false);
        if (mouse.EnhancePointerPrecision == enabled)
        {
            return OperationResult.Ok(enabled ? "Enhance pointer precision was already on." : "Enhance pointer precision was already off.");
        }

        mouse.EnhancePointerPrecision = enabled;
        return await _input.ApplyMouseAsync(mouse, cancellationToken).ConfigureAwait(false);
    }

    private Task<OperationResult> ApplyProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        var profile = _input.BuiltInProfiles.FirstOrDefault(p => p.Id == profileId);
        return profile is null
            ? Task.FromResult(OperationResult.Fail($"The built-in profile '{profileId}' is missing."))
            : _input.ApplyProfileAsync(profile, cancellationToken);
    }

    private static bool LooksLikeGaming(MouseSettings mouse) =>
        mouse.Speed == InputMath.DefaultSpeed && !mouse.EnhancePointerPrecision && mouse.PointerTrails < 2;
}
