using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Services;
using PcChecker.Core.Settings;
using PcChecker.Core.Shell;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The one-time "You're in Simple mode" hint on the Dashboard and in Settings. Shared, so closing it in one place closes
/// it everywhere. It shows in the first session that reaches it (marked as seen in settings straight away), stays up
/// for that session until closed, and never shows in Advanced mode or in screenshot runs (see
/// <see cref="SimpleModeRules.ShowSimpleModeHint"/>).
/// </summary>
public sealed partial class SimpleModeHintViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private bool _shownThisSession;
    private bool _closedThisSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private bool _isVisible;

    public SimpleModeHintViewModel(ISettingsService settings)
    {
        _settings = settings;
        Update(settings.Current);
        settings.Changed += (_, s) => DispatcherHelper.PostToUi(() => Update(s));
    }

    /// <summary>Bound two-way to the InfoBar: its close button sets this to false, which closes the hint.</summary>
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
        _closedThisSession = true;
        IsVisible = false;
    }

    /// <summary>Turns Advanced mode on from the hint (the same switch as the one at the bottom of the menu).</summary>
    [RelayCommand]
    private void TurnOnAdvancedMode()
    {
        _closedThisSession = true;
        IsVisible = false;
        _settings.Current.General.AdvancedMode = true;
        _ = SaveAsync();
    }

    private void Update(AppSettings settings)
    {
        var general = settings.General;
        bool show = SimpleModeRules.ShowSimpleModeHint(general.AdvancedMode, general.SimpleModeHintDismissed, _shownThisSession,
            _closedThisSession, AppOptions.Current.ScreenshotsRequested);
        if (show && !general.SimpleModeHintDismissed)
        {
            // Seen now: later sessions skip it, this one keeps it until it is closed.
            _shownThisSession = true;
            general.SimpleModeHintDismissed = true;
            _ = SaveAsync();
        }

        IsVisible = show;
    }

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
