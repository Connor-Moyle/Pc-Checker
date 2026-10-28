using PcChecker.Core.Common;
using PcChecker.Core.Security;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Security page rules: Security Center decoding, UAC, activation, servicing dates, checks and the score.</summary>
public class SecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x061100u, true, true, 0x06)]   // Defender on, up to date
    [InlineData(0x060100u, false, true, 0x06)]  // off (snoozed bit), up to date
    [InlineData(0x061110u, true, false, 0x06)]  // on, definitions out of date
    [InlineData(0x041000u, true, true, 0x04)]   // third-party antivirus on
    [InlineData(0x040000u, false, true, 0x04)]  // third-party antivirus off
    [InlineData(266256u, true, false, 0x04)]    // 0x041010
    public void Security_center_product_state_is_decoded(uint state, bool enabled, bool upToDate, int provider)
    {
        var decoded = SecurityCenterState.Decode(state);
        Assert.Equal(enabled, decoded.Enabled);
        Assert.Equal(upToDate, decoded.UpToDate);
        Assert.Equal(provider, decoded.ProviderFlags);
    }

    [Fact]
    public void Snoozed_products_are_neither_on_nor_reported_as_snoozed_when_on()
    {
        Assert.True(SecurityCenterState.Decode(0x060100).Snoozed);
        Assert.False(SecurityCenterState.Decode(0x061100).Snoozed);
    }

    [Fact]
    public void Active_third_party_antivirus_is_found_and_defender_is_skipped()
    {
        var products = new[]
        {
            new SecurityProduct("Windows Defender", 0x060100, true),
            new SecurityProduct("Avast Antivirus", 0x041000, false),
        };
        Assert.Equal("Avast Antivirus", SecurityCenterState.ActiveThirdParty(products)!.Name);
        Assert.Null(SecurityCenterState.ActiveThirdParty([new SecurityProduct("Old AV", 0x040000, false)]));
        Assert.True(SecurityCenterState.IsMicrosoftProduct("Windows Defender", "windowsdefender://"));
        Assert.True(SecurityCenterState.IsMicrosoftProduct("Something", "windowsdefender://"));
        Assert.False(SecurityCenterState.IsMicrosoftProduct("Norton 360", @"C:\Program Files\Norton\wsc_proxy.exe"));
    }

    [Theory]
    [InlineData(1, 2, 1, UacLevel.AlwaysNotify)]
    [InlineData(1, 5, 1, UacLevel.Default)]
    [InlineData(1, 5, 0, UacLevel.NoDim)]
    [InlineData(1, 0, 0, UacLevel.NeverNotify)]
    [InlineData(0, 5, 1, UacLevel.Off)]
    [InlineData(1, 1, 1, UacLevel.Custom)]
    [InlineData(null, null, null, UacLevel.Default)]
    public void Uac_values_map_to_the_slider_levels(int? lua, int? consent, int? secure, UacLevel expected) =>
        Assert.Equal(expected, UacLevels.Map(lua, consent, secure));

    [Fact]
    public void Uac_status_is_red_for_never_notify_and_off()
    {
        Assert.Equal(HealthStatus.Good, UacLevels.Status(UacLevel.Default));
        Assert.Equal(HealthStatus.Warning, UacLevels.Status(UacLevel.NoDim));
        Assert.Equal(HealthStatus.Critical, UacLevels.Status(UacLevel.NeverNotify));
        Assert.Equal(HealthStatus.Critical, UacLevels.Status(UacLevel.Off));
        Assert.Equal(HealthStatus.Unknown, UacLevels.Status(UacLevel.Unknown));
    }

    [Theory]
    [InlineData(1, "Activated", HealthStatus.Good)]
    [InlineData(0, "Not activated", HealthStatus.Critical)]
    [InlineData(2, "Not activated yet (first grace period)", HealthStatus.Warning)]
    [InlineData(3, "Needs activating again (hardware changed)", HealthStatus.Warning)]
    [InlineData(4, "Not genuine (grace period)", HealthStatus.Critical)]
    [InlineData(5, "Not activated (reminders are showing)", HealthStatus.Critical)]
    [InlineData(6, "Not activated yet (extended grace period)", HealthStatus.Warning)]
    [InlineData(null, "Unknown", HealthStatus.Unknown)]
    public void Activation_status_maps_to_words(int? status, string text, HealthStatus health)
    {
        Assert.Equal(text, ActivationStatus.Describe(status));
        Assert.Equal(health, ActivationStatus.Status(status));
    }

    [Fact]
    public void Activation_query_targets_the_windows_licence_with_a_key()
    {
        Assert.Contains("55c92734-d682-4d71-983e-d6ec3f16059f", ActivationStatus.Query);
        Assert.Contains("PartialProductKey IS NOT NULL", ActivationStatus.Query);
    }

    [Theory]
    [InlineData(26100, "24H2", "Professional", 2026, 10, 13)]
    [InlineData(26100, "24H2", "Core", 2026, 10, 13)]
    [InlineData(26100, "24H2", "Enterprise", 2027, 10, 12)]
    [InlineData(26200, "25H2", "Professional", 2027, 10, 12)]
    [InlineData(22631, "23H2", "CoreSingleLanguage", 2025, 11, 11)]
    [InlineData(22631, "23H2", "Education", 2026, 11, 10)]
    [InlineData(22621, "22H2", "Professional", 2024, 10, 8)]
    [InlineData(19045, "22H2", "Professional", 2025, 10, 14)]
    [InlineData(22631, null, "Professional", 2025, 11, 11)] // version from the build number
    public void Servicing_table_knows_the_end_dates(int build, string? version, string edition, int y, int m, int d)
    {
        var info = WindowsServicing.Lookup(build, version, edition);
        Assert.Equal(new DateOnly(y, m, d), info.EndOfServicing);
    }

    [Fact]
    public void Servicing_table_says_unknown_instead_of_guessing()
    {
        Assert.False(WindowsServicing.Lookup(26100, "24H2", "EnterpriseS").IsKnown); // LTSC has its own dates
        Assert.False(WindowsServicing.Lookup(27000, "26H1", "Professional").IsKnown);
        Assert.False(WindowsServicing.Lookup(26100, "24H2", "ServerDatacenter").IsKnown);
        Assert.Equal("Windows 11 24H2", WindowsServicing.Lookup(26100, "24h2", "Core").VersionName);
        Assert.Equal("Windows 10 22H2", WindowsServicing.Lookup(19045, null, "Core").VersionName);
    }

    [Fact]
    public void Servicing_status_turns_yellow_then_red()
    {
        var info = WindowsServicing.Lookup(26200, "25H2", "Professional"); // ends 12 Oct 2027
        Assert.Equal(HealthStatus.Good, WindowsServicing.Status(info, new DateOnly(2026, 10, 28)));
        Assert.Equal(HealthStatus.Warning, WindowsServicing.Status(info, new DateOnly(2027, 9, 1)));
        Assert.Equal(HealthStatus.Good, WindowsServicing.Status(info, new DateOnly(2027, 7, 13))); // 91 days before
        Assert.Equal(HealthStatus.Warning, WindowsServicing.Status(info, new DateOnly(2027, 10, 12))); // last day
        Assert.Equal(HealthStatus.Critical, WindowsServicing.Status(info, new DateOnly(2027, 10, 13)));
        Assert.Equal(HealthStatus.Unknown, WindowsServicing.Status(WindowsServicing.Lookup(26100, "24H2", null), new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void Score_takes_full_weight_for_red_and_half_for_yellow()
    {
        var checks = new List<SecurityCheck>
        {
            Check(SecurityCheckIds.Antivirus, HealthStatus.Good),
            Check(SecurityCheckIds.Smb1, HealthStatus.Critical),          // -10
            Check(SecurityCheckIds.MemoryIntegrity, HealthStatus.Warning), // -3 (5 halved, rounded up)
            Check(SecurityCheckIds.Tpm, HealthStatus.Unknown),             // unknown costs nothing
        };
        var score = SecurityScoring.Score(checks);
        Assert.Equal(87, score.Value);
        Assert.Equal(HealthStatus.Critical, score.Status); // any red item means "Needs attention"
        Assert.Equal("Needs attention", score.Grade);
    }

    [Fact]
    public void Score_is_never_negative_and_all_green_is_well_protected()
    {
        var allRed = SecurityCheckBuilderIds().Select(id => Check(id, HealthStatus.Critical)).ToList();
        Assert.Equal(0, SecurityScoring.Score(allRed).Value);

        var allGreen = SecurityCheckBuilderIds().Select(id => Check(id, HealthStatus.Good)).ToList();
        var good = SecurityScoring.Score(allGreen);
        Assert.Equal(100, good.Value);
        Assert.Equal("Well protected", good.Grade);
        Assert.Empty(good.TopFixes);
    }

    [Fact]
    public void Top_fixes_put_red_before_yellow_and_heaviest_first()
    {
        var checks = new List<SecurityCheck>
        {
            Check(SecurityCheckIds.Firewall, HealthStatus.Warning),     // 20, yellow
            Check(SecurityCheckIds.Guest, HealthStatus.Critical),       // 5, red
            Check(SecurityCheckIds.Smb1, HealthStatus.Critical),        // 10, red
            Check(SecurityCheckIds.Encryption, HealthStatus.Warning),   // 8, yellow
            Check(SecurityCheckIds.Antivirus, HealthStatus.Good),
        };
        var top = SecurityScoring.TopFixes(checks);
        Assert.Equal([SecurityCheckIds.Smb1, SecurityCheckIds.Guest, SecurityCheckIds.Firewall], top.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Builder_shows_third_party_antivirus_instead_of_defender()
    {
        var facts = new SecurityFacts
        {
            Antivirus = [new SecurityProduct("Windows Defender", 0x060100, true), new SecurityProduct("ESET Security", 0x041000, false)],
            Defender = new DefenderStatus { AntivirusEnabled = false, RunningMode = "Passive Mode" },
        };
        var check = SecurityCheckBuilder.Antivirus(facts, Now);
        Assert.Equal(HealthStatus.Good, check.Status);
        Assert.Contains("ESET Security", check.Summary);
        Assert.Equal(SecurityFix.None, check.Fix);
    }

    [Fact]
    public void Builder_falls_back_to_security_center_when_defender_cannot_be_read()
    {
        var on = SecurityCheckBuilder.Antivirus(new SecurityFacts { Antivirus = [new SecurityProduct("Windows Defender", 0x061100, true)] }, Now);
        Assert.Equal(HealthStatus.Good, on.Status);

        var none = SecurityCheckBuilder.Antivirus(new SecurityFacts { Antivirus = [new SecurityProduct("Windows Defender", 0x060100, true)] }, Now);
        Assert.Equal(HealthStatus.Critical, none.Status);

        Assert.Equal(HealthStatus.Unknown, SecurityCheckBuilder.Antivirus(new SecurityFacts(), Now).Status);
    }

    [Fact]
    public void Builder_flags_old_definitions_and_offers_the_update()
    {
        var facts = new SecurityFacts
        {
            Defender = new DefenderStatus { AntivirusEnabled = true, RealTimeProtection = true, TamperProtection = true, CloudProtection = true, DefinitionAgeDays = 9 },
        };
        var check = SecurityCheckBuilder.Antivirus(facts, Now);
        Assert.Equal(HealthStatus.Critical, check.Status);
        Assert.Equal(SecurityFix.UpdateDefinitions, check.Fix);

        var off = SecurityCheckBuilder.Antivirus(new SecurityFacts { Defender = new DefenderStatus { AntivirusEnabled = true, RealTimeProtection = false, DefinitionAgeDays = 0 } }, Now);
        Assert.Equal(HealthStatus.Critical, off.Status);
        Assert.Contains("real-time protection is off", off.Summary);
    }

    [Fact]
    public void Builder_offers_to_turn_the_firewall_back_on_only_for_profiles_that_are_off()
    {
        var facts = new SecurityFacts
        {
            FirewallProfiles = [new("Domain", true), new("Private", false), new("Public", false)],
        };
        var check = SecurityCheckBuilder.Firewall(facts);
        Assert.Equal(HealthStatus.Critical, check.Status);
        Assert.Equal(SecurityFix.TurnOnFirewall, check.Fix);
        Assert.Equal("Private,Public", check.FixTarget);

        var domainOnly = SecurityCheckBuilder.Firewall(new SecurityFacts { FirewallProfiles = [new("Domain", false), new("Private", true), new("Public", true)] });
        Assert.Equal(HealthStatus.Warning, domainOnly.Status);

        var thirdParty = SecurityCheckBuilder.Firewall(facts with { Firewalls = [new SecurityProduct("ZoneAlarm Firewall", 0x011000, false)] });
        Assert.Equal(HealthStatus.Good, thirdParty.Status);
        Assert.Equal(SecurityFix.None, thirdParty.Fix);
    }

    [Fact]
    public void Unknown_facts_give_grey_items_that_do_not_cost_points()
    {
        var checks = SecurityCheckBuilder.Build(new SecurityFacts { EncryptionAvailable = false }, Now);
        Assert.Equal(16, checks.Count);
        Assert.Equal(checks.Count, checks.Select(c => c.Id).Distinct().Count());
        var score = SecurityScoring.Score(checks.Where(c => c.Status == HealthStatus.Unknown).ToList());
        Assert.Equal(100, score.Value);
    }

    [Theory]
    [InlineData(1, 1, HealthStatus.Good)]
    [InlineData(0, 0, HealthStatus.Warning)]
    [InlineData(0, 1, HealthStatus.Warning)] // encrypted, protection paused
    [InlineData(0, 2, HealthStatus.Good)]    // encrypting now
    [InlineData(0, 3, HealthStatus.Warning)] // decrypting
    public void Encryption_status_rules(int protection, int conversion, HealthStatus expected) =>
        Assert.Equal(expected, SecurityCheckBuilder.EncryptionStatus(protection, conversion));

    [Fact]
    public void Value_rules_read_numbers_not_translated_text()
    {
        Assert.False(SecurityValueRules.SmartScreenApps(0, "Warn"));    // policy wins
        Assert.True(SecurityValueRules.SmartScreenApps(null, null));    // Windows default
        Assert.False(SecurityValueRules.SmartScreenApps(null, "Off"));
        Assert.Null(SecurityValueRules.SmartScreenEdge(false, null, null));
        Assert.False(SecurityValueRules.SmartScreenEdge(true, null, 0));
        Assert.True(SecurityValueRules.IsGuestSid("S-1-5-21-1004336348-1177238915-682003330-501"));
        Assert.False(SecurityValueRules.IsGuestSid("S-1-5-21-1004336348-1177238915-682003330-1501"));
        Assert.True(SecurityValueRules.RemoteDesktopOn(0));
        Assert.False(SecurityValueRules.RemoteDesktopOn(1));
        Assert.True(SecurityValueRules.FirewallEnabled(2));
        Assert.Equal(true, SecurityValueRules.IsRunning([2u], SecurityValueRules.MemoryIntegrityService));
        Assert.Equal(false, SecurityValueRules.IsRunning([2u], SecurityValueRules.CredentialGuardService));
        Assert.True(SecurityValueRules.OptionalFeatureOn(1));
        Assert.False(SecurityValueRules.OptionalFeatureOn(3));
    }

    [Fact]
    public void Defender_codes_and_ages_are_plain_words()
    {
        Assert.Equal("Quarantined", DefenderText.ThreatStatus(3));
        Assert.Equal("Severe", DefenderText.Severity(5));
        Assert.True(DefenderText.NeedsAttention(103));
        Assert.False(DefenderText.NeedsAttention(3));
        Assert.Null(DefenderText.Age(uint.MaxValue));
        Assert.Equal(2, DefenderText.Age(2));
        Assert.Equal("never", DefenderText.Ago(null, Now));
        Assert.Equal("yesterday", DefenderText.Ago(Now.AddDays(-1), Now));
        Assert.Equal(@"C:\Users\a\x.exe", DefenderText.CleanResource(@"file:_C:\Users\a\x.exe"));
        Assert.Equal(HealthStatus.Warning, DefenderText.DefinitionsStatus(4));
    }

    [Fact]
    public void Only_windows_settings_security_and_known_tools_can_be_opened()
    {
        Assert.True(SecurityLinks.IsAllowed(SecurityLinks.Firewall));
        Assert.True(SecurityLinks.IsAllowed("ms-settings:activation"));
        Assert.True(SecurityLinks.IsAllowed(SecurityLinks.UacSettings));
        Assert.False(SecurityLinks.IsAllowed(@"C:\Users\x\Downloads\evil.exe"));
        Assert.False(SecurityLinks.IsAllowed("https://example.com"));
        Assert.Equal("windows-update", SecurityLinks.AppPage(SecurityLinks.WindowsUpdatePage));
    }

    [Fact]
    public async Task Demo_service_fills_every_check()
    {
        var demo = new DemoSecurityStatusService();
        var facts = await demo.ReadAsync(CancellationToken.None);
        var checks = SecurityCheckBuilder.Build(facts, Now);
        Assert.All(checks.Where(c => c.Id != SecurityCheckIds.CredentialGuard), c => Assert.NotEqual(HealthStatus.Unknown, c.Status));
        Assert.NotEmpty(await demo.GetRecentThreatsAsync(CancellationToken.None));
        Assert.True((await demo.TurnOffSmb1Async(CancellationToken.None)).Success);
        Assert.False((await demo.ReadAsync(CancellationToken.None)).Smb1Enabled);
    }

    private static SecurityCheck Check(string id, HealthStatus status) => new(id, id, status, string.Empty, string.Empty, null);

    private static IEnumerable<string> SecurityCheckBuilderIds() =>
        SecurityCheckBuilder.Build(new SecurityFacts(), Now).Select(c => c.Id);
}
