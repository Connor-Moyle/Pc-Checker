using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Converters;
using PcChecker.Core.Common;
using PcChecker.Core.Network;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Optimization;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>Shared brush and text helpers for the Network page rows.</summary>
internal static class NetworkUi
{
    public static Brush Severity(Severity severity) => severity switch
    {
        Core.Common.Severity.Critical => SeverityToBrushConverter.Critical,
        Core.Common.Severity.High => SeverityToBrushConverter.High,
        Core.Common.Severity.Medium => SeverityToBrushConverter.Medium,
        Core.Common.Severity.Low => SeverityToBrushConverter.Low,
        _ => SeverityToBrushConverter.Info,
    };

    public static Brush Status(HealthStatus status) => status switch
    {
        HealthStatus.Good => HealthStatusToBrushConverter.Good,
        HealthStatus.Warning => HealthStatusToBrushConverter.Warning,
        HealthStatus.Critical => HealthStatusToBrushConverter.Critical,
        _ => HealthStatusToBrushConverter.Unknown,
    };

    public static Brush Risk(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => HealthStatusToBrushConverter.Good,
        RiskLevel.Moderate => HealthStatusToBrushConverter.Warning,
        _ => HealthStatusToBrushConverter.Critical,
    };

    public static string RiskText(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Moderate",
        _ => "Advanced",
    };

    public static Brush Signal(int? percent) => percent switch
    {
        null => HealthStatusToBrushConverter.Unknown,
        >= WifiRules.FairSignalPercent => HealthStatusToBrushConverter.Good,
        >= WifiRules.WeakSignalPercent => HealthStatusToBrushConverter.Warning,
        _ => HealthStatusToBrushConverter.Critical,
    };

    public static string Ms(double? ms) => ms is null ? "—" : ms < 10 ? $"{ms:0.0} ms" : $"{ms:0} ms";

    public static string Mbps(double? mbps) => mbps is null ? "—" : mbps >= 100 ? $"{mbps:0}" : $"{mbps:0.#}";

    public static string LinkSpeed(long? bps) => bps switch
    {
        null or <= 0 => "—",
        >= 1_000_000_000 => $"{bps / 1_000_000_000.0:0.#} Gbps",
        _ => $"{bps / 1_000_000.0:0} Mbps",
    };
}

/// <summary>A Wi-Fi finding with its optional one-click fix.</summary>
public sealed partial class NetworkFindingItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFix))]
    private bool _isFixed;

    [ObservableProperty]
    private string? _resultMessage;

    public NetworkFindingItem(NetworkFinding finding, TuningAction? action)
    {
        Finding = finding;
        Action = action;
    }

    public NetworkFinding Finding { get; }
    public TuningAction? Action { get; }
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string SeverityText => Finding.Severity.ToString();
    public Brush SeverityBrush => NetworkUi.Severity(Finding.Severity);
    public bool HasAction => Action is not null;
    public bool CanFix => HasAction && !IsFixed;
}

/// <summary>One access point in the "Nearby networks" table.</summary>
public sealed class NearbyNetworkItem
{
    public NearbyNetworkItem(WifiNetworkInfo network) => Network = network;

    public WifiNetworkInfo Network { get; }
    public string Ssid => string.IsNullOrWhiteSpace(Network.Ssid) ? "(hidden network)" : Network.Ssid;
    public bool IsHidden => string.IsNullOrWhiteSpace(Network.Ssid);
    public string Bssid => Network.Bssid;
    public int Signal => Network.SignalPercent;
    public string SignalText => $"{Network.SignalPercent} %";
    public Brush SignalBrush => NetworkUi.Signal(Network.SignalPercent);
    public int Channel => Network.Channel;
    public string Band => Network.Band;
    public string Radio => WifiBands.DescribeRadio(Network.Radio);
    public string Security => string.IsNullOrWhiteSpace(Network.Authentication) ? "—" : Network.Authentication;
    public bool IsConnected => Network.IsConnected;
}

/// <summary>A band with one bar per channel.</summary>
public sealed record ChannelBandGroup(string Band, string Caption, IReadOnlyList<ChannelBarItem> Bars);

/// <summary>One channel bar in the congestion chart.</summary>
public sealed record ChannelBarItem(int Channel, int NetworkCount, int Maximum, bool IsYours, bool IsRecommended, int StrongestSignal)
{
    public string Label => $"Ch {Channel}";
    public string CountText => NetworkCount == 1 ? "1 network" : $"{NetworkCount} networks";
    public string Tag => IsYours ? "yours" : IsRecommended ? "best" : string.Empty;
    public bool HasTag => IsYours || IsRecommended;
    public Brush BarBrush => IsYours ? SeverityToBrushConverter.Info : NetworkCount > WifiRules.CrowdedNetworkCount ? SeverityToBrushConverter.High : HealthStatusToBrushConverter.Good;
    public string ToolTip => NetworkCount == 0
        ? $"Nothing else uses channel {Channel}."
        : $"{CountText} use or overlap channel {Channel}; the strongest is at {StrongestSignal} %.";
}

