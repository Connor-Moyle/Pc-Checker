using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.AppHistory;

namespace PcChecker.App.ViewModels;

/// <summary>One app on the App history page. The numeric properties are what the columns sort by.</summary>
public sealed partial class AppHistoryRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private ImageSource? _icon;

    public AppHistoryRow(AppUsageSummary summary, string? publisher)
    {
        Summary = summary;
        Publisher = publisher;
    }

    public AppUsageSummary Summary { get; }

    public string Name => Summary.Name;

    public string? Publisher { get; }

    public bool HasPublisher => !string.IsNullOrWhiteSpace(Publisher);

    public string? Path => Summary.Path;

    public bool HasPath => !string.IsNullOrWhiteSpace(Summary.Path);

    public string PathText => Summary.Path ?? Summary.AppKey;

    public bool HasIcon => Icon is not null;

    public double CpuSeconds => Summary.CpuTime.TotalSeconds;

    public string CpuText => AppHistoryText.CpuTime(Summary.CpuTime);

    public long NetworkSort => Summary.NetworkBytes ?? -1;

    public string NetworkText => AppHistoryText.Bytes(Summary.NetworkBytes);

    public long MeteredSort => Summary.MeteredBytes ?? -1;

    public string MeteredText => AppHistoryText.Bytes(Summary.MeteredBytes);

    public long ReadSort => Summary.DiskReadBytes ?? -1;

    public string ReadText => AppHistoryText.Bytes(Summary.DiskReadBytes);

    public long WriteSort => Summary.DiskWriteBytes ?? -1;

    public string WriteText => AppHistoryText.Bytes(Summary.DiskWriteBytes);

    public DateTimeOffset FirstSeen => Summary.FirstSeen;

    public string FirstSeenText => Summary.FirstSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public DateTimeOffset LastSeen => Summary.LastSeen;

    public string LastSeenText => Summary.LastSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>The name to search for on the Processes page: the program file's name without ".exe".</summary>
    public string ProcessSearchText
    {
        get
        {
            var file = System.IO.Path.GetFileNameWithoutExtension(Summary.Path ?? string.Empty);
            return string.IsNullOrWhiteSpace(file) ? Summary.Name : file;
        }
    }
}
