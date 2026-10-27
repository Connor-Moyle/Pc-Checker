using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcChecker.Core.Common;

/// <summary>
/// The app always runs elevated. On a standard account, Windows asks for an administrator's password and the app
/// then runs as that administrator, so HKCU, the user's Startup folder and per-user Store apps belong to the
/// administrator, not to the person signed in. This detects that case so per-user changes can say whose settings
/// they changed instead of silently acting on the wrong profile.
/// </summary>
public static class ElevatedUserScope
{
    private static readonly Lazy<(string? ProcessUser, string? SessionUser)> Users = new(Probe);

    /// <summary>A sentence for per-user results and lists, or null when the app runs as the signed-in user.</summary>
    public static string? Note => Describe(Users.Value.ProcessUser, Users.Value.SessionUser);

    /// <summary>True when both names are known and are different accounts.</summary>
    public static bool Differs(string? processUser, string? sessionUser) =>
        !string.IsNullOrWhiteSpace(processUser) && !string.IsNullOrWhiteSpace(sessionUser)
        && !Normalize(processUser).Equals(Normalize(sessionUser), StringComparison.OrdinalIgnoreCase);

    /// <summary>Explains whose per-user settings are affected, or null when the app runs as the signed-in user.</summary>
    public static string? Describe(string? processUser, string? sessionUser) =>
        Differs(processUser, sessionUser)
            ? $"PC Checker is running as the administrator account {processUser}, not as {sessionUser} who is signed in, so per-user settings (HKCU, the user Startup folder, Store apps) shown and changed here belong to {processUser}."
            : null;

    /// <summary>Appends <see cref="Note"/> to a successful per-user result.</summary>
    public static OperationResult Annotate(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Success && Note is { } note ? result with { Message = result.Message + " " + note } : result;
    }

    private static string Normalize(string user)
    {
        var trimmed = user.Trim();
        int slash = trimmed.LastIndexOf('\\');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static (string?, string?) Probe()
    {
        if (!PlatformGuard.IsWindows)
        {
            return (null, null);
        }

        try
        {
            string process = Environment.UserDomainName + "\\" + Environment.UserName;
            int session = Process.GetCurrentProcess().SessionId;
            string? user = QuerySession(session, WtsUserName);
            if (string.IsNullOrWhiteSpace(user))
            {
                return (process, null);
            }

            string? domain = QuerySession(session, WtsDomainName);
            return (process, string.IsNullOrWhiteSpace(domain) ? user : domain + "\\" + user);
        }
        catch
        {
            return (null, null);
        }
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    private static string? QuerySession(int sessionId, int infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buffer, out _) || buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    private static class NativeMethods
    {
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr memory);
    }
}
