using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.Fans;

namespace PcChecker.App.ViewModels;

/// <summary>Profile picker, save/delete and the "revert all" command of the Fans page.</summary>
public sealed partial class FansViewModel
{
    private void RefreshProfiles()
    {
        var selectedName = SelectedProfile?.Name;
        Profiles.Clear();
        foreach (var p in _fans.BuiltInProfiles)
        {
            Profiles.Add(new FanProfileItem(p, true));
        }

        foreach (var p in Settings.Current.Fans.Profiles)
        {
            Profiles.Add(new FanProfileItem(p, false));
        }

        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == selectedName)
            ?? Profiles.FirstOrDefault(p => p.Name == Settings.Current.Fans.ActiveProfileName)
            ?? Profiles.FirstOrDefault(p => p.Name == FanCurveMath.PresetBalanced)
            ?? Profiles.FirstOrDefault();
    }
    [RelayCommand]
    private async Task ApplyProfileAsync()
    {
        if (SelectedProfile is not { } item)
        {
            return;
        }

        if (!await EnsureControlConsentAsync())
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _fans.ApplyProfileAsync(item.Profile, ct);
            Report(result);
            if (result.Success)
            {
                ActiveProfileName = item.Name;
                LastAppliedProfileName = item.Name;
                Settings.Current.Fans.ActiveProfileName = item.Name;
                await SaveSettingsAsync();
                foreach (var fan in Fans)
                {
                    if (item.Profile.Fans.TryGetValue(fan.Id, out var assignment))
                    {
                        fan.LoadAssignment(assignment);
                    }
                }

                SoftwareControlledCount = Fans.Count(f => f.IsSoftwareControlled);
            }
        }, Loc.F("Applying profile \"{0}\"…", item.Name));
    }

    [RelayCommand]
    private async Task SaveProfileAsAsync()
    {
        var name = (NewProfileName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            ErrorMessage = Loc.T("Type a name for the profile first (for example \"Quiet night\").");
            return;
        }

        if (_fans.BuiltInProfiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = $"\"{name}\" is a built-in profile. Choose another name.";
            return;
        }

        var profile = new FanProfile { Name = name };
        foreach (var fan in Fans.Where(f => f.Controllable))
        {
            profile.Fans[fan.Id] = fan.ToAssignment();
        }

        var list = Settings.Current.Fans.Profiles;
        var existing = list.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            bool replace = await _dialogs.ConfirmAsync(Loc.T("Replace this profile?"),
                Loc.F("A profile called \"{0}\" already exists. Replace it with the current fan settings?", name), Loc.T("Replace"));
            if (!replace)
            {
                return;
            }

            list[existing] = profile;
        }
        else
        {
            list.Add(profile);
        }

        await SaveSettingsAsync();
        RefreshProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == name);
        NewProfileName = null;
        _notifications.Success(Loc.T("Profile saved"), Loc.F("\"{0}\" remembers the mode, speed and curve of every controllable fan.", name));
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { IsBuiltIn: false } item)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync(Loc.T("Delete this profile?"),
            Loc.F("\"{0}\" will be removed. Fans keep running as they are now.", item.Name), Loc.T("Delete"), isDanger: true);
        if (!ok)
        {
            return;
        }

        Settings.Current.Fans.Profiles.RemoveAll(p => p.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(Settings.Current.Fans.ActiveProfileName, item.Name, StringComparison.OrdinalIgnoreCase))
        {
            Settings.Current.Fans.ActiveProfileName = null;
            LastAppliedProfileName = null;
        }

        await SaveSettingsAsync();
        RefreshProfiles();
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        bool ok = await _dialogs.ConfirmAsync(Loc.T("Return all fans to automatic?"),
            Loc.T("Every fan goes back to the speed the motherboard or graphics driver chooses. Your saved profiles are kept."), Loc.T("Revert all"));
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _fans.RevertAllToAutoAsync(ct);
            Report(result);
            if (result.Success)
            {
                ActiveProfileName = null;
                LastAppliedProfileName = null;
                Settings.Current.Fans.ActiveProfileName = null;
                await SaveSettingsAsync();
                foreach (var fan in Fans)
                {
                    fan.Mode = FanMode.Auto;
                    fan.IsAutoSelected = true;
                }

                SoftwareControlledCount = 0;
            }
        }, Loc.T("Returning fans to automatic…"));
    }

}
