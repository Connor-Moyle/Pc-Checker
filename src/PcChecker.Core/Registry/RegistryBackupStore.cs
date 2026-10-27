using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Startup;

namespace PcChecker.Core.RegistryHealth;

/// <summary>
/// Writes registry backups in the format the Registry page lists and restores: one <c>.reg</c> file plus a JSON
/// manifest, sealed in HKLM (see <see cref="RegistryBackupCatalog"/>). Shared by the registry fixer and the Leftovers
/// page, so a key either of them removes can be put back from either page.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RegistryBackupStore
{
    /// <summary>
    /// Exports a whole key with <c>reg export</c> through a folder only administrators can write to, falling back to a
    /// value-by-value copy. Null when the key cannot be backed up completely (then it must not be removed).
    /// </summary>
    public static async Task<string?> ExportKeyAsync(RegistryAccess access, string hive, string keyPath, ILogger logger, CancellationToken cancellationToken)
    {
        string fullName = RegistryPathTools.FullKeyName(hive, keyPath);
        try
        {
            var exported = await ProtectedTaskFile.UseFolderAsync("PcChecker-reg-", "registry export", async (folder, ct) =>
            {
                string target = Path.Combine(folder, "export.reg");
                var output = await CommandRunner.RunAsync(RegExe(), $"export \"{hive}\\{keyPath}\" \"{target}\" /y /reg:64",
                    TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                if (output.Success && File.Exists(target))
                {
                    var text = await File.ReadAllTextAsync(target, ct).ConfigureAwait(false);
                    if (RegExportBuilder.KeyNames(text).Count > 0)
                    {
                        return text;
                    }
                }

                logger.LogDebug("reg export failed for {Key} ({Output}); falling back to a value-by-value backup.", fullName, output.FirstLine);
                return null;
            }, ex => logger.LogDebug(ex, "Could not remove the temporary registry export folder."), cancellationToken).ConfigureAwait(false);

            if (exported is not null)
            {
                return exported;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            logger.LogDebug(ex, "reg export output could not be read for {Key}.", fullName);
        }

        try
        {
            var tree = access.ReadTree(hive, keyPath);
            return tree.Count > 0 ? RegExportBuilder.Build(tree) : null;
        }
        catch (InvalidOperationException ex)
        {
            // Incomplete fallback backup (unreadable sub-key, unsupported value type, too large): skip the entry.
            logger.LogWarning(ex, "Key {Key} could not be backed up.", fullName);
            return null;
        }
    }

    /// <summary>
    /// Writes one backup made of <paramref name="sections"/> for <paramref name="issues"/> and seals it. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it cannot be saved or sealed, in
    /// which case nothing may be removed.
    /// </summary>
    public static RegistryBackup Write(string directory, IReadOnlyList<string> sections, IReadOnlyList<RegistryIssue> issues, string summary, ILogger logger)
    {
        Directory.CreateDirectory(directory);
        var created = DateTimeOffset.Now;
        string id = RegistryBackupCatalog.NewId(created);
        for (int n = 2; File.Exists(Path.Combine(directory, id + RegistryBackupCatalog.RegExtension)); n++)
        {
            id = RegistryBackupCatalog.NewId(created) + "-" + n;
        }

        string regPath = Path.Combine(directory, id + RegistryBackupCatalog.RegExtension);
        string manifestPath = Path.ChangeExtension(regPath, RegistryBackupCatalog.ManifestExtension);

        // UTF-16 with a byte-order mark, like regedit and reg export write it. The seal is made from these bytes, not
        // from the files read back, and kept in HKLM so a restore can tell when the files were changed afterwards.
        byte[] regFile = RegistryBackupCatalog.EncodeRegFile(RegExportBuilder.Merge(sections));
        var manifest = new RegistryBackupManifest(id, created, issues.Count, summary, issues);
        byte[] manifestFile = RegistryBackupCatalog.EncodeManifest(manifest);
        File.WriteAllBytes(regPath, regFile);
        File.WriteAllBytes(manifestPath, manifestFile);
        try
        {
            RegistryBackupSeals.Save(id, RegistryBackupCatalog.Seal(regFile, manifestFile));
        }
        catch (IOException)
        {
            // A backup that cannot be restored is no backup: remove it so the caller stops before deleting anything.
            TryDelete(regPath);
            TryDelete(manifestPath);
            throw;
        }

        logger.LogInformation("Registry backup {Id} written with {Count} entries: {File}", id, issues.Count, regPath);
        return new RegistryBackup(id, created, regPath, issues.Count, summary);
    }

    public static string RegExe()
    {
        var windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var path = Path.Combine(windows, "System32", "reg.exe");
        return File.Exists(path) ? path : "reg.exe";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless.
        }
    }
}
