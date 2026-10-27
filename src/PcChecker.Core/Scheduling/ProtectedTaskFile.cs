using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Hands a task definition to <c>schtasks /XML</c> through a file nobody but administrators and SYSTEM can change.
/// The elevated app registers tasks that run with highest privileges, so the definition must not pass through a
/// folder the signed-in user can write to (such as %TEMP%): a non-elevated process could swap the file between the
/// write and schtasks reading it and get its own command registered to run elevated without a UAC prompt.
/// Each call creates a fresh, randomly named folder under %ProgramData% with an explicit, non-inherited ACL
/// (Administrators and SYSTEM only, owned by Administrators), checks that ACL, writes the file and removes the
/// folder afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProtectedTaskFile
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>Writes <paramref name="xml"/> (UTF-16) to a protected file, runs <paramref name="use"/> with its path, then deletes it.</summary>
    /// <exception cref="IOException">The protected folder could not be created or did not end up protected.</exception>
    /// <exception cref="UnauthorizedAccessException">The process is not elevated.</exception>
    public static async Task<T> UseAsync<T>(string xml, Func<string, CancellationToken, Task<T>> use, Action<Exception> onCleanupError, CancellationToken cancellationToken)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(programData))
        {
            throw new IOException("The ProgramData folder could not be found.");
        }

        var folder = Path.Combine(programData, "PcChecker-task-" + Guid.NewGuid().ToString("N"));
        var directory = CreateProtectedDirectory(folder);
        try
        {
            EnsureProtected(directory);
            var path = Path.Combine(directory.FullName, "task.xml");
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, Encoding.Unicode))
            {
                await writer.WriteAsync(xml.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            return await use(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onCleanupError(ex);
            }
        }
    }

    private static DirectoryInfo CreateProtectedDirectory(string folder)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { Administrators, LocalSystem })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security.CreateDirectory(folder);
    }

    /// <summary>Refuses a folder that is not owned by Administrators/SYSTEM, inherits rules, or grants anyone else access.</summary>
    private static void EnsureProtected(DirectoryInfo directory)
    {
        var actual = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = actual.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !(owner.Equals(Administrators) || owner.Equals(LocalSystem)))
        {
            throw new IOException("The folder for the task definition is not owned by Administrators.");
        }

        if (!actual.AreAccessRulesProtected)
        {
            throw new IOException("The folder for the task definition inherits permissions.");
        }

        foreach (FileSystemAccessRule rule in actual.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && !(rule.IdentityReference.Equals(Administrators) || rule.IdentityReference.Equals(LocalSystem)))
            {
                throw new IOException("The folder for the task definition can be changed by other accounts.");
            }
        }
    }
}
