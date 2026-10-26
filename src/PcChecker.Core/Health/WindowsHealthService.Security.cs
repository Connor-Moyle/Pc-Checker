using System.Diagnostics;
using System.Globalization;
using System.Management;
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

    private void CollectWindowsUpdate(AnalysisContext ctx)
    {
        DateTimeOffset? lastSuccess = null;
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install"))
        {
            if (key?.GetValue("LastSuccessTime") is string text && TryParseUpdateTime(text, out var parsed))
            {
                lastSuccess = parsed;
            }
        }

        bool rebootPending = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
                             || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        ctx.Items.Add(HealthItemRules.WindowsUpdate(lastSuccess, rebootPending, ctx.Now));
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
            IReadOnlyDictionary<string, bool> profiles = ReadFirewallFromRegistry();
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
