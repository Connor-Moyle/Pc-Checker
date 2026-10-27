using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Hosts;

/// <summary>A sample hosts file kept in memory: a few local names, an ad block list, and two entries that look unwanted.</summary>
public sealed class DemoHostsFileService : IHostsFileService
{
    private const string Sample =
        HostsFileRules.WindowsDefault +
        "\r\n" +
        "# Home network\r\n" +
        "192.168.1.20\tnas.home nas\t# Synology in the cupboard\r\n" +
        "192.168.1.31\tprinter.home\r\n" +
        "fd00::20\tnas6.home\r\n" +
        "\r\n" +
        "# Development\r\n" +
        "127.0.0.1\tmyapp.test api.myapp.test\r\n" +
        "# 10.0.0.5\tstaging.myapp.test\r\n" +
        "\r\n" +
        "# Blocked trackers\r\n" +
        "0.0.0.0 ads.doubleclick.net\r\n" +
        "0.0.0.0 tracking.example-ads.com\r\n" +
        "0.0.0.0 telemetry.example-analytics.io\r\n" +
        "\r\n" +
        "0.0.0.0 mbamupdates.com\r\n" +
        "185.243.112.34 www.paypal.com paypal.com\r\n";

    private readonly object _gate = new();
    private readonly List<(HostsBackup Backup, byte[] Bytes)> _backups = [];
    private byte[] _bytes = Encoding.ASCII.GetBytes(Sample);

    public DemoHostsFileService()
    {
        var older = Encoding.ASCII.GetBytes(HostsFileRules.WindowsDefault);
        _backups.Add((new HostsBackup(Path.Combine(BackupFolder, "hosts-2026-09-14_18-02-11.bak"), new DateTime(2026, 9, 14, 18, 2, 11), older.Length), older));
    }

    public string HostsPath => @"C:\Windows\System32\drivers\etc\hosts";

    public string BackupFolder => @"C:\Users\Alex\AppData\Local\PcChecker\backup\hosts";

    public async Task<HostsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var facts = new HostsFileFacts(true, _bytes.Length, ReadOnly: true, Hidden: false, System: false, HostsPath, UnusualFolder: false);
            return HostsSnapshotBuilder.Build(HostsPath, _bytes.ToArray(), facts);
        }
    }

    public async Task<OperationResult<HostsBackup?>> SaveAsync(HostsSnapshot loaded, string text, string description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(text);
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (HostsSnapshotBuilder.Hash(_bytes) != loaded.ContentHash)
            {
                return OperationResult<HostsBackup?>.Fail("Another program changed the hosts file after PC Checker read it. Reload the page and make your change again.");
            }

            var now = DateTime.Now;
            var backup = new HostsBackup(Path.Combine(BackupFolder, HostsFileRules.BackupFileName(now)), now, _bytes.Length);
            _backups.Add((backup, _bytes));
            _bytes = HostsEncodingDetector.Encode(text, HostsEncodingDetector.ForSaving(text, loaded.Encoding));
            return OperationResult<HostsBackup?>.Ok(backup, $"The hosts file was saved (demo). The previous version is in {backup.Path}.");
        }
    }

    public Task<IReadOnlyList<HostsBackup>> GetBackupsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<HostsBackup>>(_backups.Select(b => b.Backup).OrderByDescending(b => b.Created).ToList());
        }
    }

    public Task<OperationResult<string>> ReadBackupAsync(HostsBackup backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        lock (_gate)
        {
            var match = _backups.FirstOrDefault(b => b.Backup.Path == backup.Path);
            return Task.FromResult(match.Bytes is null
                ? OperationResult<string>.Fail("That backup is no longer there.")
                : OperationResult<string>.Ok(HostsEncodingDetector.Decode(match.Bytes, HostsEncodingDetector.Detect(match.Bytes))));
        }
    }
}
