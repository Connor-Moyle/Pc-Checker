using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Users;

/// <summary>
/// The signed-in sessions, read with the Remote Desktop Services (WTS) APIs, and the actions on them: switch user
/// (locks the PC), disconnect, sign off another user and send a message. Per-user processor and memory totals are
/// summed from that session's processes. Changes to another session go through the change guard and are journaled.
/// </summary>
public sealed class WindowsUserSessionService : IUserSessionService
{
    private const string Module = "Users";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsUserSessionService> _logger;

    public WindowsUserSessionService(IChangeGuard guard, ILogger<WindowsUserSessionService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public Task<IReadOnlyList<UserSession>> GetSessionsAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<IReadOnlyList<UserSession>>([]);
        }

        return Task.Run<IReadOnlyList<UserSession>>(() =>
        {
            var usage = UserSessionNative.MeasureSessionUsage(cancellationToken);
            var sessions = new List<UserSession>();
            try
            {
                int currentSession = Process.GetCurrentProcess().SessionId;
                foreach (var raw in UserSessionNative.EnumerateSessions())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var state = SessionStateMapper.FromWts(raw.State);
                    usage.TryGetValue(raw.SessionId, out var u);
                    sessions.Add(new UserSession(raw.SessionId, raw.UserName, raw.Domain, state, raw.ClientName,
                        SessionStateMapper.ChooseLogonTime(raw.LogonTime, u.LogonTime), raw.SessionId == currentSession)
                    {
                        CpuPercent = Math.Round(u.CpuPercent, 1),
                        MemoryBytes = u.MemoryBytes,
                        ProcessCount = u.ProcessCount,
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The signed-in sessions could not be read.");
            }

            return sessions
                .Where(s => s.IsSignedIn || s.State == SessionState.Disconnected)
                .OrderByDescending(s => s.IsCurrent)
                .ThenBy(s => s.SessionId)
                .ToList();
        }, cancellationToken);
    }

    public OperationResult SwitchUser()
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Switching user is only possible on Windows.");
        }

        return UserSessionNative.LockWorkstation()
            ? OperationResult.Ok("The PC is locked. Pick another account on the sign-in screen.")
            : OperationResult.Fail("The sign-in screen could not be shown.");
    }

    public Task<OperationResult> DisconnectAsync(UserSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!SessionStateMapper.CanDisconnect(session))
        {
            return Task.FromResult(OperationResult.Fail($"{session.DisplayName}'s session cannot be disconnected right now."));
        }

        return GuardedAsync($"Disconnect {session.DisplayName}", session.SessionId,
            () => UserSessionNative.Disconnect(session.SessionId),
            $"{session.DisplayName} was disconnected; their programs keep running.", cancellationToken);
    }

    public Task<OperationResult> SignOffAsync(UserSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsCurrent)
        {
            return Task.FromResult(OperationResult.Fail("Use Switch user or the Start menu to sign yourself out, so you are not cut off mid-action."));
        }

        if (!SessionStateMapper.CanSignOff(session))
        {
            return Task.FromResult(OperationResult.Fail($"{session.DisplayName}'s session cannot be signed out right now."));
        }

        return GuardedAsync($"Sign out {session.DisplayName}", session.SessionId,
            () => UserSessionNative.Logoff(session.SessionId),
            $"{session.DisplayName} was signed out.", cancellationToken);
    }

    public Task<OperationResult> SendMessageAsync(UserSession session, string title, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Sending a message is only possible on Windows."));
        }

        if (!SessionStateMapper.CanSendMessage(session))
        {
            return Task.FromResult(OperationResult.Fail($"A message cannot reach {session.DisplayName} right now."));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return Task.FromResult(OperationResult.Fail("Type a message to send."));
        }

        return Task.Run(() => UserSessionNative.SendMessage(session.SessionId, title ?? "PC Checker", message)
            ? OperationResult.Ok($"Message sent to {session.DisplayName}.")
            : OperationResult.Fail($"The message could not be sent to {session.DisplayName}."), cancellationToken);
    }

    private Task<OperationResult> GuardedAsync(string description, int sessionId, Func<bool> action, string success, CancellationToken cancellationToken)
    {
        var options = new ChangeOptions(false, false, Module, "Session " + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Reversible: false, UndoHint: null);
        return _guard.RunGuardedAsync(description, options, ct =>
            Task.FromResult(action() ? OperationResult.Ok(success) : OperationResult.Fail("Windows refused the request. You may need administrator rights.")),
            null, cancellationToken);
    }
}
