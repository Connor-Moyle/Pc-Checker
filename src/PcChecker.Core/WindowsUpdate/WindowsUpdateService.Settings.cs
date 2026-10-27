using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Pause, resume and active hours: the values the Settings app keeps under
/// <c>HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings</c>, written the same way Settings writes them. Policy-managed
/// settings are never written, and builds older than Windows 10 version 2004 are sent to Settings instead.
/// </summary>
public sealed partial class WindowsUpdateService
{
    private const string UseSettingsInstead = " Use Windows Update in Settings instead.";

    public Task<OperationResult> PauseAsync(int weeks, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        if (!UpdatePauseRules.SupportsRegistryPause(Environment.OSVersion.Version.Build))
        {
            return Task.FromResult(OperationResult.Fail("This version of Windows pauses updates differently." + UseSettingsInstead));
        }

        if (UpdatePolicyRules.Evaluate(ReadPolicyValues()).PauseManaged)
        {
            return Task.FromResult(OperationResult.Fail("Pausing updates is controlled by a policy on this PC."));
        }

        var settings = ReadUxSettings([]);
        var plan = UpdatePauseRules.Plan(settings.Pause, DateTimeOffset.UtcNow, weeks, settings.MaxPauseDays);
        if (!plan.Allowed)
        {
            return Task.FromResult(OperationResult.Fail(plan.Message));
        }

        var before = settings.Pause.IsPaused && settings.Pause.PausedUntil is { } until
            ? "paused until " + UpdatePauseRules.FormatRegistryTime(until)
            : "not paused";
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "Pause updates", Reversible: true,
            UndoHint: $"Before: {before}. Resume updates on the Windows Update page or in Settings → Windows Update.");
        var description = plan.IsExtension ? $"Extend the Windows Update pause by {weeks} week(s)" : $"Pause Windows Update for {weeks} week(s)";
        return _guard.RunGuardedAsync(description, options, _ =>
        {
            var start = UpdatePauseRules.FormatRegistryTime(plan.Start);
            var expiry = UpdatePauseRules.FormatRegistryTime(plan.Expiry);
            var values = new Dictionary<string, string>
            {
                [UpdatePauseRules.StartValue] = start,
                [UpdatePauseRules.ExpiryValue] = expiry,
                [UpdatePauseRules.FeatureStartValue] = start,
                [UpdatePauseRules.FeatureEndValue] = expiry,
                [UpdatePauseRules.QualityStartValue] = start,
                [UpdatePauseRules.QualityEndValue] = expiry,
            };
            var written = WriteUxValues(key =>
            {
                foreach (var (name, value) in values)
                {
                    key.SetValue(name, value, RegistryValueKind.String);
                }
            });
            if (!written.Success)
            {
                return Task.FromResult(written);
            }

            // Reading back only proves the values were stored (something else may have reset them at once). Whether the
            // update orchestrator honours them cannot be checked from here, so the message says where to confirm it.
            var check = ReadUxSettings([]);
            if (!check.Pause.IsPaused || check.Pause.PausedUntil != plan.Expiry)
            {
                _logger.LogWarning("Pause values did not read back as written (expected {Expiry}).", expiry);
                return Task.FromResult(OperationResult.Fail("Windows did not keep the pause." + UseSettingsInstead));
            }

            return Task.FromResult(OperationResult.Ok(plan.Message + " If Windows Update in Settings does not show the pause, pause there instead."));
        }, null, cancellationToken);
    }

    public Task<OperationResult> ResumeAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        if (UpdatePolicyRules.Evaluate(ReadPolicyValues()).PauseManaged)
        {
            return Task.FromResult(OperationResult.Fail("Pausing updates is controlled by a policy on this PC."));
        }

        var settings = ReadUxSettings([]);
        if (!settings.Pause.IsPaused)
        {
            return Task.FromResult(OperationResult.Ok("Updates are not paused."));
        }

        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "Resume updates", Reversible: true,
            UndoHint: $"Updates were paused until {UpdatePauseRules.FormatRegistryTime(settings.Pause.PausedUntil!.Value)}. Pause again on the Windows Update page.");
        return _guard.RunGuardedAsync("Resume Windows Update", options, _ =>
        {
            var removed = WriteUxValues(key =>
            {
                foreach (var name in UpdatePauseRules.AllValues)
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
            });
            if (!removed.Success)
            {
                return Task.FromResult(removed);
            }

            // As with pausing, this only proves the values are gone; Settings is where to confirm Windows agrees.
            return Task.FromResult(ReadUxSettings([]).Pause.IsPaused
                ? OperationResult.Fail("Windows still reports updates as paused." + UseSettingsInstead)
                : OperationResult.Ok("Updates are no longer paused. Windows will look for updates again shortly. If Settings still shows a pause, resume there."));
        }, null, cancellationToken);
    }

    public Task<OperationResult> SetActiveHoursAsync(int startHour, int endHour, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        var policy = UpdatePolicyRules.Evaluate(ReadPolicyValues());
        if (policy.ActiveHoursManaged)
        {
            return Task.FromResult(OperationResult.Fail("Active hours are set by a policy on this PC."));
        }

        if (ActiveHoursRules.Validate(startHour, endHour, ActiveHoursRules.EffectiveMaxSpan(policy.ActiveHoursMaxSpan)) is { } problem)
        {
            return Task.FromResult(OperationResult.Fail(problem));
        }

        if (!UpdatePauseRules.SupportsRegistryPause(Environment.OSVersion.Version.Build))
        {
            return Task.FromResult(OperationResult.Fail("This version of Windows keeps active hours differently." + UseSettingsInstead));
        }

        var current = ReadUxSettings([]).ActiveHours ?? ActiveHoursRules.FromRegistry(null, null, null);
        if (current.IsAutomatic)
        {
            // How Settings records "adjust automatically: off" is not documented, so PC Checker does not guess at it.
            return Task.FromResult(OperationResult.Fail(
                "Windows is adjusting active hours automatically. Turn off 'Adjust active hours automatically' in Settings → Windows Update → Advanced options, or set the hours there."));
        }

        var wanted = new ActiveHours(startHour, endHour, false);
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "Active hours", Reversible: true,
            UndoHint: $"Before: {ActiveHoursRules.Describe(current)}. Set them back on the Windows Update page or in Settings → Windows Update → Advanced options.");
        return _guard.RunGuardedAsync($"Set active hours to {ActiveHoursRules.Describe(wanted)}", options, _ =>
        {
            // Settings writes the hand-picked pair as well and restores ActiveHoursStart/End from it, so both are written.
            var written = WriteUxValues(key =>
            {
                key.SetValue(ActiveHoursRules.UserChoiceStartValue, startHour, RegistryValueKind.DWord);
                key.SetValue(ActiveHoursRules.UserChoiceEndValue, endHour, RegistryValueKind.DWord);
                key.SetValue(ActiveHoursRules.StartValue, startHour, RegistryValueKind.DWord);
                key.SetValue(ActiveHoursRules.EndValue, endHour, RegistryValueKind.DWord);
            });
            if (!written.Success)
            {
                return Task.FromResult(written);
            }

            // Reading back only proves the values were stored, so the message says where to confirm them.
            var check = ReadActiveHoursPairs();
            return Task.FromResult(check is { Start: { } s, End: { } e, ChoiceStart: { } cs, ChoiceEnd: { } ce }
                    && s == startHour && e == endHour && cs == startHour && ce == endHour
                ? OperationResult.Ok($"Active hours are now {ActiveHoursRules.Describe(wanted)}. Windows will not restart for updates during these hours. If Settings shows other hours, set them there.")
                : OperationResult.Fail("Windows did not keep the new active hours." + UseSettingsInstead));
        }, null, cancellationToken);
    }

    private (int? Start, int? End, int? ChoiceStart, int? ChoiceEnd) ReadActiveHoursPairs()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(UpdatePauseRules.SettingsKey);
            return (AsInt(key?.GetValue(ActiveHoursRules.StartValue)), AsInt(key?.GetValue(ActiveHoursRules.EndValue)),
                AsInt(key?.GetValue(ActiveHoursRules.UserChoiceStartValue)), AsInt(key?.GetValue(ActiveHoursRules.UserChoiceEndValue)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Active hours could not be read back.");
            return (null, null, null, null);
        }
    }

    private (UpdatePauseState Pause, ActiveHours? ActiveHours, int MaxPauseDays) ReadUxSettings(List<string> warnings)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(UpdatePauseRules.SettingsKey);
            if (key is null)
            {
                return (UpdatePauseState.NotPaused, ActiveHoursRules.FromRegistry(null, null, null), UpdatePauseRules.DefaultMaxPauseDays);
            }

            var pause = UpdatePauseRules.ReadState(key.GetValue(UpdatePauseRules.StartValue) as string,
                key.GetValue(UpdatePauseRules.ExpiryValue) as string, DateTimeOffset.UtcNow);
            var hours = ActiveHoursRules.FromRegistry(AsInt(key.GetValue(ActiveHoursRules.StartValue)),
                AsInt(key.GetValue(ActiveHoursRules.EndValue)), AsInt(key.GetValue(ActiveHoursRules.SmartStateValue)));
            var maxDays = UpdatePauseRules.EffectiveMaxPauseDays(AsInt(key.GetValue(UpdatePauseRules.MaxPauseDaysValue)));
            return (pause, hours, maxDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows Update settings could not be read.");
            warnings.Add("The pause and active hours settings could not be read.");
            return (UpdatePauseState.NotPaused, null, UpdatePauseRules.DefaultMaxPauseDays);
        }
    }

    private OperationResult WriteUxValues(Action<RegistryKey> write)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(UpdatePauseRules.SettingsKey, writable: true);
            if (key is null)
            {
                return OperationResult.Fail("The Windows Update settings key could not be opened." + UseSettingsInstead);
            }

            write(key);
            return OperationResult.Ok();
        }
        catch (UnauthorizedAccessException ex)
        {
            return OperationResult.Fail("PC Checker needs to run as an administrator to change this." + UseSettingsInstead, ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows Update settings could not be written.");
            return OperationResult.Fail("The Windows Update settings could not be changed: " + ex.Message + UseSettingsInstead, ex);
        }
    }

    private UpdatePolicyValues ReadPolicyValues() => ReadPolicyValues(_logger);

    /// <summary>The Windows Update policy values (Group Policy and device management), or empty when unreadable.</summary>
    internal static UpdatePolicyValues ReadPolicyValues(ILogger logger)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return new UpdatePolicyValues(ReadValues(hklm, UpdatePolicyRules.GroupPolicyKey), ReadValues(hklm, UpdatePolicyRules.AutomaticUpdatesKey),
                ReadValues(hklm, UpdatePolicyRules.DeviceManagementKey));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Windows Update policies could not be read.");
            return UpdatePolicyValues.Empty;
        }
    }

    private static Dictionary<string, object?> ReadValues(RegistryKey root, string path)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var key = root.OpenSubKey(path);
        if (key is null)
        {
            return values;
        }

        foreach (var name in key.GetValueNames())
        {
            if (name.Length > 0)
            {
                values[name] = key.GetValue(name);
            }
        }

        return values;
    }

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(path);
            return AsInt(key?.GetValue(name));
        }
        catch
        {
            return null;
        }
    }

    private static int? AsInt(object? value) => value switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        _ => null,
    };
}
