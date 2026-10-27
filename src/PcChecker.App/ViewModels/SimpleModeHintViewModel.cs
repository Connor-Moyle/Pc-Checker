using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Services;
using PcChecker.Core.Settings;
using PcChecker.Core.Shell;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The one-time "You're in Simple mode" hint on the Dashboard and in Settings. Shared, so closing it in one place closes
/// it everywhere; it stays closed (saved in settings) and never shows in Advanced mode or in screenshot runs.
/// </summary>
public sealed partial class SimpleModeHintViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private bool _isVisible;

    public SimpleModeHintViewModel(ISettingsService settings)
    {
        _settings = settings;
        Update(settings.Current);
        settings.Changed += (_, s) => DispatcherHelper.PostToUi(() => Update(s));
    }

    /// <summary>Bound two-way to the InfoBar: its close button sets this to false, which dismisses the hint for good.</summary>
    public bool IsOpen
    {
        get => IsVisible;
        set
        {
            if (!value && IsVisible)
            {
                Dismiss();
            }
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        IsVisible = false;
        if (_settings.Current.General.SimpleModeHintDismissed)
        {
            return;
        }

        _settings.Current.General.SimpleModeHintDismissed = true;
        _ = SaveAsync();
    }

    /// <summary>Turns Advanced mode on from the hint (the same switch as the one at the bottom of the menu).</summary>
    [RelayCommand]
    private void TurnOnAdvancedMode()
    {
        _settings.Current.General.AdvancedMode = true;
        _settings.Current.General.SimpleModeHintDismissed = true;
        IsVisible = false;
        _ = SaveAsync();
    }

    private void Update(AppSettings settings) =>
        IsVisible = SimpleModeRules.ShowSimpleModeHint(settings.General.AdvancedMode, settings.General.SimpleModeHintDismissed, AppOptions.Current.ScreenshotsRequested);

    private async Task SaveAsync()
    {
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception)
        {
            // Not saving only means the hint may show once more next time.
        }
    }
}
