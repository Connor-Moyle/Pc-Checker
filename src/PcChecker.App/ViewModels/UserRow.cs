using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Users;

namespace PcChecker.App.ViewModels;

/// <summary>One row on the Users tab: a signed-in session with its totals in plain text.</summary>
public sealed class UserRow
{
    public UserRow(UserSession session)
    {
        Session = session;
    }

    public UserSession Session { get; }

    public string DisplayName => Session.DisplayName;

    public string StateText => SessionStateMapper.Label(Session.State);

    public string SessionIdText => Session.SessionId.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string ClientText => string.IsNullOrWhiteSpace(Session.ClientName) ? Loc.T("This PC") : Session.ClientName!;

    public string CpuText => ProcessPercent(Session.CpuPercent);

    public string MemoryText => Session.MemoryBytes > 0 ? ByteSize.Format(Session.MemoryBytes) : "—";

    public string ProcessCountText => Loc.F("{0} programs", Session.ProcessCount);

    public string LogonText => Session.LogonTime is { } t ? t.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "—";

    public bool IsCurrent => Session.IsCurrent;

    public string YouBadge => Session.IsCurrent ? Loc.T("You") : string.Empty;

    private static string ProcessPercent(double value) =>
        value <= 0 ? "0%" : value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "%";
}
