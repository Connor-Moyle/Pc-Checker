namespace PcChecker.Core.Debloat;

/// <summary>
/// Where per-user (HKCU) debloat values are read and written. <see cref="Sid"/> null means the HKCU of the account
/// PC Checker runs as; otherwise the signed-in user's hive under HKEY_USERS\&lt;SID&gt;.
/// </summary>
public sealed record UserHiveTarget(string? Sid, string? AccountName, string? Note)
{
    /// <summary>HKCU of the account PC Checker runs as (the signed-in user, or the documented fallback).</summary>
    public static UserHiveTarget Own(string? note = null) => new(null, null, note);

    /// <summary>True when HKCU operations go to another account's hive (the signed-in user's).</summary>
    public bool IsRedirected => Sid is not null;
}

/// <summary>
/// Pure decisions for targeting the signed-in user's registry hive when PC Checker runs elevated as a different
/// (administrator) account: which SID to use, when to fall back to the app's own HKCU, and how HKCU paths map onto
/// HKEY_USERS (HKCU\Software\Classes lives in the separate &lt;SID&gt;_Classes hive).
/// </summary>
public static class UserHiveResolver
{
    public const string ClassesPath = @"Software\Classes";
    public const string ClassesHiveSuffix = "_Classes";

    /// <summary>
    /// The account that owns the session's shell: the SID most explorer.exe processes in the session belong to (there
    /// is normally one). Null when none is known.
    /// </summary>
    public static string? PickSessionOwner(IEnumerable<string?> explorerOwnerSids)
    {
        ArgumentNullException.ThrowIfNull(explorerOwnerSids);
        return explorerOwnerSids
            .Where(IsUserSid)
            .GroupBy(s => s!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// True for SIDs of real accounts that have a profile hive: local and domain accounts (S-1-5-21-…) and Microsoft
    /// Entra ID accounts (S-1-12-1-…). Built-in service accounts (SYSTEM S-1-5-18, LOCAL SERVICE, NETWORK SERVICE) and
    /// anything malformed are rejected.
    /// </summary>
    public static bool IsUserSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return false;
        }

        var s = sid.Trim();
        if (!(s.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || s.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Only digits and dashes after the prefix, and never a hive suffix such as "_Classes".
        return s[2..].All(c => char.IsAsciiDigit(c) || c == '-') && !s.EndsWith('-');
    }

    /// <summary>
    /// Chooses the hive for HKCU operations. The signed-in user's hive is used when it is a different account from the
    /// one PC Checker runs as and its hive is loaded (it is while that user is signed in); otherwise the app's own HKCU
    /// is used, with <paramref name="fallbackNote"/> explaining whose settings are changed when the accounts differ.
    /// </summary>
    public static UserHiveTarget Choose(string? processSid, string? interactiveSid, string? interactiveName, Func<string, bool> isHiveLoaded, string? fallbackNote)
    {
        ArgumentNullException.ThrowIfNull(isHiveLoaded);
        if (!IsUserSid(interactiveSid) || string.IsNullOrWhiteSpace(processSid))
        {
            return UserHiveTarget.Own(fallbackNote);
        }

        var sid = interactiveSid!.Trim();
        if (sid.Equals(processSid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return UserHiveTarget.Own();
        }

        bool loaded;
        try
        {
            loaded = isHiveLoaded(sid);
        }
        catch (Exception)
        {
            loaded = false;
        }

        if (!loaded)
        {
            return UserHiveTarget.Own(fallbackNote);
        }

        var who = string.IsNullOrWhiteSpace(interactiveName) ? "the signed-in user" : interactiveName.Trim();
        return new UserHiveTarget(sid, interactiveName,
            $"PC Checker runs as a different administrator account, so per-user tweaks are applied to {who}, who is signed in to Windows. App removals for one account and the linked Optimize page actions still act on the administrator account.");
    }

    /// <summary>
    /// The HKEY_USERS subkey and path for an HKCU path of <paramref name="sid"/>. HKCU\Software\Classes is the
    /// separate &lt;SID&gt;_Classes hive, used directly when it is loaded.
    /// </summary>
    public static (string SubKey, string Path) MapPath(string sid, string path, bool classesHiveLoaded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        var trimmed = (path ?? string.Empty).Trim('\\');
        if (classesHiveLoaded)
        {
            if (trimmed.Equals(ClassesPath, StringComparison.OrdinalIgnoreCase))
            {
                return (sid + ClassesHiveSuffix, string.Empty);
            }

            if (trimmed.StartsWith(ClassesPath + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return (sid + ClassesHiveSuffix, trimmed[(ClassesPath.Length + 1)..]);
            }
        }

        return (sid, trimmed);
    }
}
