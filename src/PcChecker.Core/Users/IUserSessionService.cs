using PcChecker.Core.Common;

namespace PcChecker.Core.Users;

/// <summary>
/// The people signed in to this PC and the few actions a Task Manager user expects on the Users tab. Actions that
/// affect another person's session are confirmed and journaled.
/// </summary>
public interface IUserSessionService
{
    /// <summary>Reads every sign-in session with each user's summed processor and memory use.</summary>
    Task<IReadOnlyList<UserSession>> GetSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Switches user by locking this PC and showing the sign-in screen, so someone else can sign in.</summary>
    OperationResult SwitchUser();

    /// <summary>Disconnects the session: the programs keep running and the desktop is left locked.</summary>
    Task<OperationResult> DisconnectAsync(UserSession session, CancellationToken cancellationToken);

    /// <summary>Signs the user out, closing their programs. Confirmed first; not allowed for your own session here.</summary>
    Task<OperationResult> SignOffAsync(UserSession session, CancellationToken cancellationToken);

    /// <summary>Shows a message box on the user's screen.</summary>
    Task<OperationResult> SendMessageAsync(UserSession session, string title, string message, CancellationToken cancellationToken);
}