/// <summary>One packet-loss target row.</summary>
public sealed class PacketLossItem
{
    public PacketLossItem(PacketLossResult result)
    {
        Result = result;
        Status = PacketLossMath.Grade(result);
    }

    public PacketLossResult Result { get; }
    public HealthStatus Status { get; }
    public string Label => Result.Label;
    public string Target => Result.Target == "gateway" ? "default gateway" : Result.Target;
    public string SentText => Result.Sent.ToString("N0");
    public string ReceivedText => Result.Received.ToString("N0");
    public string LossText => $"{Result.LossPercent:0.#} %";
    public string AvgText => NetworkUi.Ms(Result.AvgMs);
    public string JitterText => NetworkUi.Ms(Result.JitterMs);
    public string P95Text => NetworkUi.Ms(Result.P95Ms);
    public string Verdict => PacketLossMath.Describe(Result);
    public Brush StatusBrush => NetworkUi.Status(Status);
}

/// <summary>One adapter in the adapters table.</summary>
public sealed class AdapterRow
{
    public AdapterRow(NetworkAdapterStatus adapter) => Adapter = adapter;

    public NetworkAdapterStatus Adapter { get; }
    public string Name => Adapter.Name;
    public string Description => Adapter.Description;
    public string Kind => Adapter.Kind;
    public string Status => Adapter.IsUp ? "Connected" : Adapter.Status == "Down" ? "Not connected" : Adapter.Status;
    public Brush StatusBrush => Adapter.IsUp ? HealthStatusToBrushConverter.Good : HealthStatusToBrushConverter.Unknown;
    public string SpeedText => NetworkUi.LinkSpeed(Adapter.LinkSpeedBps);
    public string Ipv4 => Adapter.Ipv4 ?? "—";
    public string DataText => Adapter.BytesReceived + Adapter.BytesSent == 0 ? "—" : $"↓ {ByteSize.Format(Adapter.BytesReceived)} · ↑ {ByteSize.Format(Adapter.BytesSent)}";
    public string ErrorsText => $"{Count(Adapter.ErrorsIn)} / {Count(Adapter.ErrorsOut)}";
    public string DiscardsText => $"{Count(Adapter.DiscardsIn)} / {Count(Adapter.DiscardsOut)}";
    public bool HasErrors => (Adapter.ErrorsIn ?? 0) + (Adapter.ErrorsOut ?? 0) > 0;
    public Brush ErrorsBrush => HasErrors ? HealthStatusToBrushConverter.Warning : HealthStatusToBrushConverter.Unknown;

    private static string Count(long? value) => value is null ? "—" : value.Value.ToString("N0");
}

/// <summary>One reversible tuning action with its live state.</summary>
public sealed partial class TuningActionRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(StatusText), nameof(StatusBrush), nameof(CanApply), nameof(CanRevert))]
    private ActionState? _state;

    [ObservableProperty]
    private DnsProvider? _selectedProvider;

    public TuningActionRow(TuningAction action, IReadOnlyList<DnsProvider> providers)
    {
        Action = action;
        Providers = action.Id == TuningActionIds.DnsFast ? providers : [];
        _selectedProvider = Providers.Count > 0 ? Providers[0] : null;
    }

    public TuningAction Action { get; }
    public IReadOnlyList<DnsProvider> Providers { get; }
    public bool HasProviders => Providers.Count > 0;
    public string Title => Action.Title;
    public string Description => Action.Description;
    public string RiskText => NetworkUi.RiskText(Action.Risk);
    public Brush RiskBrush => NetworkUi.Risk(Action.Risk);
    public bool RequiresRestart => Action.RequiresRestart;
    public bool RequiresReconnect => Action.RequiresReconnect;
    public bool AdvancedOnly => Action.AdvancedOnly;
    public string StateText => State?.CurrentValue ?? "Reading…";
    public string StatusText => State?.IsApplied switch { true => "Applied", false => "Not applied", _ => "Unknown" };
    public Brush StatusBrush => State?.IsApplied switch { true => HealthStatusToBrushConverter.Good, false => HealthStatusToBrushConverter.Warning, _ => HealthStatusToBrushConverter.Unknown };

    /// <summary>The DNS action can be applied again to switch provider.</summary>
    public bool CanApply => State?.IsApplied != true || HasProviders;
    public bool CanRevert => State?.IsApplied == true;
    public ControlAppearance ApplyAppearance => Action.Risk == RiskLevel.Advanced ? ControlAppearance.Danger : ControlAppearance.Primary;
    public string ApplyActionId => HasProviders && SelectedProvider is not null ? NetworkTuningCatalog.DnsActionId(SelectedProvider.Id) : Action.Id;
}

