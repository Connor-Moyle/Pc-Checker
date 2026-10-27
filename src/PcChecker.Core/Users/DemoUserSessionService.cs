using PcChecker.Core.Common;

namespace PcChecker.Core.Users;

/// <summary>Two signed-in users for demo mode and screenshots: the active desktop user and a disconnected second user.</summary>
public sealed class DemoUserSessionService : IUserSessionService
{
    private readonly object _gate = new();
    private readonly List<UserSession> _sessions;

    public DemoUserSessionService()
    {
        var now = DateTimeOffset.Now;
        _sessions =
        [
            new UserSession(1, "Alex", Environment.MachineName, SessionState.Active, null, now.AddHours(-6).AddMinutes(-12), IsCurrent: true)
            {
                CpuPercent = 18.4,
                MemoryBytes = (long)(9.2 * ByteSize.Gigabyte),
                ProcessCount = 142,
            },
            new UserSession(2, "Sam", Environment.MachineName, SessionState.Disconnected, null, now.AddHours(-2).AddMinutes(-5), IsCurrent: false)
            {
                CpuPercent = 1.1,
                MemoryBytes = (long)(1.8 * ByteSize.Gigabyte),
                ProcessCount = 37,
            },
        ];
    }

    public async Task<IReadOnlyList<UserSession>> GetSessionsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 80, 220).ConfigureAwait(false);
        lock (_gate)
        {
            return _sessions.ToList();
        }
    }

    public OperationResult SwitchUser() => OperationResult.Ok("Locked the PC and opened the sign-in screen (demo).");

    public Task<OperationResult> DisconnectAsync(UserSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            Replace(session.SessionId, s => s with { State = SessionState.Disconnected });
        }

        return Task.FromResult(OperationResult.Ok($"{session.DisplayName} was disconnected (demo)."));
    }

    public Task<OperationResult> SignOffAsync(UserSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsCurrent)
        {
            return Task.FromResult(OperationResult.Fail("Use Switch user or the Start menu to sign yourself out."));
        }

        lock (_gate)
        {
            _sessions.RemoveAll(s => s.SessionId == session.SessionId);
        }

        return Task.FromResult(OperationResult.Ok($"{session.DisplayName} was signed out (demo)."));
    }

    public Task<OperationResult> SendMessageAsync(UserSession session, string title, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.FromResult(OperationResult.Ok($"Message sent to {session.DisplayName} (demo)."));
    }

    private void Replace(int sessionId, Func<UserSession, UserSession> update)
    {
        int i = _sessions.FindIndex(s => s.SessionId == sessionId);
        if (i >= 0)
        {
            _sessions[i] = update(_sessions[i]);
        }
    }
}
