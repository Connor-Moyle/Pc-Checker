using System.Globalization;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;
using PcChecker.Core.Common;

namespace PcChecker.Core.Debloat;

/// <summary>
/// <see cref="IRegistryAccessor"/> on the real registry, always through the 64-bit view so HKLM\SOFTWARE is not
/// redirected for this 64-bit app. Reads never throw (they return null); writes throw so the engine can report the
/// exact failure (for example "access denied" on settings Windows protects).
/// HKCU operations go to the hive chosen by <see cref="UserHiveTarget"/>: normally the app's own HKCU, but when PC
/// Checker runs elevated as a different administrator account, the signed-in user's hive under HKEY_USERS\&lt;SID&gt;
/// (and HKEY_USERS\&lt;SID&gt;_Classes for HKCU\Software\Classes), so the person at the PC gets the change.
/// </summary>
public sealed class WindowsRegistryAccessor : IRegistryAccessor
{
    private readonly Lazy<UserHiveTarget> _target;

    /// <summary>Uses the app's own HKCU.</summary>
    public WindowsRegistryAccessor()
        : this(() => UserHiveTarget.Own())
    {
    }

    /// <summary>Resolves the HKCU target once, on first use (the lookup queries WMI, so it never runs at startup).</summary>
    public WindowsRegistryAccessor(Func<UserHiveTarget> resolveTarget)
    {
        ArgumentNullException.ThrowIfNull(resolveTarget);
        _target = new Lazy<UserHiveTarget>(() =>
        {
            try
            {
                return resolveTarget();
            }
            catch (Exception)
            {
                return UserHiveTarget.Own(ElevatedUserScope.Note);
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Where HKCU operations go (resolved on first access).</summary>
    public UserHiveTarget Target => _target.Value;

    /// <summary>The target when it has been resolved already, otherwise null (never triggers the lookup).</summary>
    public UserHiveTarget? ResolvedTarget => _target.IsValueCreated ? _target.Value : null;

    public RegistryData? Read(DebloatHive hive, string path, string name)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            var (root, mapped) = OpenRoot(hive, path, writable: false);
            using var rootKey = root;
            using var key = root.OpenSubKey(mapped, writable: false);
            if (key is null)
            {
                return null;
            }

            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                return null;
            }

            return key.GetValueKind(name) switch
            {
                RegistryValueKind.DWord => new RegistryData(DebloatValueKind.DWord, unchecked((uint)(int)value).ToString(CultureInfo.InvariantCulture)),
                RegistryValueKind.QWord => new RegistryData(DebloatValueKind.QWord, unchecked((ulong)(long)value).ToString(CultureInfo.InvariantCulture)),
                RegistryValueKind.Binary => new RegistryData(DebloatValueKind.Binary, RegistryValueCodec.ToHex((byte[])value)),
                RegistryValueKind.MultiString => new RegistryData(DebloatValueKind.MultiString, string.Join("\n", (string[])value)),
                RegistryValueKind.ExpandString => new RegistryData(DebloatValueKind.ExpandString, value.ToString() ?? string.Empty),
                _ => new RegistryData(DebloatValueKind.String, value.ToString() ?? string.Empty),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool KeyExists(DebloatHive hive, string path)
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            var (root, mapped) = OpenRoot(hive, path, writable: false);
            using var rootKey = root;
            using var key = root.OpenSubKey(mapped, writable: false);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(DebloatHive hive, string path)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        try
        {
            var (root, mapped) = OpenRoot(hive, path, writable: false);
            using var rootKey = root;
            using var key = root.OpenSubKey(mapped, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public void Write(DebloatHive hive, string path, string name, RegistryData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        PlatformGuard.ThrowIfNotWindows("Registry changes");
        var (root, mapped) = OpenRoot(hive, path, writable: true);
        using var rootKey = root;
        using var key = root.CreateSubKey(mapped, writable: true) ?? throw new IOException($"Could not open {RegistryValueCodec.HiveName(hive)}\\{path}.");
        switch (data.Kind)
        {
            case DebloatValueKind.DWord:
                if (!RegistryValueCodec.TryParseDword(data.Value, out uint dword))
                {
                    throw new FormatException($"\"{data.Value}\" is not a valid DWORD.");
                }

                key.SetValue(name, unchecked((int)dword), RegistryValueKind.DWord);
                break;
            case DebloatValueKind.Binary:
                key.SetValue(name, RegistryValueCodec.ParseHex(data.Value) ?? throw new FormatException($"\"{data.Value}\" is not valid hex."), RegistryValueKind.Binary);
                break;
            case DebloatValueKind.QWord:
                if (!RegistryValueCodec.TryParseQword(data.Value, out ulong qword))
                {
                    throw new FormatException($"\"{data.Value}\" is not a valid QWORD.");
                }

                key.SetValue(name, unchecked((long)qword), RegistryValueKind.QWord);
                break;
            case DebloatValueKind.ExpandString:
                key.SetValue(name, data.Value, RegistryValueKind.ExpandString);
                break;
            case DebloatValueKind.MultiString:
                key.SetValue(name, data.Value.Length == 0 ? Array.Empty<string>() : data.Value.Split('\n'), RegistryValueKind.MultiString);
                break;
            default:
                key.SetValue(name, data.Value, RegistryValueKind.String);
                break;
        }
    }

    public void DeleteValue(DebloatHive hive, string path, string name)
    {
        PlatformGuard.ThrowIfNotWindows("Registry changes");
        var (root, mapped) = OpenRoot(hive, path, writable: true);
        using var rootKey = root;
        using var key = root.OpenSubKey(mapped, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKeyTree(DebloatHive hive, string path)
    {
        PlatformGuard.ThrowIfNotWindows("Registry changes");
        var (root, mapped) = OpenRoot(hive, path, writable: true);
        using var rootKey = root;
        root.DeleteSubKeyTree(mapped, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// The root key to use for <paramref name="hive"/> and the path under it; the caller disposes the key. For a
    /// redirected HKCU this is HKEY_USERS\&lt;SID&gt; (or &lt;SID&gt;_Classes), opened writable for changes.
    /// </summary>
    private (RegistryKey Root, string Path) OpenRoot(DebloatHive hive, string path, bool writable)
    {
        if (hive == DebloatHive.LocalMachine)
        {
            return (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), path);
        }

        if (hive == DebloatHive.Users)
        {
            return (RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64), path);
        }

        if (Target.Sid is not { } sid)
        {
            return (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64), path);
        }

        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        bool classesLoaded;
        using (var classes = users.OpenSubKey(sid + UserHiveResolver.ClassesHiveSuffix, writable: false))
        {
            classesLoaded = classes is not null;
        }

        var (subKey, mapped) = UserHiveResolver.MapPath(sid, path, classesLoaded);
        var root = users.OpenSubKey(subKey, writable)
                   ?? throw new IOException($"The settings of {Target.AccountName ?? sid} are not loaded (the account signed out). Refresh the page and try again.");
        return (root, mapped);
    }

    /// <summary>
    /// Works out whose HKCU to change: the owner of this session's File Explorer (the signed-in user) when that is a
    /// different account from the one PC Checker runs as and their hive is loaded, otherwise the app's own HKCU with
    /// the documented note. Never throws.
    /// </summary>
    public static UserHiveTarget ResolveSignedInUser()
    {
        if (!PlatformGuard.IsWindows)
        {
            return UserHiveTarget.Own();
        }

        // Same account (the usual case: an administrator who approved the UAC prompt): nothing to look up.
        if (ElevatedUserScope.SessionUser is not null && !ElevatedUserScope.Differs(ElevatedUserScope.ProcessUser, ElevatedUserScope.SessionUser))
        {
            return UserHiveTarget.Own();
        }

        string? processSid;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            processSid = identity.User?.Value;
        }
        catch (Exception)
        {
            return UserHiveTarget.Own(ElevatedUserScope.Note);
        }

        var sid = UserHiveResolver.PickSessionOwner(ExplorerOwnerSids()) ?? SidOf(ElevatedUserScope.SessionUser);
        return UserHiveResolver.Choose(processSid, sid, NameOf(sid) ?? ElevatedUserScope.SessionUser, IsHiveLoaded, ElevatedUserScope.Note);
    }

    /// <summary>Owner SIDs of the explorer.exe processes in this app's session (Win32_Process.GetOwnerSid).</summary>
    private static List<string?> ExplorerOwnerSids()
    {
        var sids = new List<string?>();
        try
        {
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            using var searcher = new ManagementObjectSearcher(@"root\cimv2",
                $"SELECT Handle FROM Win32_Process WHERE Name = 'explorer.exe' AND SessionId = {session.ToString(CultureInfo.InvariantCulture)}");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    if (item is not ManagementObject process)
                    {
                        continue;
                    }

                    try
                    {
                        using var owner = process.InvokeMethod("GetOwnerSid", null, null);
                        if (owner is not null && Convert.ToUInt32(owner["ReturnValue"] ?? 1u, CultureInfo.InvariantCulture) == 0)
                        {
                            sids.Add(owner["Sid"]?.ToString());
                        }
                    }
                    catch (Exception)
                    {
                        // The process exited between the query and the call; ignore it.
                    }
                }
            }
        }
        catch (Exception)
        {
            // WMI unavailable: the caller falls back to the session's user name.
        }

        return sids;
    }

    private static string? SidOf(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return null;
        }

        try
        {
            return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? NameOf(string? sid)
    {
        if (!UserHiveResolver.IsUserSid(sid))
        {
            return null;
        }

        try
        {
            return new SecurityIdentifier(sid!).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsHiveLoaded(string sid)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var key = users.OpenSubKey(sid, writable: false);
        return key is not null;
    }
}
