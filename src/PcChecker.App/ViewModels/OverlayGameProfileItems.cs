using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Gaming;

namespace PcChecker.App.ViewModels;

/// <summary>One game profile in the list on the Overlay page.</summary>
public sealed partial class GameProfileRow : ObservableObject
{
    [ObservableProperty]
    private bool _enabled;

    public GameProfileRow(GameProfile profile, string summary, bool isActive)
    {
        Name = profile.Name;
        ExeName = profile.ExeName;
        Summary = summary;
        IsActive = isActive;
        _enabled = profile.Enabled;
    }

    public string Name { get; }

    public string ExeName { get; }

    /// <summary>What the profile changes: overlay preset, fan profile, power plan.</summary>
    public string Summary { get; }

    /// <summary>True while this profile is applied (its game is running).</summary>
    public bool IsActive { get; }

    /// <summary>Raised when the user flips the on/off switch.</summary>
    public event EventHandler? EnabledToggled;

    partial void OnEnabledChanged(bool value) => EnabledToggled?.Invoke(this, EventArgs.Empty);
}

/// <summary>A choice in the editor's drop-downs: a value (null = "leave as it is") and the text shown.</summary>
public sealed record ProfileChoice(string? Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A power plan choice in the editor (null id = "do not change the power plan").</summary>
public sealed record PowerPlanChoice(Guid? Id, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A running program offered by "Add running game", with its load as text.</summary>
public sealed record RunningAppRow(RunningApp App)
{
    public string Title => string.IsNullOrWhiteSpace(App.WindowTitle) ? App.ProcessName : App.WindowTitle;

    public string ExeName => App.ExeName;

    public string LoadText => App.GpuPercent is { } gpu
        ? $"GPU {gpu:0} % · CPU {App.CpuPercent:0} %"
        : $"CPU {App.CpuPercent:0} %";
}
