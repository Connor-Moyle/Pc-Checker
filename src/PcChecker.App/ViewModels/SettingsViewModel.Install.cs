using System.Windows;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.Updates;

namespace PcChecker.App.ViewModels;

/// <summary>
/// "Install in Program Files": the portable exe copies itself to C:\Program Files\PC Checker and reopens from there,
/// which Replace Task Manager, Start with Windows and the schedules need. Shown only while this copy runs from
/// somewhere else. The service is resolved lazily so this part needs no change to the view model's constructor.
/// </summary>
public sealed partial class SettingsViewModel
{
    private IAppInstallService? _appInstall;

    private IAppInstallService? AppInstall =>
        _appInstall ??= App.Services.GetService(typeof(IAppInstallService)) as IAppInstallService;

    /// <summary>True when this copy runs from outside Program Files and can install itself there.</summary>
    public bool CanInstallApp => AppInstall?.CanInstall == true;

    public string AppInstallHint => Loc.F("Copies PC Checker to {0} and opens it from there. Replace Task Manager, Start with Windows and schedules need this.",
        System.IO.Path.GetDirectoryName(AppInstall?.InstallPath ?? string.Empty) ?? string.Empty);

    [RelayCommand]
    private async Task InstallApp()
    {
        if (AppInstall is not { CanInstall: true, InstallPath: { } target } service)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync(Loc.T("Install in Program Files?"),
            Loc.F("PC Checker will be copied to {0}, added to the Start menu and opened from there; this window closes. Your settings are kept. The copy you started now is not changed and can be deleted afterwards.", target),
            Loc.T("Install and restart"));
        if (!ok)
        {
            return;
        }

        var result = await Task.Run(service.InstallAndStart);
        if (result.Success)
        {
            Application.Current?.Shutdown();
        }
        else
        {
            _notifications.Error(Loc.T("PC Checker was not installed"), result.Message);
        }
    }
}
