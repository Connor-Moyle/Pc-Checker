using Microsoft.Extensions.DependencyInjection;

namespace PcChecker.App.ViewModels;

public sealed partial class SettingsViewModel
{
    private SimpleModeHintViewModel? _simpleModeHint;

    /// <summary>The one-time "You're in Simple mode" hint, shared with the Dashboard (closing one closes both).</summary>
    public SimpleModeHintViewModel SimpleModeHint => _simpleModeHint ??= App.Services.GetRequiredService<SimpleModeHintViewModel>();
}
