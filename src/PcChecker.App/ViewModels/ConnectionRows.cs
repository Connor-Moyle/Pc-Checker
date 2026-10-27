using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Connections;

namespace PcChecker.App.ViewModels;

/// <summary>One open endpoint on the Connections page. Rows are kept by key and updated in place every refresh.</summary>
public sealed partial class ConnectionRow : ObservableObject
{
    public ConnectionRow(NetworkEndpoint endpoint, ConnectionProcess? process)
    {
        _endpoint = endpoint;
        _process = process;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(StateToolTip), nameof(RemoteText), nameof(IsExposed))]
    private NetworkEndpoint _endpoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Program), nameof(IsExposed), nameof(PathText))]
    private ConnectionProcess? _process;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private ImageSource? _icon;

    [ObservableProperty]
    private string? _hostName;

    public bool HasIcon => Icon is not null;

    public string Key => Endpoint.Key;

    public string Program => ConnectionProcesses.NameOf(Process, Endpoint.Pid);

    public string PathText => Process?.Path ?? Program;

    public string PidText => Endpoint.Pid.ToString(CultureInfo.CurrentCulture);

    public string ProtocolText => ConnectionText.Protocol(Endpoint);

    public string LocalText => ConnectionText.Endpoint(Endpoint.LocalAddress, Endpoint.LocalPort);

    public string RemoteText => ConnectionText.Remote(Endpoint);

    public string StateText => ConnectionText.State(Endpoint);

    public string StateToolTip => Endpoint.Protocol == ConnectionProtocol.Tcp
        ? ConnectionText.TechnicalState(Endpoint.State)
        : Loc.T("UDP sends and receives without a fixed connection.");

    public bool IsExposed => ConnectionRules.AcceptsFromOtherDevices(Endpoint, Process?.IsWindows ?? false);

    public string CopyText() => ConnectionText.CopyLine(Endpoint, Program, HostName);
}

/// <summary>A program in the list on the left: every process with the same file, with its connection counts.</summary>
public sealed partial class ConnectionProgramRow : ObservableObject
{
    public ConnectionProgramRow(string key, string name)
    {
        Key = key;
        _name = name;
    }

    /// <summary>The empty key is the "All programs" row.</summary>
    public string Key { get; }

    public bool IsAll => Key.Length == 0;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isExposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private ImageSource? _icon;

    [ObservableProperty]
    private ConnectionGroup? _group;

    public bool HasIcon => Icon is not null;

    public string CountText => Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>What a screen reader says for the item: name, count and the exposure note.</summary>
    public string AutomationName => IsExposed
        ? Loc.F("{0}, {1} connections, accepts connections from other devices", Name, Count)
        : Loc.F("{0}, {1} connections", Name, Count);

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(AutomationName));
    }

    partial void OnIsExposedChanged(bool value) => OnPropertyChanged(nameof(AutomationName));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(AutomationName));
}
