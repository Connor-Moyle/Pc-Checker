using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Privacy;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Privacy page rules: the consent store's FILETIMEs, desktop app paths, Allow/Deny values and quick status.</summary>
public class PrivacyTests
{
    [Fact]
    public void Filetime_values_become_utc_times()
    {
        var expected = new DateTimeOffset(2026, 10, 27, 18, 30, 0, TimeSpan.Zero);
        long raw = expected.ToFileTime();
        Assert.Equal(expected, ConsentStoreParser.FromFileTime(raw));
        Assert.Equal(expected, ConsentStoreParser.FromFileTime(BitConverter.GetBytes(raw)));
        Assert.Null(ConsentStoreParser.FromFileTime(0L));
        Assert.Null(ConsentStoreParser.FromFileTime(-5L));
        Assert.Null(ConsentStoreParser.FromFileTime(null));
        Assert.Null(ConsentStoreParser.FromFileTime("not a number"));
    }

    [Fact]
    public void A_zero_stop_time_means_in_use_now()
    {
        long start = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero).ToFileTime();
        Assert.True(ConsentStoreParser.IsInUse(start, 0L));
        Assert.True(ConsentStoreParser.IsInUse(start, null));
        Assert.False(ConsentStoreParser.IsInUse(start, start + TimeSpan.FromMinutes(5).Ticks));
        Assert.False(ConsentStoreParser.IsInUse(0L, 0L)); // never used
    }

    [Fact]
    public void Duration_is_stop_minus_start_and_unknown_while_in_use()
    {
        var start = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(47), ConsentStoreParser.Duration(start, start.AddMinutes(47), false));
        Assert.Null(ConsentStoreParser.Duration(start, null, true));
        Assert.Null(ConsentStoreParser.Duration(start, start.AddMinutes(-1), false));
        Assert.Equal("1 h 5 min", ConsentStoreParser.FormatDuration(TimeSpan.FromMinutes(65)));
        Assert.Equal("48 s", ConsentStoreParser.FormatDuration(TimeSpan.FromSeconds(48)));
        Assert.Equal("—", ConsentStoreParser.FormatDuration(null));
    }

    [Theory]
    [InlineData("C:#Program Files#Zoom#bin#Zoom.exe", @"C:\Program Files\Zoom\bin\Zoom.exe", "Zoom")]
    [InlineData(@"C:#Users#Alex#AppData#Local#Discord#app-1.0.9188#Discord.exe", @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9188\Discord.exe", "Discord")]
    [InlineData(@"\\?#C:#Tools#cam.v2.exe", @"\\?\C:\Tools\cam.v2.exe", "cam.v2")]
    public void NonPackaged_key_names_decode_to_paths(string key, string path, string name)
    {
        var decoded = ConsentStoreParser.DecodeNonPackagedPath(key);
        Assert.Equal(path, decoded);
        Assert.Equal(name, ConsentStoreParser.ProgramName(decoded));
    }

    [Theory]
    [InlineData("Allow", ConsentValue.Allow)]
    [InlineData("deny", ConsentValue.Deny)]
    [InlineData(" Prompt ", ConsentValue.Prompt)]
    [InlineData(null, ConsentValue.Unknown)]
    [InlineData("Erlauben", ConsentValue.Unknown)]
    public void Capability_values_are_parsed(string? raw, ConsentValue expected) =>
        Assert.Equal(expected, ConsentStoreParser.ParseValue(raw));

    [Fact]
    public void Access_text_reads_the_device_switch_first()
    {
        Assert.Equal("Off for the whole device", ConsentStoreParser.AccessText(ConsentValue.Deny, ConsentValue.Allow, ConsentValue.Allow));
        Assert.Equal("Off for all apps", ConsentStoreParser.AccessText(ConsentValue.Allow, ConsentValue.Deny, ConsentValue.Allow));
        Assert.Equal("On for Store apps, off for desktop apps", ConsentStoreParser.AccessText(ConsentValue.Unknown, ConsentValue.Allow, ConsentValue.Deny));
        Assert.Equal("On", ConsentStoreParser.AccessText(ConsentValue.Unknown, ConsentValue.Unknown, ConsentValue.Unknown));
        Assert.Equal("Deny", ConsentStoreParser.Format(ConsentValue.Deny));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConsentStoreParser.Format(ConsentValue.Unknown));
    }

    [Fact]
    public void Only_store_apps_that_still_have_access_can_be_revoked()
    {
        var store = App("Microsoft.WindowsCamera_8wekyb3d8bbwe", true, ConsentValue.Allow);
        Assert.True(store.CanRevoke);
        Assert.False((store with { Value = ConsentValue.Deny }).CanRevoke);
        Assert.False(App("C:#Apps#cam.exe", false, ConsentValue.Allow).CanRevoke);
    }

    [Fact]
    public void Merge_keeps_one_row_per_app_with_in_use_first()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            App("A_1", true, ConsentValue.Allow, now.AddDays(-2)),
            App("A_1", true, ConsentValue.Allow, now.AddHours(-1), ConsentScope.Machine),
            App("B_1", true, ConsentValue.Allow, now.AddMinutes(-5), inUse: true),
            App("C:#x.exe", false, ConsentValue.Allow, now.AddHours(-3)),
        };
        var merged = ConsentStoreParser.Merge(records);
        Assert.Equal(["B_1", "A_1", "C:#x.exe"], merged.Select(r => r.KeyName).ToArray());
        Assert.Equal(ConsentScope.Machine, merged[1].Scope); // the newer record of A wins
    }

    [Theory]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Camera")]
    [InlineData("MSTeams_8wekyb3d8bbwe", "Microsoft Teams")]
    [InlineData("Contoso.VideoChatPro_abc123", "Video Chat Pro")]
    [InlineData("NoUnderscore", "No Underscore")]
    public void Store_app_names_have_a_readable_fallback(string family, string expected) =>
        Assert.Equal(expected, PackagedAppNames.FriendlyName(family));

    [Theory]
    [InlineData(PrivacyQuickStatusRules.ActivityHistory, TweakStatus.Applied, "Off", HealthStatus.Good)]
    [InlineData(PrivacyQuickStatusRules.ActivityHistory, TweakStatus.NotApplied, "Not turned off for the whole PC", HealthStatus.Warning)]
    [InlineData(PrivacyQuickStatusRules.AdvertisingId, TweakStatus.NotApplied, "On", HealthStatus.Warning)]
    [InlineData(PrivacyQuickStatusRules.AdvertisingId, TweakStatus.Partial, "Partly off", HealthStatus.Warning)]
    [InlineData(PrivacyQuickStatusRules.DiagnosticData, TweakStatus.Applied, "Required data only", HealthStatus.Good)]
    [InlineData(PrivacyQuickStatusRules.DiagnosticData, TweakStatus.NotApplied, "Optional data may be sent", HealthStatus.Warning)]
    [InlineData(PrivacyQuickStatusRules.DiagnosticData, TweakStatus.Unknown, "Could not be checked", HealthStatus.Unknown)]
    public void Quick_status_comes_from_the_debloat_tweak_state(string id, TweakStatus status, string text, HealthStatus health)
    {
        var quick = PrivacyQuickStatusRules.Map(id, status);
        Assert.Equal(text, quick.StateText);
        Assert.Equal(health, quick.Status);
    }

    [Fact]
    public void Quick_status_tweaks_exist_in_the_debloat_catalog()
    {
        var ids = DebloatCatalog.All.Select(t => t.Id).ToHashSet();
        Assert.All(PrivacyQuickStatusRules.TweakIds, id => Assert.Contains(id, ids));
    }

    [Fact]
    public async Task Demo_privacy_service_fills_all_three_capabilities_and_revokes()
    {
        var demo = new DemoPrivacyService();
        var snapshot = await demo.ReadAsync(CancellationToken.None);
        Assert.Equal(3, snapshot.Capabilities.Count);
        Assert.All(snapshot.Capabilities, c => Assert.NotEmpty(c.Apps));
        Assert.Contains(snapshot.Capabilities, c => c.InUseNow);

        var teams = snapshot.Capabilities[0].Apps.First(a => a.CanRevoke);
        Assert.True((await demo.SetAppAccessAsync(teams, ConsentValue.Deny, CancellationToken.None)).Success);
        var after = await demo.ReadAsync(CancellationToken.None);
        Assert.Equal(ConsentValue.Deny, after.Capabilities[0].Apps.First(a => a.KeyName == teams.KeyName).Value);
    }

    private static CapabilityAppUsage App(string key, bool packaged, ConsentValue value, DateTimeOffset? start = null,
        ConsentScope scope = ConsentScope.CurrentUser, bool inUse = false) =>
        new(CapabilityKind.Camera, key, key, packaged ? null : ConsentStoreParser.DecodeNonPackagedPath(key), packaged,
            start ?? DateTimeOffset.UnixEpoch, inUse ? null : (start ?? DateTimeOffset.UnixEpoch).AddMinutes(1), inUse, value, scope);
}
