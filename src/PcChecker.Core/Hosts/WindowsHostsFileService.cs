using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Hosts;

/// <summary>
/// The real hosts file: found through %SystemRoot% (and the Tcpip DataBasePath setting when it points elsewhere), read
/// and written in its own encoding, with read-only, hidden and system attributes lifted only while saving.
/// </summary>
public sealed class WindowsHostsFileService : IHostsFileService
{
    public const string Module = "Hosts file";

    /// <summary>Shown when Windows or security software refuses the write.</summary>
    public const string LockedMessage =
        "Windows or your security software blocked the change. Some antivirus programs (for example Malwarebytes, " +
        "Bitdefender, Norton or Kaspersky) lock the hosts file to protect it. Turn off their hosts file protection for a " +
        "moment, save again here, then turn it back on. Your edits are still on this page.";

    private const string TcpipParameters = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
    private const FileAttributes Lifted = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsHostsFileService> _logger;

    public WindowsHostsFileService(IChangeGuard guard, ILogger<WindowsHostsFileService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public string HostsPath => Locate().Path;

    public string BackupFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcChecker", "backup", "hosts");

    public Task<HostsSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (path, unusual) = Locate();
        byte[]? bytes = null;
        var facts = new HostsFileFacts(false, 0, false, false, false, path, unusual);
        var extra = new List<string>();
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            facts = facts with
            {
                Exists = true,
                Size = info.Length,
                ReadOnly = info.Attributes.HasFlag(FileAttributes.ReadOnly),
                Hidden = info.Attributes.HasFlag(FileAttributes.Hidden),
                System = info.Attributes.HasFlag(FileAttributes.System),
            };
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The hosts file could not be read.");
                throw new IOException("The hosts file could not be read: " + ex.Message + " Security software may be locking it.", ex);
            }
        }

        _logger.LogInformation("Hosts file read: {Path}, {Size} bytes.", path, facts.Size);
        return HostsSnapshotBuilder.Build(path, bytes, facts, AnsiCodePage(), extra);
    }, cancellationToken);

    public Task<OperationResult<HostsBackup?>> SaveAsync(HostsSnapshot loaded, string text, string description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(text);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult<HostsBackup?>.Fail("The hosts file can only be changed on Windows."));
        }

        string path = loaded.Path;
        string backupPath = FreeBackupPath(DateTime.Now);
        var options = new ChangeOptions(null, null, Module, path, true,
            $"Use Undo last save on Network → Hosts file, or copy {backupPath} back over {path} and run ipconfig /flushdns.");
        return _guard.RunGuardedAsync<HostsBackup?>(description, options, ct => Task.Run(() => Write(loaded, text, backupPath), ct), null, cancellationToken);
    }

    public Task<IReadOnlyList<HostsBackup>> GetBackupsAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<HostsBackup>>(() =>
    {
        try
        {
            if (!Directory.Exists(BackupFolder))
            {
                return [];
            }

            return Directory.EnumerateFiles(BackupFolder, "hosts-*.bak")
                .Select(p => (Path: p, When: HostsFileRules.ParseBackupFileName(Path.GetFileName(p))))
                .Where(b => b.When is not null)
                .Select(b => new HostsBackup(b.Path, b.When!.Value, new FileInfo(b.Path).Length))
                .OrderByDescending(b => b.Created)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Hosts backups could not be listed.");
            return [];
        }
    }, cancellationToken);

    public Task<OperationResult<string>> ReadBackupAsync(HostsBackup backup, CancellationToken cancellationToken) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(backup);
        try
        {
            var bytes = File.ReadAllBytes(backup.Path);
            return OperationResult<string>.Ok(HostsEncodingDetector.Decode(bytes, HostsEncodingDetector.Detect(bytes), AnsiCodePage()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<string>.Fail("The backup could not be read: " + ex.Message, ex);
        }
    }, cancellationToken);

    private OperationResult<HostsBackup?> Write(HostsSnapshot loaded, string text, string backupPath)
    {
        string path = loaded.Path;
        byte[]? current = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (HostsSnapshotBuilder.Hash(current) != loaded.ContentHash)
        {
            return OperationResult<HostsBackup?>.Fail("Another program changed the hosts file after PC Checker read it. Reload the page and make your change again.");
        }

        HostsBackup? backup = null;
        if (current is not null)
        {
            Directory.CreateDirectory(BackupFolder);
            File.WriteAllBytes(backupPath, current);
            backup = new HostsBackup(backupPath, DateTime.Now, current.Length);
        }

        int codePage = AnsiCodePage();
        var encoding = HostsEncodingDetector.ForSaving(text, loaded.Encoding, codePage);
        var bytes = HostsEncodingDetector.Encode(text, encoding, codePage);
        FileAttributes? original = current is null ? null : File.GetAttributes(path);
        try
        {
            if (original is { } attrs && (attrs & Lifted) != 0)
            {
                File.SetAttributes(path, attrs & ~Lifted);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Writing the hosts file was refused.");
            return OperationResult<HostsBackup?>.Fail(LockedMessage, ex);
        }
        finally
        {
            RestoreAttributes(path, original);
        }

        string encodingNote = encoding != loaded.Encoding
            ? $" It is now saved as {HostsEncodingDetector.Describe(encoding)} so the characters you typed are kept."
            : string.Empty;
        string backupNote = backup is null ? string.Empty : $" The previous version is in {backup.Path}.";
        return OperationResult<HostsBackup?>.Ok(backup, "The hosts file was saved." + backupNote + encodingNote);
    }

    private void RestoreAttributes(string path, FileAttributes? original)
    {
        if (original is not { } attrs || (attrs & Lifted) == 0)
        {
            return;
        }

        try
        {
            File.SetAttributes(path, attrs);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "The hosts file attributes could not be set back.");
        }
    }

    private string FreeBackupPath(DateTime now)
    {
        string name = HostsFileRules.BackupFileName(now);
        string path = Path.Combine(BackupFolder, name);
        for (int i = 2; File.Exists(path) && i < 100; i++)
        {
            path = Path.Combine(BackupFolder, name.Replace(".bak", $" ({i}).bak", StringComparison.Ordinal));
        }

        return path;
    }

    /// <summary>The hosts file Windows reads, and whether Tcpip's DataBasePath moved it.</summary>
    private static (string Path, bool Unusual) Locate()
    {
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } root
            ? root
            : Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } windows ? windows : @"C:\Windows";
        string defaultPath = Path.Combine(HostsFileRules.DefaultFolder(systemRoot), "hosts");
        if (!PlatformGuard.IsWindows)
        {
            return (defaultPath, false);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TcpipParameters);
            var value = key?.GetValue("DataBasePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (!HostsFileRules.IsDefaultDatabasePath(value, systemRoot))
            {
                string folder = Environment.ExpandEnvironmentVariables(value!.Trim());
                if (Directory.Exists(folder))
                {
                    return (Path.Combine(folder, "hosts"), true);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall back to the usual place.
        }

        return (defaultPath, false);
    }

    private static int AnsiCodePage()
    {
        try
        {
            return PlatformGuard.IsWindows ? GetACP() : HostsEncodingDetector.DefaultAnsiCodePage;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return HostsEncodingDetector.DefaultAnsiCodePage;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetACP();
}
