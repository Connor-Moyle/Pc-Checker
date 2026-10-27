using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Gaming;

namespace PcChecker.App.Services;

/// <summary>Game profile switcher, part 2: applying and putting back the overlay, fan and power plan parts of a profile.</summary>
public sealed partial class GameProfileSwitcher
{
    // ----- Overlay -----

    private async Task ApplyOverlayAsync(GameProfile profile, List<string> results)
    {
        var baseline = _baseline!;
        await DispatcherHelper.RunOnUiAsync(() =>
        {
            if (profile.OverlayPresetName is { Length: > 0 } presetName)
            {
                var preset = OverlayPresetCatalog.Find(presetName, _settings.Current.Overlay.Presets);
                if (preset is null)
                {
                    results.Add($"overlay preset \"{presetName}\" not found");
                }
                else
                {
                    OverlayPresetCatalog.Apply(preset, _settings.Current.Overlay);
                    _overlay.ApplySettings();
                    baseline.OverlayLayoutChanged = true;
                    results.Add($"overlay preset \"{preset.Name}\"");
                }
            }

            if (profile.ShowOverlay != _overlay.IsVisible)
            {
                if (profile.ShowOverlay)
                {
                    _overlay.Show();
                }
                else
                {
                    _overlay.Hide();
                }

                baseline.OverlayVisibilityChanged = true;
                results.Add(profile.ShowOverlay ? "overlay shown" : "overlay hidden");
            }
        }).ConfigureAwait(false);

        if (baseline.OverlayLayoutChanged)
        {
            await SaveSettingsAsync().ConfigureAwait(false);
        }
    }

    private async Task RevertOverlayAsync(Baseline baseline, List<string> results, bool shuttingDown)
    {
        if (!baseline.OverlayLayoutChanged && !baseline.OverlayVisibilityChanged)
        {
            return;
        }

        void Restore()
        {
            if (baseline.OverlayLayoutChanged)
            {
                OverlayPresetCatalog.Apply(baseline.OverlayLayout, _settings.Current.Overlay);
                results.Add("overlay layout restored");
            }

            if (shuttingDown)
            {
                _settings.Current.Overlay.Enabled = baseline.OverlayWasVisible;
                return;
            }

            if (baseline.OverlayLayoutChanged)
            {
                _overlay.ApplySettings();
            }

            if (baseline.OverlayVisibilityChanged)
            {
                if (baseline.OverlayWasVisible)
                {
                    _overlay.Show();
                }
                else
                {
                    _overlay.Hide();
                }

                results.Add(baseline.OverlayWasVisible ? "overlay shown again" : "overlay hidden again");
            }
        }

        if (shuttingDown)
        {
            Restore();
        }
        else
        {
            await DispatcherHelper.RunOnUiAsync(Restore).ConfigureAwait(false);
        }

        await SaveSettingsAsync().ConfigureAwait(false);
    }

    // ----- Fans -----

    private async Task ApplyFansAsync(GameProfile profile, List<string> results)
    {
        if (profile.FanProfileName is not { Length: > 0 } name)
        {
            return;
        }

        if (!_fans.IsAvailable)
        {
            results.Add("fan profile skipped (fan control is not available on this PC)");
            return;
        }

        var fanProfile = _settings.Current.Fans.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? _fans.BuiltInProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (fanProfile is null)
        {
            results.Add($"fan profile \"{name}\" not found");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await _fans.ApplyProfileAsync(fanProfile, cts.Token).ConfigureAwait(false);
        if (result.Success)
        {
            _baseline!.FansChanged = true;
        }

        results.Add(result.Success ? $"fans \"{fanProfile.Name}\"" : $"fans not changed ({result.Message})");
    }

    private async Task RevertFansAsync(Baseline baseline, List<string> results)
    {
        if (!baseline.FansChanged || !_fans.IsAvailable)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = baseline.FanProfile is { } previous
            ? await _fans.ApplyProfileAsync(previous, cts.Token).ConfigureAwait(false)
            : await _fans.RevertAllToAutoAsync(cts.Token).ConfigureAwait(false);
        results.Add(result.Success
            ? baseline.FanProfile is { } p ? $"fans back to \"{p.Name}\"" : "fans back to automatic"
            : $"fans could not be put back ({result.Message})");
    }

    // ----- Power plan -----

    private async Task ApplyPowerAsync(GameProfile profile, List<string> results)
    {
        if (profile.PowerPlanId is not { } plan || plan == Guid.Empty || plan == _baseline?.PowerPlanId)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await _profiles.SwitchPowerPlanAsync(plan, $"game profile \"{profile.Name}\"", cts.Token).ConfigureAwait(false);
        if (result.Success)
        {
            _baseline!.PowerChanged = true;
        }

        results.Add(result.Success ? result.Message.TrimEnd('.') : $"power plan not changed ({result.Message})");
    }

    private async Task RevertPowerAsync(Baseline baseline, List<string> results)
    {
        if (!baseline.PowerChanged || baseline.PowerPlanId is not { } plan)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await _profiles.SwitchPowerPlanAsync(plan, "game closed", cts.Token).ConfigureAwait(false);
        results.Add(result.Success ? result.Message.TrimEnd('.') : $"power plan could not be put back ({result.Message})");
    }
}
