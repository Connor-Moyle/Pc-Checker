using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Settings;
using PcChecker.Core.Users;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Users page: who is signed in to this PC, with each person's summed processor and memory use, and the actions a
/// Task Manager user expects — switch user, disconnect, sign off another user, and send them a message. Actions on
/// other people's sessions are confirmed first.
/// </summary>
public sealed partial class UsersViewModel : ViewModelBase
{
    private readonly IUserSessionService _users;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<UsersViewModel> _logger;

    public UsersViewModel(IUserSessionService users, ISettingsService settings, IDialogService dialogs,
        INotificationService notifications, ILogger<UsersViewModel> logger)
        : base(settings)
    {
        _users = users;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
    }

    public ObservableCollection<UserRow> Users { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    [NotifyPropertyChangedFor(nameof(CanSignOff))]
    [NotifyPropertyChangedFor(nameof(CanSendMessage))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOffCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private UserRow? _selectedUser;

    [ObservableProperty]
    private string? _messageText;

    [ObservableProperty]
    private string _countText = "—";

    [ObservableProperty]
    private string _statusText = Loc.T("Reading signed-in users…");

    [ObservableProperty]
    private bool _hasNoRows;

    public bool HasSelection => SelectedUser is not null;

    public bool CanDisconnect => SelectedUser is not null && SessionStateMapper.CanDisconnect(SelectedUser.Session);

    public bool CanSignOff => SelectedUser is not null && !SelectedUser.Session.IsCurrent && SessionStateMapper.CanSignOff(SelectedUser.Session);

    public bool CanSendMessage => SelectedUser is not null && SessionStateMapper.CanSendMessage(SelectedUser.Session);

    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync();

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync();

    /// <summary>The header's Refresh button and F5.</summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync(CancellationToken.None);

    private Task LoadAsync() =>
        RunBusyAsync(async ct =>
        {
            var sessions = await _users.GetSessionsAsync(ct);

            // Clearing the list makes the grid's two-way binding null the selection, so remember it first.
            int? selectedSession = SelectedUser?.Session.SessionId;
            Users.Clear();
            foreach (var session in sessions)
            {
                Users.Add(new UserRow(session));
            }

            HasNoRows = Users.Count == 0;
            CountText = sessions.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            StatusText = sessions.Count == 1
                ? Loc.T("1 person is signed in")
                : Loc.F("{0} people are signed in", sessions.Count);
            SelectedUser = selectedSession is { } id ? Users.FirstOrDefault(u => u.Session.SessionId == id) : null;
        }, Loc.T("Reading signed-in users…"));

    [RelayCommand]
    private void SwitchUser()
    {
        var result = _users.SwitchUser();
        if (!result.Success)
        {
            _notifications.Error(Loc.T("Could not switch user"), result.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task Disconnect()
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Disconnect user?"),
            Loc.F("Disconnect {0}? Their programs keep running; their desktop is locked.", user.DisplayName), Loc.T("Disconnect")))
        {
            return;
        }

        await RunAndReloadAsync(() => _users.DisconnectAsync(user.Session, CancellationToken.None));
    }

    [RelayCommand(CanExecute = nameof(CanSignOff))]
    private async Task SignOff()
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Sign out user?"),
            Loc.F("Sign {0} out? Their unsaved work will be lost.", user.DisplayName), Loc.T("Sign out"), isDanger: true))
        {
            return;
        }

        await RunAndReloadAsync(() => _users.SignOffAsync(user.Session, CancellationToken.None));
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessage()
    {
        if (SelectedUser is not { } user)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(MessageText))
        {
            _notifications.Warning(Loc.T("No message"), Loc.T("Type a message to send."));
            return;
        }

        var result = await _users.SendMessageAsync(user.Session, Loc.T("Message from this PC"), MessageText!, CancellationToken.None);
        if (result.Success)
        {
            _notifications.Success(Loc.T("Sent"), result.Message);
            MessageText = null;
        }
        else
        {
            _notifications.Error(Loc.T("Could not send"), result.Message);
        }
    }

    private async Task RunAndReloadAsync(Func<Task<Core.Common.OperationResult>> action)
    {
        await RunBusyAsync(async _ =>
        {
            var result = await action();
            if (result.Success)
            {
                _notifications.Success(Loc.T("Done"), result.Message);
            }
            else
            {
                _notifications.Error(Loc.T("Could not complete"), result.Message);
            }
        }, Loc.T("Working…"));

        await LoadAsync();
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Users page operation failed.");
}
