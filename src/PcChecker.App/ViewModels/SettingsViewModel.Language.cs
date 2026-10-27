using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.Core.Localization;

namespace PcChecker.App.ViewModels;

/// <summary>Settings: interface language. A change is saved at once and takes effect when PC Checker restarts.</summary>
public sealed partial class SettingsViewModel
{
    public IReadOnlyList<AppLanguage> Languages { get; } = LanguageCatalog.All;

    [ObservableProperty]
    private AppLanguage? _selectedLanguage;

    /// <summary>True after the language was changed in this run, until the app restarts.</summary>
    [ObservableProperty]
    private bool _languageRestartNeeded;

    private string? _languageAtStart;

    private void LoadLanguageSettings(Core.Settings.AppSettings s)
    {
        var code = string.IsNullOrWhiteSpace(s.General.Language) ? LanguageCatalog.System : s.General.Language;
        _languageAtStart ??= code;
        SelectedLanguage = Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? Languages[0];
    }

    partial void OnSelectedLanguageChanged(AppLanguage? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        Persist(s => s.General.Language = value.Code);
        var before = LanguageCatalog.Resolve(_languageAtStart, CultureInfo.CurrentUICulture);
        LanguageRestartNeeded = LanguageCatalog.Resolve(value.Code, CultureInfo.CurrentUICulture) != before;
    }

    /// <summary>Starts a new copy (which waits for this one to close) and closes this one.</summary>
    [RelayCommand]
    private void RestartForLanguage()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Where(a => a is not "--minimized"))
            {
                start.ArgumentList.Add(arg);
            }

            start.ArgumentList.Add("--wait-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(start);
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            ErrorMessage = "PC Checker could not restart itself: " + ex.Message + " Close and open it again to switch language.";
        }
    }
}
