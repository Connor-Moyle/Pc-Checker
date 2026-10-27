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

            // Read the values back: if Windows did not keep them, say so and point to Settings.
            var check = ReadUxSettings([]);
            if (!check.Pause.IsPaused || check.Pause.PausedUntil != plan.Expiry)
            {
                _logger.LogWarning("Pause values did not read back as written (expected {Expiry}).", expiry);
                return Task.FromResult(OperationResult.Fail("Windows did not keep the pause." + UseSettingsInstead));
            }

            return Task.FromResult(OperationResult.Ok(plan.Message + " Windows Update in Settings shows the same date."));
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

            return Task.FromResult(ReadUxSettings([]).Pause.IsPaused
                ? OperationResult.Fail("Windows still reports updates as paused." + UseSettingsInstead)
                : OperationResult.Ok("Updates are no longer paused. Windows will look for updates again shortly."));
        }, null, cancellationToken);
    }

    public Task<OperationResult> SetActiveHoursAsync(int startHour, int endHour, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail(NotOnWindows));
        }

        if (ActiveHoursRules.Validate(startHour, endHour) is { } problem)
        {
            return Task.FromResult(OperationResult.Fail(problem));
        }

        if (UpdatePolicyRules.Evaluate(ReadPolicyValues()).ActiveHoursManaged)
        {
            return Task.FromResult(OperationResult.Fail("Active hours are set by a policy on this PC."));
        }

        var current = ReadUxSettings([]).ActiveHours ?? ActiveHoursRules.FromRegistry(null, null, null);
        var wanted = new ActiveHours(startHour, endHour, false);
        var options = new ChangeOptions(CreateBefore: false, CreateAfter: false, Module, "Active hours", Reversible: true,
            UndoHint: $"Before: {ActiveHoursRules.Describe(current)}. Set them back on the Windows Update page or in Settings → Windows Update → Advanced options.");
        return _guard.RunGuardedAsync($"Set active hours to {ActiveHoursRules.Describe(wanted)}", options, _ =>
        {
            var written = WriteUxValues(key =>
            {
                key.SetValue(ActiveHoursRules.StartValue, startHour, RegistryValueKind.DWord);
                key.SetValue(ActiveHoursRules.EndValue, endHour, RegistryValueKind.DWord);
                if (current.IsAutomatic)
                {
                    // Choosing hours by hand in Settings switches the automatic adjustment off.
                    key.SetValue(ActiveHoursRules.SmartStateValue, 0, RegistryValueKind.DWord);
                }
            });
            if (!written.Success)
            {
                return Task.FromResult(written);
            }

            var check = ReadUxSettings([]).ActiveHours;
            return Task.FromResult(check is { } h && h.StartHour == startHour && h.EndHour == endHour
                ? OperationResult.Ok($"Active hours are now {ActiveHoursRules.Describe(wanted)}. Windows will not restart for updates during these hours.")
                : OperationResult.Fail("Windows did not keep the new active hours." + UseSettingsInstead));
        }, null, cancellationToken);
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

    private UpdatePolicyValues ReadPolicyValues()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return new UpdatePolicyValues(ReadValues(hklm, UpdatePolicyRules.GroupPolicyKey), ReadValues(hklm, UpdatePolicyRules.AutomaticUpdatesKey),
                ReadValues(hklm, UpdatePolicyRules.DeviceManagementKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows Update policies could not be read.");
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
