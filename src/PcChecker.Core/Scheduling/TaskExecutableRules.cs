using System.Security.AccessControl;

namespace PcChecker.Core.Scheduling;

/// <summary>One access rule of a file or folder: who (SID string), which rights, allow or deny, and whether it only applies to children.</summary>
public sealed record TaskPathAccessRule(string Sid, FileSystemRights Rights, bool IsAllow, bool InheritOnly);

/// <summary>The owner and access rules of one file or folder on the way to the executable.</summary>
public sealed record TaskPathSecurity(string Path, string? OwnerSid, IReadOnlyList<TaskPathAccessRule> Rules);

/// <summary>
/// Decides whether an executable may be registered in a task that runs with highest privileges and no UAC prompt.
/// Only Administrators, SYSTEM and TrustedInstaller may be able to change the file, add files next to it, or rename
/// or re-permission any folder above it (as under Program Files). Otherwise any non-elevated program could replace
/// PcChecker.exe and be started elevated at the next sign-in or scheduled run. Pure, so the rules are unit-tested.
/// </summary>
public static class TaskExecutableRules
{
    /// <summary>BUILTIN\Administrators.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>NT AUTHORITY\SYSTEM.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>NT SERVICE\TrustedInstaller.</summary>
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1849506451-2746019179";

    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    private const FileSystemRights Takeover = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll | GenericWrite;

    /// <summary>Rights on the executable itself that let someone replace or rename it.</summary>
    private const FileSystemRights FileRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | Takeover;

    /// <summary>Rights on the executable's folder that let someone swap the file or plant DLLs next to it.</summary>
    private const FileSystemRights FolderRights = FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | Takeover;

    /// <summary>Rights on a folder further up that let someone rename the folder below it or the folder itself.</summary>
    private const FileSystemRights AncestorRights = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | Takeover;

    /// <summary>True for the accounts that may control an elevated task's executable.</summary>
    public static bool IsTrusted(string? sid) =>
        sid is not null && (sid.Equals(AdministratorsSid, StringComparison.OrdinalIgnoreCase)
                            || sid.Equals(SystemSid, StringComparison.OrdinalIgnoreCase)
                            || sid.Equals(TrustedInstallerSid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns why <paramref name="file"/> must not be run by an elevated task, or null when only trusted accounts can
    /// change it. <paramref name="folders"/> lists its folder first, then each parent up to the drive root.
    /// </summary>
    public static string? WhyUnsafe(TaskPathSecurity file, IReadOnlyList<TaskPathSecurity> folders)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(folders);
        if (folders.Count == 0)
        {
            return Unsafe(file.Path, file.Path);
        }

        if (!IsTrusted(file.OwnerSid) || GrantsUntrusted(file, FileRights))
        {
            return Unsafe(file.Path, file.Path);
        }

        for (int i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            if (!IsTrusted(folder.OwnerSid) || GrantsUntrusted(folder, i == 0 ? FolderRights : AncestorRights))
            {
                return Unsafe(file.Path, folder.Path);
            }
        }

        return null;
    }

    /// <summary>The message shown when the executable's location is not protected.</summary>
    public static string Unsafe(string executablePath, string weakPath) =>
        $"PC Checker cannot add a task that starts it with administrator rights from \"{executablePath}\": "
        + (string.Equals(executablePath, weakPath, StringComparison.OrdinalIgnoreCase)
            ? "the file"
            : $"\"{weakPath}\"")
        + " can be changed without administrator rights, so another program could replace PcChecker.exe and get administrator rights without a UAC prompt. "
        + "Move PcChecker.exe to a folder only administrators can change, such as C:\\Program Files\\PC Checker "
        + "(\"Install in Program Files\" in Settings does this), start it from there and try again.";

    private static bool GrantsUntrusted(TaskPathSecurity path, FileSystemRights dangerous) =>
        path.Rules.Any(r => r.IsAllow && !r.InheritOnly && !IsTrusted(r.Sid) && (r.Rights & dangerous) != 0);
}