/// <summary>One adapter driver property with the value picker.</summary>
public sealed partial class AdapterSettingRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplySelected))]
    private string? _selectedValue;

    public AdapterSettingRow(AdapterSetting setting, bool canRevert, bool isKnown)
    {
        Setting = setting;
        CanRevert = canRevert;
        IsKnown = isKnown;
        _selectedValue = setting.CurrentValue;
    }

    public AdapterSetting Setting { get; }
    public bool CanRevert { get; }
    public bool IsKnown { get; }
    public string AdapterName => Setting.AdapterName;
    public string DisplayName => Setting.DisplayName;
    public string Keyword => Setting.Key;
    public string Explanation => Setting.Explanation;
    public IReadOnlyList<ValueOption> Options => Setting.AllowedValues.Select(a => new ValueOption(a.Value, a.Label)).ToList();
    public string CurrentLabel => Setting.CurrentLabel;
    public string RecommendedText => Setting.RecommendedLabel is { } r ? $"Recommended: {r}" : "No recommendation (leave as is)";
    public bool HasRecommendation => Setting.RecommendedValue is not null;
    public bool MatchesRecommendation => HasRecommendation && !Setting.DiffersFromRecommendation;
    public bool CanUseRecommended => Setting.DiffersFromRecommendation && Setting.IsEditable && AdapterSettingAdvisor.IsAllowed(Setting, Setting.RecommendedValue);
    public bool CanApplySelected => Setting.IsEditable && IsKnown && SelectedValue is not null && !string.Equals(SelectedValue, Setting.CurrentValue, StringComparison.OrdinalIgnoreCase);
    public bool IsEditable => Setting.IsEditable && IsKnown;
    public string RiskText => NetworkUi.RiskText(Setting.Risk);
    public Brush RiskBrush => NetworkUi.Risk(Setting.Risk);
}

/// <summary>A registry value and the label the driver shows for it.</summary>
public sealed record ValueOption(string Value, string Label);

/// <summary>The settings of one adapter.</summary>
public sealed record AdapterSettingGroup(string AdapterName, IReadOnlyList<AdapterSettingRow> Settings, int HiddenCount)
{
    public bool HasHidden => HiddenCount > 0;
    public string HiddenText => $"{HiddenCount} driver-specific setting{(HiddenCount == 1 ? string.Empty : "s")} without a recommendation are shown in Advanced mode.";
}

/// <summary>A saved Wi-Fi network.</summary>
public sealed class SavedNetworkRow
{
    public SavedNetworkRow(WifiProfileInfo profile, int count, bool isCurrent)
    {
        Profile = profile;
        Count = count;
        IsCurrent = isCurrent;
    }

    public WifiProfileInfo Profile { get; }
    public int Count { get; }
    public bool IsCurrent { get; }
    public string Name => Profile.Name;
    public string PriorityText => $"#{Profile.Priority}";
    public string Security => string.IsNullOrWhiteSpace(Profile.Authentication) ? "—" : Profile.Authentication;
    public string AutoConnectText => Profile.AutoConnect ? "Connects automatically" : "Only when you choose it";
    public string MeteredText => Profile.Metered ? "Metered" : "Not metered";
    public bool AutoConnect => Profile.AutoConnect;
    public bool Metered => Profile.Metered;
    public bool CanMoveUp => Profile.Priority > 1;
    public bool CanMoveDown => Profile.Priority < Count;
}

/// <summary>A checklist line in "Quick improve Wi-Fi".</summary>
public sealed partial class QuickImproveRow : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string? _outcome;

    public QuickImproveRow(QuickImproveItem item)
    {
        Item = item;
        _isSelected = item.Selected;
    }

    public QuickImproveItem Item { get; }
    public string Title => Item.Title;
    public string ChangeText => $"{Item.CurrentText} → {Item.TargetText}";
    public string Reason => Item.Reason;
    public string RiskText => NetworkUi.RiskText(Item.Risk);
    public Brush RiskBrush => NetworkUi.Risk(Item.Risk);
    public bool MayDisconnect => Item.MayDisconnect;
    public bool RequiresRestart => Item.RequiresRestart;
}

/// <summary>A before/after line after Quick improve.</summary>
public sealed record ComparisonRow(string Metric, string Before, string After, bool? Better)
{
    public string Verdict => Better switch { true => "Better", false => "Worse", _ => "Same" };
    public Brush VerdictBrush => Better switch { true => HealthStatusToBrushConverter.Good, false => HealthStatusToBrushConverter.Critical, _ => HealthStatusToBrushConverter.Unknown };
}
