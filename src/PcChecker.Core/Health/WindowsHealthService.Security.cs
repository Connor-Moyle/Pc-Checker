using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

/// <summary>
/// Windows Update and security-basics collectors of <see cref="WindowsHealthService"/> (registry, Security Center, netsh, BitLocker).
/// </summary>
public sealed partial class WindowsHealthService
{
    // ---------------------------------------------------------------- windows update

    /// <summary>One row of the Windows Update history (<c>IUpdateHistoryEntry</c>): operation 1 is an install, result 2/3 is success.</summary>
    public sealed record UpdateHistoryEntry(int Operation, int ResultCode, DateTime DateUtc, string? Title);

    // Defender security intelligence (daily) and platform updates also go through Windows Update; they would hide a PC
    // that has stopped installing the monthly Windows updates.
    private static readonly string[] DefenderUpdateKbs = ["KB2267602", "KB4052623"];

    private const int UpdateHistoryRows = 1000;

    private void CollectWindowsUpdate(AnalysisContext ctx)
    {
        // Windows 10 and 11 no longer write Auto Update\Results\Install\LastSuccessTime, so the update history of the
        // Windows Update Agent comes first, then the WindowsUpdateClient "installation successful" event, and the old
        // registry value only when neither can be read.
        DateTimeOffset? lastSuccess = ReadLastInstallFromUpdateAgent() ?? ReadLastInstallFromEventLog();
        if (lastSuccess is null)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install");
            if (key?.GetValue("LastSuccessTime") is string text && TryParseUpdateTime(text, out var parsed))
            {
                lastSuccess = parsed;
            }
        }

