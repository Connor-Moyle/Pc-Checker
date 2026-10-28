using System.Diagnostics;
using System.Management;
using System.Text;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.History;
using PcChecker.Core.Logging;
using PcChecker.Core.Processes;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.Core.Crashes;

public sealed partial class WindowsCrashService
{
    /// <summary>Only the end of setupapi.dev.log is read (it can grow to tens of megabytes).</summary>
    private const int SetupApiTailBytes = 4 * 1024 * 1024;

    /// <summary>History is loaded for at most this many crashes (the newest).</summary>
    private const int MaxContextCrashes = 40;

    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The install sections in the last part of %windir%\INF\setupapi.dev.log inside the scan window.</summary>
    private IReadOnlyList<SetupApiInstall> ReadSetupApiLog(DateTimeOffset now)
    {
        try
        {
            var path = Path.Combine(_windows, "INF", "setupapi.dev.log");
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - SetupApiTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n');
            var cutoff = now.AddDays(-CrashRules.WindowDays).LocalDateTime;
            return SetupApiLogParser.Parse(start > 0 ? lines.Skip(1) : lines).Where(i => i.LocalTime >= cutoff).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "setupapi.dev.log could not be read.");
            return [];
        }
    }

    public async Task<CrashContextData> LoadContextAsync(IReadOnlyList<CrashEvent> crashes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(crashes);
        var journal = await Guarded(async ct => _journal is null ? [] : await _journal.ReadAsync(1000).WaitAsync(SourceTimeout, ct).ConfigureAwait(false),
            (IReadOnlyList<ActionRecord>)[], "the action journal", cancellationToken).ConfigureAwait(false);
        var updates = await Guarded(async ct =>
        {
            if (_updates is null)
            {
                return (IReadOnlyList<UpdateInstallRecord>)[];
            }

            var history = await _updates.GetHistoryAsync(200, ct).WaitAsync(SourceTimeout, ct).ConfigureAwait(false);
            return history.Entries
                .Where(e => e.Date is not null && e.Outcome is UpdateHistoryOutcome.Succeeded or UpdateHistoryOutcome.SucceededWithErrors)
                .Select(e => new UpdateInstallRecord(e.Date!.Value, e.Title, e.Classification == UpdateClassification.Driver))
                .ToList();
        }, [], "the Windows Update history", cancellationToken).ConfigureAwait(false);

        var samples = new List<HistorySample>();
        if (_history is not null)
        {
            foreach (var crash in crashes.Where(CrashPatternRules.IsSerious).OrderByDescending(c => c.Time).Take(MaxContextCrashes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = await Guarded(ct => _history.LoadAsync(crash.Time - CrashContextRules.HistoryWindow - TimeSpan.FromMinutes(1), crash.Time + TimeSpan.FromMinutes(2), ct),
                    HistoryData.Empty(crash.Time, crash.Time), "the History recording", cancellationToken).ConfigureAwait(false);
                samples.AddRange(data.Samples);
            }
        }

        var unique = samples.GroupBy(s => s.Time).Select(g => g.First()).OrderBy(s => s.Time).ToList();
        return new CrashContextData(unique, journal, updates);
    }

    private async Task<T> Guarded<T>(Func<CancellationToken, Task<T>> load, T fallback, string what, CancellationToken ct)
    {
        try
        {
            return await load(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Crash context: {What} could not be read.", what);
            return fallback;
        }
    }

    public Task<CrashDriverDetails?> InspectDriverAsync(string module, CancellationToken cancellationToken) =>
        Task.Run(() => InspectDriver(module, cancellationToken), cancellationToken);

    private CrashDriverDetails? InspectDriver(string module, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(module) || !PlatformGuard.IsWindows)
        {
            return null;
        }

        var file = Path.GetFileName(module.Trim());
        string? service = null;
        string? path = null;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PathName FROM Win32_SystemDriver");
            foreach (var row in searcher.Get().Cast<ManagementObject>())
            {
                using (row)
                {
                    var image = NormalizeImagePath(row["PathName"] as string);
                    if (image is not null && string.Equals(Path.GetFileName(image), file, StringComparison.OrdinalIgnoreCase))
                    {
                        service = row["Name"] as string;
                        path = image;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Win32_SystemDriver could not be read for {Module}.", file);
        }

        ct.ThrowIfCancellationRequested();
        path ??= new[] { Path.Combine(_windows, "System32", "drivers", file), Path.Combine(_windows, "System32", file) }.FirstOrDefault(File.Exists);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        string? company = null, description = null, version = null;
        DateTimeOffset? fileDate = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            company = Blank(info.CompanyName);
            description = Blank(info.FileDescription);
            version = Blank(info.FileVersion)?.Split(' ')[0];
            fileDate = new DateTimeOffset(File.GetLastWriteTime(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            _logger.LogDebug(ex, "Version of {Path} could not be read.", path);
        }

        var signer = AuthenticodeVerifier.Verify(path).Publisher;
        ct.ThrowIfCancellationRequested();

        string? deviceName = null, deviceId = null, driverVersion = null, inf = null;
        DateTime? driverDate = null;
        if (!string.IsNullOrWhiteSpace(service))
        {
            try
            {
                using var devices = new ManagementObjectSearcher($"SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Service = '{Wql(service)}'");
                foreach (var row in devices.Get().Cast<ManagementObject>())
                {
                    using (row)
                    {
                        deviceName ??= row["Name"] as string;
                        deviceId ??= row["DeviceID"] as string;
                    }
                }

                if (deviceId is not null)
                {
                    using var signed = new ManagementObjectSearcher($"SELECT DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver WHERE DeviceID = '{Wql(deviceId)}'");
                    foreach (var row in signed.Get().Cast<ManagementObject>())
                    {
                        using (row)
                        {
                            driverVersion ??= row["DriverVersion"] as string;
                            inf ??= row["InfName"] as string;
                            if (driverDate is null && row["DriverDate"] is string dmtf && Restore.DmtfDateTime.TryParse(dmtf, out var d))
                            {
                                driverDate = d.Date;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "The device for driver service {Service} could not be read.", service);
            }
        }

        return new CrashDriverDetails
        {
            FileName = file,
            Path = path,
            Company = company,
            Description = description,
            FileVersion = version,
            FileDate = fileDate,
            Signer = signer,
            IsMicrosoft = CrashDriverRules.IsMicrosoft(company, signer) || CrashBlameRules.IsWindowsModule(file),
            DeviceName = deviceName,
            DeviceId = deviceId,
            DriverVersion = driverVersion,
            DriverDate = driverDate,
        };
    }

    /// <summary>"\SystemRoot\System32\drivers\x.sys", "System32\drivers\x.sys" or "\??\C:\…" → a full path.</summary>
    private string? NormalizeImagePath(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var p = image.Trim().Trim('"');
        if (p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            p = Path.Combine(_windows, p[@"\SystemRoot\".Length..]);
        }
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
        {
            p = Path.Combine(_windows, p);
        }

        return p;
    }

    private static string Wql(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
