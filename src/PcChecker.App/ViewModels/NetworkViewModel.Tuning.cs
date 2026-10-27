using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Optimization;

namespace PcChecker.App.ViewModels;

/// <summary>"Tune Wi-Fi and network": reversible tweaks, adapter driver properties and saved networks.</summary>
public sealed partial class NetworkViewModel
{
    private readonly List<TuningActionRow> _allActionRows = [];
    private IReadOnlyList<AdapterSetting> _adapterSettings = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenAdvancedActions))]
    private int _hiddenAdvancedActions;

    public ObservableCollection<TuningActionRow> TuningActions { get; } = [];
    public ObservableCollection<AdapterSettingGroup> AdapterSettingGroups { get; } = [];
    public ObservableCollection<SavedNetworkRow> SavedNetworks { get; } = [];

    public bool HasHiddenAdvancedActions => HiddenAdvancedActions > 0;
    public bool HasAdapterSettings => AdapterSettingGroups.Count > 0;
    public bool HasSavedNetworks => SavedNetworks.Count > 0;

    [RelayCommand]
    private Task ReloadTuningAsync() => RunBusyAsync(LoadTuningCoreAsync, "Reading tuning settings…");

    private async Task LoadTuningCoreAsync(CancellationToken ct)
    {
        _allActionRows.Clear();
        foreach (var action in _tuning.Actions)
        {
            var row = new TuningActionRow(action, NetworkTuningCatalog.DnsProviders);
            _allActionRows.Add(row);
        }

        // Each state read may start a PowerShell or netsh process; read them side by side.
        var states = await Task.WhenAll(_allActionRows.Select(r => _tuning.GetStateAsync(r.Action.Id, ct)));
        for (int i = 0; i < states.Length; i++)
        {
            _allActionRows[i].State = states[i];
        }

        _adapterSettings = await _tuning.GetAdapterSettingsAsync(null, ct);
        RebuildTuningRows();
        await LoadSavedNetworksAsync(ct);
    }

    private void RebuildTuningRows()
    {
        TuningActions.Clear();
        foreach (var row in _allActionRows.Where(r => IsAdvancedMode || !r.AdvancedOnly))
        {
            TuningActions.Add(row);
        }

        HiddenAdvancedActions = IsAdvancedMode ? 0 : _allActionRows.Count(r => r.AdvancedOnly);

        AdapterSettingGroups.Clear();
        foreach (var group in _adapterSettings.GroupBy(s => s.AdapterName))
        {
            var rows = new List<AdapterSettingRow>();
            int hidden = 0;
            foreach (var setting in group)
            {
                bool known = AdapterSettingAdvisor.IsKnown(setting.Key, setting.DisplayName);
                if (!known && !IsAdvancedMode)
                {
                    hidden++;
                    continue;
                }

                rows.Add(new AdapterSettingRow(setting, _tuning.CanRevertAdapterSetting(setting.AdapterName, setting.Key), known));
            }

            // Settings with a recommendation that differs come first, then the rest in driver order.
            rows = rows.OrderByDescending(r => r.CanUseRecommended).ThenByDescending(r => r.IsKnown).ToList();
            AdapterSettingGroups.Add(new AdapterSettingGroup(group.Key, rows, hidden));
        }

        OnPropertyChanged(nameof(HasAdapterSettings));
    }

    private async Task LoadSavedNetworksAsync(CancellationToken ct)
    {
        var profiles = await _tuning.GetSavedNetworksAsync(ct);
        string? connected = WifiReport?.Current?.Ssid;
        SavedNetworks.Clear();
        foreach (var profile in profiles.OrderBy(p => p.Interface).ThenBy(p => p.Priority))
        {
            int count = profiles.Count(p => p.Interface == profile.Interface);
            SavedNetworks.Add(new SavedNetworkRow(profile, count, connected is not null && profile.Name == connected));
        }

        OnPropertyChanged(nameof(HasSavedNetworks));
    }

    // ---------------------------------------------------------------- tuning actions

    [RelayCommand]
    private async Task ApplyTuningAsync(TuningActionRow? row)
    {
        if (row is null)
        {
            return;
        }

        string what = row.HasProviders && row.SelectedProvider is { } p
            ? $"{row.Description}\n\nProvider: {p.Name} ({p.Primary}, {p.Secondary}). {p.Note}"
            : row.Description;
        if (!await ConfirmTuningAsync(row.Title, what, row.Action.Risk, row.RequiresReconnect, row.RequiresRestart, "Apply"))
        {
            return;
        }

        await RunTuningAsync(row, ct => _tuning.ApplyAsync(row.ApplyActionId, ct), "Applying: " + row.Title + "…", "Change applied");
    }

    [RelayCommand]
    private async Task RevertTuningAsync(TuningActionRow? row)
    {
        if (row is null)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync("Revert: " + row.Title,
            "PC Checker puts back the value it recorded before it made this change (or the Windows default when nothing was recorded)."
            + (row.RequiresRestart ? "\n\nA restart is needed before this takes effect." : string.Empty),
            "Revert");
        if (ok)
        {
            await RunTuningAsync(row, ct => _tuning.RevertAsync(row.Action.Id, ct), "Reverting: " + row.Title + "…", "Change reverted");
        }
    }

    [RelayCommand]
    private async Task FixFindingAsync(NetworkFindingItem? item)
    {
        if (item?.Action is not { } action || !item.CanFix)
        {
            return;
        }

        var row = _allActionRows.FirstOrDefault(r => r.Action.Id == action.Id) ?? new TuningActionRow(action, NetworkTuningCatalog.DnsProviders);
        if (!await ConfirmTuningAsync(action.Title, action.Description, action.Risk, action.RequiresReconnect, action.RequiresRestart, "Apply fix"))
        {
            return;
        }

        var result = await RunTuningAsync(row, ct => _tuning.ApplyAsync(action.Id, ct), "Applying: " + action.Title + "…", "Fix applied");
        if (result?.Success == true)
        {
            item.IsFixed = true;
            item.ResultMessage = result.Message;
        }
    }

    private async Task<OperationResult?> RunTuningAsync(TuningActionRow row, Func<CancellationToken, Task<OperationResult>> change, string busyText, string successTitle)
    {
        var result = await RunChangeAsync(change, busyText, successTitle);
        try
        {
            row.State = await _tuning.GetStateAsync(row.Action.Id, CancellationToken.None);
            if (row.Action.Id == TuningActionIds.DnsFast)
            {
                // DNS-over-HTTPS depends on the DNS provider; refresh it too.
                var doh = _allActionRows.FirstOrDefault(r => r.Action.Id == TuningActionIds.DnsOverHttps);
                if (doh is not null)
                {
                    doh.State = await _tuning.GetStateAsync(doh.Action.Id, CancellationToken.None);
                }

                await RefreshAdaptersQuietlyAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh the tuning state.");
        }

        return result;
    }

    private Task<bool> ConfirmTuningAsync(string title, string description, RiskLevel risk, bool reconnect, bool restart, string confirmText)
    {
        var message = description
            + (reconnect ? "\n\nThe network connection may pause for a few seconds." : string.Empty)
            + (restart ? "\n\nA restart is needed before this takes effect." : string.Empty)
            + "\n\nYou can undo it with Revert on this page."
            + (risk == RiskLevel.Safe ? string.Empty : " A restore point is created first when that is enabled in Settings.")
            + (risk == RiskLevel.Advanced ? "\n\nThis is an advanced change. Only apply it if you know why you want it." : string.Empty);
        return _dialogs.ConfirmAsync(title, message, confirmText, isDanger: risk == RiskLevel.Advanced);
    }

    // ---------------------------------------------------------------- adapter settings

    [RelayCommand]
    private Task ApplyAdapterSettingAsync(AdapterSettingRow? row) =>
        row?.SelectedValue is { } value ? SetAdapterValueAsync(row, value) : Task.CompletedTask;

    [RelayCommand]
    private Task UseRecommendedAsync(AdapterSettingRow? row) =>
        row?.Setting.RecommendedValue is { } value && row.CanUseRecommended ? SetAdapterValueAsync(row, value) : Task.CompletedTask;

    [RelayCommand]
    private async Task RevertAdapterSettingAsync(AdapterSettingRow? row)
    {
        if (row is null || !row.CanRevert)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync($"Revert {row.DisplayName}?",
            $"PC Checker puts back the value {row.AdapterName} had before PC Checker first changed it. The adapter restarts, so the connection drops for a few seconds.",
            "Revert");
        if (ok)
        {
            await RunChangeAsync(ct => _tuning.RevertAdapterSettingAsync(row.AdapterName, row.Keyword, ct), $"Reverting {row.DisplayName}…", "Setting reverted");
            await ReloadAdapterSettingsAsync();
        }
    }

    private async Task SetAdapterValueAsync(AdapterSettingRow row, string value)
    {
        string label = row.Setting.LabelFor(value);
        bool ok = await _dialogs.ConfirmAsync($"Change {row.DisplayName}?",
            $"{row.Explanation}\n\n{row.AdapterName}: {row.CurrentLabel} → {label}\n\n"
            + "The adapter restarts to apply it, so the connection drops for a few seconds. You can put the old value back with Revert.",
            "Change setting", isDanger: row.Setting.Risk == RiskLevel.Advanced);
        if (ok)
        {
            await RunChangeAsync(ct => _tuning.SetAdapterSettingAsync(row.AdapterName, row.Keyword, value, ct), $"Changing {row.DisplayName} (the adapter restarts)…", "Setting changed");
            await ReloadAdapterSettingsAsync();
        }
        else
        {
            row.SelectedValue = row.Setting.CurrentValue;
        }
    }

    private async Task ReloadAdapterSettingsAsync()
    {
        try
        {
            _adapterSettings = await _tuning.GetAdapterSettingsAsync(null, CancellationToken.None);
            RebuildTuningRows();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reload the adapter settings.");
        }
    }

    // ---------------------------------------------------------------- saved networks

    [RelayCommand]
    private Task MoveNetworkUpAsync(SavedNetworkRow? row) =>
        row is { CanMoveUp: true } ? MoveNetworkAsync(row, row.Profile.Priority - 1) : Task.CompletedTask;

    [RelayCommand]
    private Task MoveNetworkDownAsync(SavedNetworkRow? row) =>
        row is { CanMoveDown: true } ? MoveNetworkAsync(row, row.Profile.Priority + 1) : Task.CompletedTask;

    private Task MoveNetworkAsync(SavedNetworkRow row, int priority) =>
        ConfirmProfileChangeAsync($"Move \"{row.Name}\" to position {priority}?",
            "When several saved networks are in range, Windows tries them from the top of this list down.",
            "Move", ct => _tuning.SetNetworkPriorityAsync(row.Name, priority, ct), $"Moving \"{row.Name}\"…", "Order changed");

    [RelayCommand]
    private Task ToggleAutoConnectAsync(SavedNetworkRow? row) =>
        row is null
            ? Task.CompletedTask
            : ConfirmProfileChangeAsync(row.AutoConnect ? $"Stop connecting to \"{row.Name}\" automatically?" : $"Connect to \"{row.Name}\" automatically?",
                row.AutoConnect
                    ? "Windows will only join this network when you pick it yourself. Useful for hotspots and networks you rarely want."
                    : "Windows will join this network by itself whenever it is in range.",
                row.AutoConnect ? "Turn off" : "Turn on",
                ct => _tuning.SetAutoConnectAsync(row.Name, !row.AutoConnect, ct), "Updating the network…", "Network updated");

    [RelayCommand]
    private Task ToggleMeteredAsync(SavedNetworkRow? row) =>
        row is null
            ? Task.CompletedTask
            : ConfirmProfileChangeAsync(row.Metered ? $"Treat \"{row.Name}\" as unlimited?" : $"Mark \"{row.Name}\" as metered?",
                row.Metered
                    ? "Windows Update, OneDrive and Store apps will download normally on this network again."
                    : "On a metered connection Windows Update, OneDrive and many apps hold back large downloads. Use it for phone hotspots and capped plans.",
                row.Metered ? "Not metered" : "Metered",
                ct => _tuning.SetMeteredAsync(row.Name, !row.Metered, ct), "Updating the network…", "Network updated");

    private async Task ConfirmProfileChangeAsync(string title, string message, string confirmText, Func<CancellationToken, Task<OperationResult>> change, string busyText, string successTitle)
    {
        if (await _dialogs.ConfirmAsync(title, message, confirmText))
        {
            await ChangeProfileAsync(change, busyText, successTitle);
        }
        else
        {
            // Put toggles that the click already flipped back to the real state.
            await ReloadSavedNetworksQuietlyAsync();
        }
    }

    [RelayCommand]
    private async Task ForgetNetworkAsync(SavedNetworkRow? row)
    {
        if (row is null)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync($"Forget \"{row.Name}\"?",
            "Windows deletes this saved network and its password and will not connect to it automatically any more."
            + (row.IsCurrent ? "\n\nYou are connected to this network right now: you will be disconnected." : string.Empty)
            + "\n\nPC Checker saves a backup first (the password stays encrypted and only works on this PC); the Logs page shows how to add it back.",
            "Forget network", isDanger: true);
        if (ok)
        {
            await ChangeProfileAsync(ct => _tuning.ForgetNetworkAsync(row.Name, ct), $"Forgetting \"{row.Name}\"…", "Network forgotten");
        }
    }

    private async Task ChangeProfileAsync(Func<CancellationToken, Task<OperationResult>> change, string busyText, string successTitle)
    {
        await RunChangeAsync(change, busyText, successTitle);
        await ReloadSavedNetworksQuietlyAsync();
    }

    private async Task ReloadSavedNetworksQuietlyAsync()
    {
        try
        {
            await LoadSavedNetworksAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reload the saved networks.");
        }
    }
}
