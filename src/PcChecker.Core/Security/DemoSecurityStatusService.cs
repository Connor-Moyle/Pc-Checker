using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

/// <summary>
/// The sample gaming desktop's security status for demo mode and screenshots: Defender on and current, firewall on,
/// one data drive unencrypted, memory integrity off and SMB 1.0 left on for an old NAS, so the page shows fixes. Actions
/// change the sample data only.
/// </summary>
public sealed class DemoSecurityStatusService : ISecurityStatusService
{
    private readonly object _gate = new();
    private SecurityFacts _facts;

    public DemoSecurityStatusService()
    {
        var now = DateTimeOffset.Now;
        _facts = new SecurityFacts
        {
            Antivirus = [new SecurityProduct("Windows Defender", 0x061100, true)],
            Firewalls = [],
            Defender = new DefenderStatus
            {
                AntivirusEnabled = true,
                RealTimeProtection = true,
                TamperProtection = true,
                CloudProtection = true,
                DefinitionVersion = "1.441.212.0",
                DefinitionsUpdated = now.AddHours(-5),
                DefinitionAgeDays = 0,
                LastQuickScan = now.AddDays(-1).AddHours(-3),
                LastFullScan = now.AddDays(-23),
                EngineVersion = "1.1.25090.3",
                PlatformVersion = "4.18.25090.3009",
                RunningMode = "Normal",
            },
            FirewallProfiles = [new("Domain", true), new("Private", true), new("Public", true)],
            SmartScreenApps = true,
            SmartScreenEdge = true,
            Uac = UacLevel.Default,
            SecureBoot = true,
            Tpm = new TpmInfo(true, true, true, "2.0, 0, 1.59"),
            MemoryIntegrity = false,
            CredentialGuard = false,
            Volumes = [new EncryptedVolume("C:", true, 1, 1), new EncryptedVolume("D:", false, 0, 0)],
            EncryptionAvailable = true,
            LicenseStatus = 1,
            Windows = new WindowsVersionInfo("Windows 10 Pro", "Professional", "24H2", 26100, 4946),
            PendingRestartReason = null,
            RemoteDesktopEnabled = false,
            GuestAccountEnabled = false,
            Smb1Enabled = true,
            UpdatesPausedUntil = null,
        };
    }

    public async Task<SecurityFacts> ReadAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        lock (_gate)
        {
            return _facts;
        }
    }

    public async Task<IReadOnlyList<ThreatDetection>> GetRecentThreatsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        string user = Environment.UserName;
        return
        [
            new ThreatDetection(2147735503, "Trojan:Win32/Wacatac.B!ml", 5, 3, now.AddDays(-12).AddHours(-2),
                $@"C:\Users\{user}\Downloads\game-trainer-v2.exe", "C:\\Windows\\explorer.exe"),
            new ThreatDetection(2147758930, "PUA:Win32/Presenoker", 1, 4, now.AddDays(-41),
                $@"C:\Users\{user}\Downloads\free-pdf-converter-setup.exe", "Unknown"),
            new ThreatDetection(2147816380, "HackTool:Win32/AutoKMS", 4, 3, now.AddDays(-97),
                @"C:\ProgramData\KMSAuto\KMSAuto.exe", "Unknown"),
        ];
    }

    public async Task<OperationResult> UpdateDefinitionsAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Downloading the newest definitions…"));
        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
        Update(f => f with
        {
            Defender = f.Defender! with { DefinitionVersion = "1.441.230.0", DefinitionsUpdated = DateTimeOffset.Now, DefinitionAgeDays = 0 },
        });
        return OperationResult.Ok("Microsoft Defender's definitions are up to date (demo).");
    }

    public async Task<OperationResult> RunScanAsync(DefenderScanType type, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string name = type == DefenderScanType.Full ? "Full scan" : "Quick scan";
        int seconds = type == DefenderScanType.Full ? 6 : 3;
        for (int i = 0; i < seconds; i++)
        {
            progress?.Report(new ProgressUpdate($"{name} running… 0 min {i} s"));
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.Now;
        Update(f => f with
        {
            Defender = type == DefenderScanType.Full ? f.Defender! with { LastFullScan = now, LastQuickScan = now } : f.Defender! with { LastQuickScan = now },
        });
        return OperationResult.Ok($"{name} finished. Microsoft Defender found nothing that needs you (demo).");
    }

    public Task<OperationResult> TurnOnFirewallAsync(IReadOnlyList<string> profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        Update(f => f with
        {
            FirewallProfiles = f.FirewallProfiles
                .Select(p => profiles.Contains(p.Profile, StringComparer.OrdinalIgnoreCase) ? p with { Enabled = true } : p)
                .ToList(),
        });
        return Task.FromResult(OperationResult.Ok($"Windows Firewall is on again for {string.Join(", ", profiles)} (demo)."));
    }

    public async Task<OperationResult> TurnOffSmb1Async(CancellationToken cancellationToken)
    {
        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        Update(f => f with { Smb1Enabled = false, PendingRestartReason = "Turning off SMB 1.0 finishes after a restart." });
        return OperationResult.Ok("SMB 1.0 is turned off. Restart to finish (demo).", rebootRequired: true);
    }

    public OperationResult OpenSetting(string link) =>
        SecurityLinks.IsAllowed(link)
            ? OperationResult.Ok($"Would open {link} (demo).")
            : OperationResult.Fail("That setting cannot be opened from here.");

    private void Update(Func<SecurityFacts, SecurityFacts> change)
    {
        lock (_gate)
        {
            _facts = change(_facts);
        }
    }
}