        bool rebootPending = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
                             || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        ctx.Items.Add(HealthItemRules.WindowsUpdate(lastSuccess, rebootPending, ctx.Now));
    }

    /// <summary>
    /// Newest successful install in the Windows Update history (operation 1 = installation, result 2 = succeeded,
    /// 3 = succeeded with errors), ignoring Defender definition and platform updates; null when there is none.
    /// </summary>
    public static DateTimeOffset? NewestSuccessfulInstall(IEnumerable<UpdateHistoryEntry>? entries)
    {
        DateTime? newest = null;
        foreach (var e in entries ?? [])
        {
            if (e.Operation != 1 || e.ResultCode is not (2 or 3) || e.DateUtc == default)
            {
                continue;
            }

            if (IsDefenderUpdate(e.Title))
            {
                continue;
            }

            if (newest is null || e.DateUtc > newest)
            {
                newest = e.DateUtc;
            }
        }

        return newest is { } utc ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime() : null;
    }

    private static bool IsDefenderUpdate(string? text) =>
        text is not null && DefenderUpdateKbs.Any(kb => text.Contains(kb, StringComparison.OrdinalIgnoreCase));

    private DateTimeOffset? ReadLastInstallFromUpdateAgent()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: false);
            if (type is null)
            {
                return null;
            }

            dynamic session = Activator.CreateInstance(type)!;
            dynamic searcher = session.CreateUpdateSearcher();
            int total = (int)searcher.GetTotalHistoryCount();
            if (total <= 0)
            {
                return null;
            }

            // The history is returned newest first; the newest rows are enough even with daily Defender updates in between.
            dynamic history = searcher.QueryHistory(0, Math.Min(total, UpdateHistoryRows));
            int count = (int)history.Count;
            var entries = new List<UpdateHistoryEntry>(count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic entry = history.Item(i);
                    entries.Add(new UpdateHistoryEntry((int)entry.Operation, (int)entry.ResultCode, (DateTime)entry.Date, (string?)entry.Title));
                }
                catch (Exception)
                {
                    // Skip a row Windows Update cannot describe.
                }
            }

            return NewestSuccessfulInstall(entries);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows Update history could not be read.");
            return null;
        }
    }

    private DateTimeOffset? ReadLastInstallFromEventLog()
    {
        try
        {
            // Event 19: "Installation Successful: Windows successfully installed the following update".
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and EventID=19]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    string? text = null;
                    try
                    {
                        text = string.Join(" ", record.Properties.Select(p => p.Value?.ToString()));
                    }
                    catch (Exception)
                    {
                        // Keep the row; only the Defender filter needs the text.
                    }

                    if (IsDefenderUpdate(text))
                    {
                        continue;
                    }

                    return record.TimeCreated is { } t ? new DateTimeOffset(t) : null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows Update events could not be read.");
        }

        return null;
    }

    /// <summary>Parses the registry's "yyyy-MM-dd HH:mm:ss" UTC stamp.</summary>
    public static bool TryParseUpdateTime(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        {
            value = new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime();
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value))
        {
            value = value.ToLocalTime();
            return true;
        }

        return false;
    }

    private static bool KeyExists(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- security

    private void CollectSecurity(AnalysisContext ctx, CancellationToken ct)
    {
        var notes = new List<string>();
        TryStep(notes, "antivirus", () =>
        {
            var products = Query(SecurityCenter, "SELECT displayName, productState FROM AntiVirusProduct", ct)
                .Select(r => new SecurityRules.AntivirusProduct(Str(r, "displayName") ?? "Antivirus", (int)(Long(r, "productState") ?? 0)))
                .ToList();
            ctx.Items.Add(SecurityRules.AntivirusItem(products));
        });

        TryStep(notes, "firewall", () =>
        {
            // The firewall policy API sees the effective state (including Group Policy); the registry and netsh are
            // fallbacks, netsh last because it prints in the display language.
            IReadOnlyDictionary<string, bool> profiles = ReadFirewallFromPolicyApi();
            if (profiles.Count == 0)
            {
                profiles = ReadFirewallFromRegistry();
            }

            if (profiles.Count == 0)
            {
                profiles = SecurityRules.ParseNetshProfiles(RunNetsh("advfirewall show allprofiles", ct));
            }

            ctx.Items.Add(SecurityRules.FirewallItem(profiles));
        });

        TryStep(notes, "drive encryption", () =>
        {
            var volumes = new List<(string Drive, int ProtectionStatus)>();
            foreach (var row in Query(VolumeEncryption, "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume", ct))
            {
                var letter = Str(row, "DriveLetter");
                if (!string.IsNullOrWhiteSpace(letter))
                {
                    volumes.Add((letter, (int)(Long(row, "ProtectionStatus") ?? 2)));
                }
            }

            ctx.Items.Add(SecurityRules.BitLockerItem(volumes, ctx.Batteries.Count > 0));
        });

        foreach (var n in notes)
        {
            ctx.Warnings.Add("Security basics: " + n);
        }
    }

    /// <summary>Per-profile state from the Windows Firewall policy COM API (INetFwPolicy2.FirewallEnabled); empty when unavailable.</summary>
    private static Dictionary<string, bool> ReadFirewallFromPolicyApi()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null)
            {
                return result;
            }

            policy = Activator.CreateInstance(type);
            dynamic fw = policy!;
            foreach (var (profile, name) in new (int, string)[] { (1, "Domain"), (2, "Private"), (4, "Public") })
            {
                result[name] = (bool)fw.FirewallEnabled[profile];
            }
        }
        catch (Exception)
        {
            result.Clear();
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy))
            {
                Marshal.FinalReleaseComObject(policy);
            }
        }

        return result;
    }

    private static Dictionary<string, bool> ReadFirewallFromRegistry()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var profiles = new (string Key, string Name)[] { ("DomainProfile", "Domain"), ("StandardProfile", "Private"), ("PublicProfile", "Public") };
        foreach (var (key, name) in profiles)
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + key);
                if (reg?.GetValue("EnableFirewall") is int enabled)
                {
                    result[name] = enabled != 0;
                }
            }
            catch (Exception)
            {
                // Policy-managed profiles can hide the value; netsh is the fallback.
            }
        }

        return result;
    }

    private static string? RunNetsh(string arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("netsh.exe", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();

            // Read asynchronously so a hung netsh cannot block the timeout: the pipe drains while we wait, and a
            // process that is still running after NetshTimeout is killed, which also completes the read.
            var readOutput = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NetshTimeout);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                ct.ThrowIfCancellationRequested();
                return null;
            }

            return readOutput.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception)
        {
            // Already gone or access denied; nothing else to do.
        }
    }
}
