using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Network.Resolvers;

namespace PcChecker.App.ViewModels;

/// <summary>Switching, resetting and undoing DNS changes, and the other actions of the DNS page.</summary>
public sealed partial class DnsViewModel
{
    /// <summary>The switch the "Switch DNS" card describes right now, or null when nothing is chosen.</summary>
    private DnsChangeRequest? CurrentRequest(bool includeIpv6)
    {
        var adapters = Adapters.Where(a => a.IsSelected).Select(a => a.Info.Id).ToList();
        return SelectedChoice switch
        {
            null => null,
            { Provider: { } provider } => DnsSwitchPlan.ForProvider(provider, adapters, includeIpv6 && provider.Servers6.Count > 0, Encrypt && EncryptionSupported),
            _ => DnsSwitchPlan.ForCustom([CustomPrimary, CustomSecondary], adapters, Loc.T("your own servers"), Encrypt && EncryptionSupported),
        };
    }

    [RelayCommand]
    private async Task SwitchAsync()
    {
        if (CurrentRequest(SetIpv6) is not { } request)
        {
            return;
        }

        if (!CanEncryptChoice)
        {
            request = request with { EncryptWithDoh = false };
        }

        var problem = DnsSwitchPlan.Validate(request);
        if (problem != DnsSwitchProblem.None)
        {
            await _dialogs.ShowMessageAsync(Loc.T("Check the DNS change"), DnsText.Problem(problem));
            return;
        }

        var adapters = Adapters.Where(a => a.IsSelected).ToList();
        var message = new StringBuilder();
        message.Append(Loc.F("{0} will use {1}: {2}.", string.Join(", ", adapters.Select(a => a.Name)), request.Label, string.Join(", ", DnsSwitchPlan.Servers(request))));
        message.Append("\n\n").Append(Loc.T("Now:")).Append('\n').Append(string.Join("\n", adapters.Select(a => $"{a.Name}: {a.ModeText} ({a.ServersText})")));
        if (request.EncryptWithDoh)
        {
            message.Append("\n\n").Append(Loc.T("Lookups will be encrypted with DNS over HTTPS. Windows is asked to fall back to normal DNS if the encrypted service cannot be reached; if websites stop loading anyway, use Undo."));
        }

        message.Append("\n\n").Append(Loc.T("A restore point is created first (per your settings) and the DNS cache is cleared afterwards. Undo puts the current settings back exactly, including \"automatic\"."));
        if (!await _dialogs.ConfirmAsync(Loc.F("Switch DNS to {0}?", request.Label), message.ToString(), Loc.T("Switch")))
        {
            return;
        }

        await RunChangeAsync(() => _dns.SwitchAsync(request, CancellationToken.None), Loc.F("Switch DNS to {0}", request.Label), Loc.T("Changing the DNS servers…"));
    }

    [RelayCommand]
    private async Task ResetToAutomaticAsync()
    {
        var adapters = Adapters.Where(a => a.IsSelected).ToList();
        if (adapters.Count == 0)
        {
            await _dialogs.ShowMessageAsync(Loc.T("Check the DNS change"), DnsText.Problem(DnsSwitchProblem.NoAdapter));
            return;
        }

        string message = Loc.F("{0} will get their DNS servers from your router again, like a new PC. Servers typed in by hand (IPv4 and IPv6) are removed.", string.Join(", ", adapters.Select(a => a.Name)))
            + "\n\n" + Loc.T("Now:") + "\n" + string.Join("\n", adapters.Select(a => $"{a.Name}: {a.ModeText} ({a.ServersText})"))
            + "\n\n" + Loc.T("A restore point is created first (per your settings) and the DNS cache is cleared afterwards. Undo puts the current settings back exactly, including \"automatic\".");
        if (!await _dialogs.ConfirmAsync(Loc.T("Reset DNS to automatic?"), message, Loc.T("Reset")))
        {
            return;
        }

        var ids = adapters.Select(a => a.Info.Id).ToList();
        await RunChangeAsync(() => _dns.ResetToAutomaticAsync(ids, CancellationToken.None), Loc.T("Reset DNS to automatic"), Loc.T("Resetting the DNS servers…"));
    }

