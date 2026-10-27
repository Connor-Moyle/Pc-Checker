using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PcChecker.Core.Scheduling;

/// <summary>Reads the owner and access rules of the executable and every folder above it for <see cref="TaskExecutableRules"/>.</summary>
[SupportedOSPlatform("windows")]
internal static class TaskExecutableSecurity
{
    /// <summary>Returns why the executable must not be run by an elevated task, or null when its location is protected.</summary>
    public static string? WhyUnsafe(string executablePath)
    {
        try
        {
            var file = new FileInfo(Path.GetFullPath(executablePath));
            if (!file.Exists)
            {
                return $"\"{executablePath}\" was not found, so no task can be created.";
            }

            var fileSecurity = Read(file.FullName, file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            var folders = new List<TaskPathSecurity>();
            for (var dir = file.Directory; dir is not null; dir = dir.Parent)
            {
                folders.Add(Read(dir.FullName, dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)));
            }

            return TaskExecutableRules.WhyUnsafe(fileSecurity, folders);
        }
        catch (Exception ex)
        {
            return $"The permissions of \"{executablePath}\" could not be checked ({ex.Message}), so no task that runs it with administrator rights is created.";
        }
    }

    private static TaskPathSecurity Read(string path, FileSystemSecurity security)
    {
        var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var rules = new List<TaskPathAccessRule>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            rules.Add(new TaskPathAccessRule(rule.IdentityReference.Value, rule.FileSystemRights,
                rule.AccessControlType == AccessControlType.Allow, rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
        }

        return new TaskPathSecurity(path, owner, rules);
    }
}
