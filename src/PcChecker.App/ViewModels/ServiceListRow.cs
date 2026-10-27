using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Services;

namespace PcChecker.App.ViewModels;

/// <summary>One row in the Services list: the plain-text columns and the underlying <see cref="ServiceDetails"/>.</summary>
public sealed partial class ServiceListRow : ObservableObject
{
    public ServiceListRow(ServiceDetails details)
    {
        Update(details);
    }

    [ObservableProperty]
    private ServiceDetails _details = null!;

    public string Name => Details.Name;

    public string DisplayName => Details.DisplayName;

    public string? Description => Details.Description;

    public string StatusText => ServiceStartupMapping.StateLabel(Details.State);

    public string StartupText => ServiceStartupMapping.Label(Details.StartupType);

    public string LogOnText => string.IsNullOrWhiteSpace(Details.LogOnAs) ? "—" : Details.LogOnAs!;

    public string PidText => Details.Pid is { } pid ? pid.ToString(System.Globalization.CultureInfo.CurrentCulture) : "—";

    public string GroupText => string.IsNullOrWhiteSpace(Details.Group) ? "—" : Details.Group!;

    public bool IsRunning => Details.IsRunning;

    public bool IsCritical => ServiceSafetyRules.IsLocked(Details);

    public string? SafeToSetManualNote => ServiceSafetyRules.SafeToSetManualNote(Details);

    public bool HasSafeNote => SafeToSetManualNote is not null;

    public string PublisherKind => Details.IsMicrosoft ? Loc.T("Windows") : Loc.T("Other");

    /// <summary>Replaces the row's data in place so the grid keeps the selection and scroll position.</summary>
    public void Update(ServiceDetails details)
    {
        Details = details;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StartupText));
        OnPropertyChanged(nameof(LogOnText));
        OnPropertyChanged(nameof(PidText));
        OnPropertyChanged(nameof(GroupText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCritical));
        OnPropertyChanged(nameof(SafeToSetManualNote));
        OnPropertyChanged(nameof(HasSafeNote));
        OnPropertyChanged(nameof(PublisherKind));
    }
}