    /// <summary>Runs a change with the busy overlay and an Undo on its success notification, then reads the settings again.</summary>
    private Task RunChangeAsync(Func<Task<OperationResult>> change, string description, string busyText) => RunBusyAsync(async ct =>
    {
        var result = await UndoOffers.RunAsync(change, async undoCt =>
        {
            var undo = await _dns.UndoLastChangeAsync(undoCt);
            DispatcherHelper.PostToUi(() => _ = LoadStatusAsync(CancellationToken.None));
            return undo;
        }, description);
        ShowResult(result);
        if (result.Success)
        {
            _notifications.Success(Loc.T("DNS changed"), result.Message);
        }

        await LoadStatusCoreAsync(CancellationToken.None);
    }, busyText, cancellable: false);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoLastChangeAsync()
    {
        if (_dns.LastChange is not { } last)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Undo the last DNS change?"),
                Loc.T("These settings come back:") + "\n" + DnsText.Previous(last) + "\n\n" + Loc.T("The DNS cache is cleared afterwards. This only undoes changes made on this page; a DNS change made on the Network page has its own Undo there."), Loc.T("Undo")))
        {
            return;
        }

        await RunBusyAsync(async _ =>
        {
            var result = await _dns.UndoLastChangeAsync(CancellationToken.None);
            ShowResult(result);
            if (result.Success)
            {
                _notifications.Success(Loc.T("DNS change undone"), result.Message);
            }

            await LoadStatusCoreAsync(CancellationToken.None);
        }, Loc.T("Putting the previous DNS servers back…"), cancellable: false);
    }

    [RelayCommand(CanExecute = nameof(HasRecommendationAction))]
    private Task ApplyRecommendationAsync()
    {
        if (_advice is null)
        {
            return Task.CompletedTask;
        }

        if (_advice.Kind == DnsAdviceKind.UseAutomatic)
        {
            return ResetToAutomaticAsync();
        }

        return _advice.Best is { } best ? UseResultAsync(best) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedResult))]
    private Task UseSelectedAsync() => SelectedResult is { } row ? UseResultAsync(row.Result) : Task.CompletedTask;

    private bool HasSelectedResult() => SelectedResult is not null;

    /// <summary>Fills the "Switch DNS" card with a tested server and asks to switch to it (or back to automatic for the router).</summary>
    private Task UseResultAsync(DnsServerResult result)
    {
        var target = result.Target;
        if (target.IsRouter && target.ProviderId is null)
        {
            return ResetToAutomaticAsync();
        }

        var choice = Choices.FirstOrDefault(c => c.Id == target.ProviderId);
        if (choice is null)
        {
            choice = Choices.First(c => c.IsCustom);
            CustomPrimary = target.Address;
            CustomSecondary = null;
        }

        SelectedChoice = choice;
        return SwitchAsync();
    }

    [RelayCommand]
    private Task FlushDnsAsync() => RunBusyAsync(async ct =>
    {
        var result = await _network.FlushDnsAsync(ct);
        ShowResult(result);
    }, Loc.T("Clearing the DNS cache…"));

    [RelayCommand]
    private void CopyResults()
    {
        if (Results.Count == 0)
        {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine(string.Join("\t", Loc.T("Server"), Loc.T("Address"), Loc.T("Cached"), Loc.T("New sites"), Loc.T("Slowest 10 %"), Loc.T("Answered"), Loc.T("DNSSEC"), Loc.T("Blocks")));
        foreach (var row in Results)
        {
            text.AppendLine(row.CopyText());
        }

        try
        {
            Clipboard.SetText(text.ToString());
            _notifications.Info(Loc.T("Copied"), Loc.T("The DNS test results are on the clipboard."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy the DNS results.");
        }
    }

    private void ShowResult(OperationResult result)
    {
        ResultSuccess = result.Success;
        ResultMessage = result.Message;
    }
}
