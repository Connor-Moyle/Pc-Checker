using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Startup;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// Reading Windows' own records. The System Resource Usage Monitor keeps them in
/// <c>%windir%\System32\sru\SRUDB.dat</c>, which the Diagnostic Policy Service holds open, so it is never read in place:
/// <c>esentutl /y … /vss</c> (built into Windows, needs administrator rights) copies it from a Volume Shadow Copy
/// snapshot, first with <c>/vssrec</c> so the snapshot is brought to a clean state, then without it if that fails.
/// The copy goes into a fresh folder under ProgramData that only Administrators and SYSTEM can open (it holds
/// usage history), is read with <see cref="SrumDatabaseReader"/>, and the folder is deleted straight afterwards.
/// The result is kept for a few minutes so changing the date range does not copy again.
/// </summary>
public sealed partial class WindowsAppHistoryService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _srumGate = new(1, 1);
    private (DateTimeOffset At, AppHistoryResult Result)? _srumCache;

    private async Task<(AppHistoryResult? Result, SrumFailure? Failure)> LoadWindowsRecordsAsync(bool forceReload, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        await _srumGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceReload && _srumCache is { } cached && DateTimeOffset.Now - cached.At < CacheLifetime)
            {
                return (cached.Result, null);
            }

            var result = await Task.Run(() => ReadWindowsRecordsAsync(progress, ct), ct).ConfigureAwait(false);
            _srumCache = (DateTimeOffset.Now, result);
            return (result, null);
        }
        catch (SrumReadException ex)
        {
            _logger.LogInformation(ex.InnerException ?? ex, "Windows' app usage records were not read ({Kind}).", ex.Kind);
            return (null, new SrumFailure(ex.Kind, ex.Message));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = SrumErrorClassifier.Classify(ex);
            _logger.LogWarning(ex, "Windows' app usage records could not be read ({Kind}).", kind);
            return (null, SrumErrorClassifier.Describe(kind));
        }
        finally
        {
            _srumGate.Release();
        }
    }

    private async Task<AppHistoryResult> ReadWindowsRecordsAsync(IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        if (!PlatformGuard.IsWindows)
        {
            throw Failure(SrumFailureKind.Unsupported);
        }

        var sruFolder = Path.Combine(Environment.SystemDirectory, "sru");
        var source = Path.Combine(sruFolder, "SRUDB.dat");
        if (!File.Exists(source))
        {
            throw Failure(SrumFailureKind.Missing);
        }

        if (!IsElevated())
        {
            throw Failure(SrumFailureKind.NotAdministrator);
        }

        progress?.Report(new ProgressUpdate("Copying Windows' app usage records…", 10, "Windows takes a snapshot of drive C: first; this can take a few seconds."));
        var raw = await ProtectedTaskFile.UseFolderAsync("PcChecker-apphistory-", "copy of the app usage records", async (folder, token) =>
        {
            var copy = Path.Combine(folder, "SRUDB.dat");
            var esentutl = CommandRunner.SystemTool("esentutl.exe");
            var output = await CommandRunner.RunAsync(esentutl, ["/y", source, "/vss", "/vssrec", "sru", sruFolder, "/d", copy, "/o"], CopyTimeout, token).ConfigureAwait(false);
            if (!output.Success || !File.Exists(copy))
            {
                _logger.LogInformation("esentutl copy with recovery ended with {Code}; trying a plain snapshot copy.", output.ExitCode);
                TryDeleteFile(copy);
                output = await CommandRunner.RunAsync(esentutl, ["/y", source, "/vss", "/d", copy, "/o"], CopyTimeout, token).ConfigureAwait(false);
            }

            if (output.TimedOut)
            {
                throw Failure(SrumFailureKind.TimedOut);
            }

            if (!output.Success || !File.Exists(copy))
            {
                var kind = SrumErrorClassifier.Classify(output.ExitCode);
                _logger.LogWarning("esentutl could not copy the SRUM database (exit code {Code}): {Line}", output.ExitCode, output.FirstLine);
                throw Failure(kind == SrumFailureKind.None ? SrumFailureKind.Unknown : kind);
            }

            progress?.Report(new ProgressUpdate("Reading Windows' app usage records…", 50));
            return SrumDatabaseReader.Read(copy, folder, progress, token);
        }, ex => _logger.LogWarning(ex, "The temporary copy of the app usage records could not be deleted."), ct).ConfigureAwait(false);

        progress?.Report(new ProgressUpdate("Adding up the figures…", 90));
        var (records, apps) = SrumRecordBuilder.Build(raw, ReadProcessorMhz(), ReadDeviceMap());
        var named = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase);
        int lookups = 0;
        foreach (var (key, identity) in apps)
        {
            named[key] = Enrich(identity, identity.Path is not null && lookups++ < MaxIdentityLookups);
        }

        if (raw.SkippedRows > 0)
        {
            _logger.LogInformation("{Count} SRUM rows could not be read and were skipped.", raw.SkippedRows);
        }

        return new AppHistoryResult(AppUsageSource.WindowsRecords, records, named, DateTimeOffset.Now)
        {
            OldestRecord = records.Count == 0 ? null : records.Min(r => r.Start),
            OwnRecordingOn = HistoryOptions.Enabled,
            Warnings = raw.Warnings,
        };
    }

    private static SrumReadException Failure(SrumFailureKind kind) => new(kind, SrumErrorClassifier.Message(kind));

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The processor's base speed in MHz as Windows reports it (registry value <c>~MHz</c>), or null.</summary>
    private double? ReadProcessorMhz()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") is int mhz ? mhz : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "The processor speed could not be read.");
            return null;
        }
    }

    /// <summary>Every drive letter's device name (\Device\HarddiskVolume3 → C:), from QueryDosDevice.</summary>
    private static Dictionary<string, string> ReadDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new StringBuilder(1024);
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = letter + ":";
            buffer.Clear();
            try
            {
                if (QueryDosDevice(drive, buffer, buffer.Capacity) > 0)
                {
                    var device = buffer.ToString();
                    if (device.Length > 0 && !map.ContainsKey(device))
                    {
                        map[device] = drive;
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                break;
            }
        }

        return map;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryDosDeviceW")]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
}
