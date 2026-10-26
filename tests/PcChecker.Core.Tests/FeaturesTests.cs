using System.Text.Json;
using PcChecker.Core.Optimization;
using PcChecker.Core.WindowsFeatures;
using Xunit;

namespace PcChecker.Core.Tests;

public class FeatureStateParserTests
{
    [Theory]
    [InlineData("Enabled", FeatureState.Enabled)]
    [InlineData("enabled", FeatureState.Enabled)]
    [InlineData("EnablePending", FeatureState.Enabled)]
    [InlineData("Disabled", FeatureState.Disabled)]
    [InlineData("DisabledWithPayloadRemoved", FeatureState.Disabled)]
    [InlineData("DisablePending", FeatureState.Disabled)]
    [InlineData("NotPresent", FeatureState.NotPresent)]
    [InlineData("2", FeatureState.Enabled)]
    [InlineData("1", FeatureState.Disabled)]
    [InlineData("0", FeatureState.Unknown)]
    [InlineData("", FeatureState.Unknown)]
    [InlineData(null, FeatureState.Unknown)]
    [InlineData("Banana", FeatureState.Unknown)]
    public void Optional_feature_state_accepts_names_and_numbers(string? input, FeatureState expected) =>
        Assert.Equal(expected, FeatureStateParser.ParseFeatureState(input));

    [Theory]
    [InlineData("Installed", FeatureState.Enabled)]
    [InlineData("InstallPending", FeatureState.Enabled)]
    [InlineData("Staged", FeatureState.Disabled)]
    [InlineData("NotPresent", FeatureState.NotPresent)]
    [InlineData("4", FeatureState.Enabled)]
    [InlineData("0", FeatureState.NotPresent)]
    [InlineData("2", FeatureState.Disabled)]
    [InlineData("99", FeatureState.Unknown)]
    public void Capability_state_accepts_names_and_numbers(string? input, FeatureState expected) =>
        Assert.Equal(expected, FeatureStateParser.ParseCapabilityState(input));

    [Fact]
    public void State_text_reads_numbers_strings_and_booleans_from_json()
    {
        using var doc = JsonDocument.Parse("""{"n":2,"s":"Enabled","b":true,"x":null}""");
        Assert.Equal("2", FeatureStateParser.StateText(doc.RootElement.GetProperty("n")));
        Assert.Equal("Enabled", FeatureStateParser.StateText(doc.RootElement.GetProperty("s")));
        Assert.Equal("Enabled", FeatureStateParser.StateText(doc.RootElement.GetProperty("b")));
        Assert.Null(FeatureStateParser.StateText(doc.RootElement.GetProperty("x")));
        Assert.True(FeatureStateParser.ParseBool(doc.RootElement.GetProperty("b")));
        Assert.True(FeatureStateParser.ParseBool(doc.RootElement.GetProperty("n")));
        Assert.False(FeatureStateParser.ParseBool(doc.RootElement.GetProperty("x")));
    }

    [Theory]
    [InlineData("""{"RestartNeeded":true,"Online":true}""", true)]
    [InlineData("""{"RestartNeeded":false,"Online":true}""", false)]
    [InlineData("Path          :\nOnline        : True\nRestartNeeded : True", true)]
    [InlineData("RestartNeeded : False", false)]
    [InlineData("The operation completed successfully. A restart is required to finish.", true)]
    [InlineData("All done.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Restart_needed_is_parsed_from_json_text_and_prose(string? output, bool expected) =>
        Assert.Equal(expected, FeatureStateParser.ParseRestartNeeded(output));
}

public class OptionalFeatureCatalogTests
{
    [Fact]
    public void Catalog_is_large_unique_and_fully_described()
    {
        Assert.True(OptionalFeatureCatalog.All.Count >= 40);
        Assert.Equal(OptionalFeatureCatalog.All.Count, OptionalFeatureCatalog.All.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(OptionalFeatureCatalog.All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.DisplayName), e.Name);
            Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Name);
            Assert.True(Enum.IsDefined(e.Risk), e.Name);
        });
    }

    [Fact]
    public void Lookup_is_case_insensitive_and_unknown_names_are_humanized()
    {
        Assert.Equal(".NET Framework 3.5 (includes 2.0 and 3.0)", OptionalFeatureCatalog.Find("netfx3")!.DisplayName);
        Assert.Equal(RiskLevel.Advanced, OptionalFeatureCatalog.Find("SMB1Protocol")!.Risk);
        Assert.Null(OptionalFeatureCatalog.Find("Contoso-Thing"));
        Assert.Equal("Contoso Thing Feature", OptionalFeatureCatalog.FriendlyName("Contoso-Thing_Feature"));
        Assert.Equal(string.Empty, OptionalFeatureCatalog.Humanize(" "));
    }

    [Fact]
    public void Dangerous_features_are_never_marked_safe()
    {
        foreach (var name in new[] { "SMB1Protocol", "SearchEngine-Client-Package", "Client-UnifiedWriteFilter" })
        {
            Assert.NotEqual(RiskLevel.Safe, OptionalFeatureCatalog.Find(name)!.Risk);
        }
    }
}

public class CapabilityCatalogTests
{
    [Fact]
    public void Exact_and_family_prefixes_resolve()
    {
        Assert.Equal("OpenSSH Client", CapabilityCatalog.FriendlyName("OpenSSH.Client~~~~0.0.1.0"));
        Assert.Equal(RiskLevel.Moderate, CapabilityCatalog.Find("OpenSSH.Server~~~~0.0.1.0")!.Risk);
        Assert.Equal(CapabilityCatalog.GroupLanguage, CapabilityCatalog.GroupFor("Language.Basic~~~en-GB~0.0.1.0"));
        Assert.Equal(CapabilityCatalog.GroupRsat, CapabilityCatalog.GroupFor("Rsat.Dns.Tools~~~~0.0.1.0"));
        Assert.Equal(CapabilityCatalog.GroupOther, CapabilityCatalog.GroupFor("Contoso.Thing~~~~1.0"));
    }

    [Fact]
    public void Friendly_names_for_families_include_the_component_and_language()
    {
        Assert.Equal("Language component: Basic (en-GB)", CapabilityCatalog.FriendlyName("Language.Basic~~~en-GB~0.0.1.0"));
        Assert.Equal("Remote Server Administration Tools: Dns.Tools", CapabilityCatalog.FriendlyName("Rsat.Dns.Tools~~~~0.0.1.0"));
        Assert.Equal("Contoso Thing", CapabilityCatalog.FriendlyName("Contoso.Thing~~~~1.0"));
        Assert.Equal("OpenSSH.Client", CapabilityCatalog.BaseName("OpenSSH.Client~~~~0.0.1.0"));
        Assert.Equal(string.Empty, CapabilityCatalog.BaseName(null));
        Assert.Null(CapabilityCatalog.LanguageTag("OpenSSH.Client~~~~0.0.1.0"));
        Assert.Equal("de-DE", CapabilityCatalog.LanguageTag("Language.Speech~~~de-DE~0.0.1.0"));
    }

    [Fact]
    public void Catalog_entries_are_unique_and_described()
    {
        Assert.Equal(CapabilityCatalog.All.Count, CapabilityCatalog.All.Select(e => e.Prefix).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(CapabilityCatalog.All, e => Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Prefix));
    }
}

public class FeatureMapperTests
{
    private static List<T> Rows<T>(string json) => JsonSerializer.Deserialize<List<T>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void Optional_features_are_mapped_deduplicated_and_sorted_with_unknowns_as_advanced()
    {
        var rows = Rows<OptionalFeatureRow>("""
            [{"FeatureName":"NetFx3","State":2},{"FeatureName":"NetFx3","State":"Disabled"},
             {"FeatureName":"Contoso-Mystery","State":"Enabled"},{"FeatureName":" ","State":2},{"FeatureName":"TelnetClient","State":"Disabled"}]
            """);
        var mapped = FeatureMapper.MapOptionalFeatures(rows);

        Assert.Equal(3, mapped.Count);
        var netfx = Assert.Single(mapped, f => f.Name == "NetFx3");
        Assert.Equal(FeatureState.Enabled, netfx.State);
        Assert.Equal(FeatureKind.OptionalFeature, netfx.Kind);
        Assert.False(netfx.AdvancedOnly);
        var mystery = Assert.Single(mapped, f => f.Name == "Contoso-Mystery");
        Assert.True(mystery.AdvancedOnly);
        Assert.Equal(RiskLevel.Moderate, mystery.Risk);
        Assert.Equal(FeatureMapper.UnknownFeatureDescription, mystery.Description);
        Assert.Equal("Contoso-Mystery", mapped[^1].Name); // advanced items sort last
    }

    [Fact]
    public void Capabilities_skip_not_present_unless_asked()
    {
        var rows = Rows<CapabilityRow>("""
            [{"Name":"OpenSSH.Client~~~~0.0.1.0","State":"Installed"},{"Name":"XPS.Viewer~~~~0.0.1.0","State":"NotPresent"},{"Name":"OpenSSH.Server~~~~0.0.1.0","State":0}]
            """);
        var mapped = FeatureMapper.MapCapabilities(rows);
        var single = Assert.Single(mapped);
        Assert.Equal("OpenSSH Client", single.DisplayName);
        Assert.Equal(FeatureState.Enabled, single.State);

        Assert.Equal(3, FeatureMapper.MapCapabilities(rows, includeNotPresent: true).Count);
        Assert.Empty(FeatureMapper.MapCapabilities(null));
    }

    [Fact]
    public void Inbox_apps_skip_frameworks_respect_nonremovable_and_flag_provisioned()
    {
        var rows = Rows<AppxPackageRow>("""
            [{"Name":"Microsoft.BingWeather","PackageFullName":"Microsoft.BingWeather_4.53_x64__8wekyb3d8bbwe","NonRemovable":false,"IsFramework":false},
             {"Name":"Microsoft.VCLibs.140.00","PackageFullName":"Microsoft.VCLibs.140.00_14.0_x64__8wekyb3d8bbwe","NonRemovable":false,"IsFramework":true},
             {"Name":"Microsoft.WindowsStore","PackageFullName":"Microsoft.WindowsStore_1_x64__8wekyb3d8bbwe","NonRemovable":"True","IsFramework":"False"},
             {"Name":"Microsoft.SecHealthUI","PackageFullName":"Microsoft.SecHealthUI_1_x64__8wekyb3d8bbwe","NonRemovable":false,"IsFramework":false},
             {"Name":"Contoso.Unknown","PackageFullName":"Contoso.Unknown_1_x64__abc","NonRemovable":false,"IsFramework":false},
             {"Name":"Microsoft.BingWeather","PackageFullName":"Microsoft.BingWeather_4.54_x64__8wekyb3d8bbwe","NonRemovable":false,"IsFramework":false}]
            """);
        var provisioned = Rows<ProvisionedPackageRow>("""[{"DisplayName":"Microsoft.BingWeather","PackageName":"Microsoft.BingWeather_4.53_neutral_~_8wekyb3d8bbwe"}]""");

        var mapped = FeatureMapper.MapInboxApps(rows, provisioned);

        Assert.Equal(4, mapped.Count);
        Assert.DoesNotContain(mapped, f => f.Name.StartsWith("Microsoft.VCLibs"));

        var weather = Assert.Single(mapped, f => f.Name == "Microsoft.BingWeather");
        Assert.True(weather.Removable);
        Assert.Equal("Weather", weather.DisplayName);
        Assert.Equal(FeatureMapper.ProvisionedNote, weather.Recommendation);
        Assert.Equal("Microsoft.BingWeather_4.53_x64__8wekyb3d8bbwe", weather.PackageFullName);

        Assert.False(Assert.Single(mapped, f => f.Name == "Microsoft.WindowsStore").Removable);
        var security = Assert.Single(mapped, f => f.Name == "Microsoft.SecHealthUI");
        Assert.False(security.Removable);
        Assert.True(security.AdvancedOnly);

        var unknown = Assert.Single(mapped, f => f.Name == "Contoso.Unknown");
        Assert.True(unknown.Removable);
        Assert.True(unknown.AdvancedOnly);
        Assert.Equal("Unknown", unknown.DisplayName);
        Assert.Null(unknown.Recommendation);
    }

    [Fact]
    public void Removability_rule_blocks_nonremovable_and_never_remove_packages()
    {
        Assert.True(InboxAppCatalog.IsRemovable("Microsoft.BingWeather", nonRemovable: false));
        Assert.False(InboxAppCatalog.IsRemovable("Microsoft.BingWeather", nonRemovable: true));
        Assert.False(InboxAppCatalog.IsRemovable("Microsoft.WindowsStore_1.0_x64__8wekyb3d8bbwe", nonRemovable: false));
        Assert.False(InboxAppCatalog.IsRemovable("microsoft.desktopappinstaller", nonRemovable: false));
        Assert.False(InboxAppCatalog.IsRemovable(null, nonRemovable: false));
        Assert.All(InboxAppCatalog.NeverRemove, name => Assert.Equal(RiskLevel.Advanced, InboxAppCatalog.RiskFor(name)));
    }

    [Fact]
    public void Labels_describe_risk_state_and_action()
    {
        Assert.Equal("Safe to remove", FeatureLabels.RiskText(RiskLevel.Safe, FeatureKind.InboxApp));
        Assert.Equal("Not recommended", FeatureLabels.RiskText(RiskLevel.Advanced, FeatureKind.InboxApp));
        Assert.Equal("Think twice", FeatureLabels.RiskText(RiskLevel.Moderate, FeatureKind.OptionalFeature));
        Assert.Equal("Installed", FeatureLabels.StateText(FeatureState.Enabled, FeatureKind.Capability));
        Assert.Equal("Not installed", FeatureLabels.StateText(FeatureState.Disabled, FeatureKind.Capability));
        Assert.Equal("Removed", FeatureLabels.StateText(FeatureState.NotPresent, FeatureKind.InboxApp));

        Assert.Equal("Enable", FeatureLabels.ActionVerb(FeatureMapper.MapOptionalFeature("NetFx3", FeatureState.Disabled)));
        Assert.Equal("Disable", FeatureLabels.ActionVerb(FeatureMapper.MapOptionalFeature("NetFx3", FeatureState.Enabled)));
        Assert.Equal("Install", FeatureLabels.ActionVerb(FeatureMapper.MapCapability("XPS.Viewer~~~~0.0.1.0", FeatureState.NotPresent)));
        Assert.Equal("Remove", FeatureLabels.ActionVerb(FeatureMapper.MapInboxApp("Microsoft.BingWeather", null, false, false)));
        Assert.Null(FeatureLabels.ActionVerb(FeatureMapper.MapInboxApp("Microsoft.WindowsStore", null, true, false)));
        Assert.Null(FeatureLabels.ActionVerb(FeatureMapper.MapOptionalFeature("NetFx3", FeatureState.Unknown)));
    }
}

public class DemoWindowsFeatureServiceTests
{
    [Fact]
    public async Task Demo_lists_all_three_kinds_with_catalog_text()
    {
        var svc = new DemoWindowsFeatureService();
        var stages = new List<string>();
        var list = await svc.ListAsync(new SyncProgress(u => stages.Add(u.Stage)), CancellationToken.None);

        Assert.True(list.Count(f => f.Kind == FeatureKind.OptionalFeature) >= 20);
        Assert.True(list.Count(f => f.Kind == FeatureKind.Capability) >= 15);
        Assert.True(list.Count(f => f.Kind == FeatureKind.InboxApp) >= 30);
        Assert.Contains("Reading built-in apps", stages);
        Assert.All(list, f => Assert.False(string.IsNullOrWhiteSpace(f.Description), f.Name));
        Assert.Contains(list, f => f.Name == "Microsoft-Windows-Subsystem-Linux" && f.State == FeatureState.Enabled);
        Assert.Contains(list, f => f.Name == "ASUS-ArmouryCrate-Bridge" && f.AdvancedOnly);
    }

    [Fact]
    public async Task Demo_changes_persist_and_protect_essential_apps()
    {
        var svc = new DemoWindowsFeatureService();
        var list = await svc.ListAsync(null, CancellationToken.None);

        var netfx = list.Single(f => f.Name == "NetFx3");
        var enable = await svc.EnableAsync(netfx, CancellationToken.None);
        Assert.True(enable.Success);
        Assert.True(enable.RebootRequired);
        Assert.Equal(FeatureState.Enabled, (await svc.ListAsync(null, CancellationToken.None)).Single(f => f.Name == "NetFx3").State);

        var ssh = list.Single(f => f.Name.StartsWith("OpenSSH.Server"));
        var install = await svc.EnableAsync(ssh, CancellationToken.None);
        Assert.True(install.Success);
        Assert.False(install.RebootRequired);

        var weather = list.Single(f => f.Name == "Microsoft.BingWeather");
        Assert.True((await svc.RemoveInboxAppAsync(weather, true, CancellationToken.None)).Success);
        Assert.Equal(FeatureState.NotPresent, (await svc.ListAsync(null, CancellationToken.None)).Single(f => f.Name == "Microsoft.BingWeather").State);

        var store = list.Single(f => f.Name == "Microsoft.WindowsStore");
        Assert.False(store.Removable);
        Assert.False((await svc.RemoveInboxAppAsync(store, false, CancellationToken.None)).Success);
        Assert.False((await svc.EnableAsync(weather, CancellationToken.None)).Success);
        Assert.False((await svc.RemoveInboxAppAsync(netfx, false, CancellationToken.None)).Success);
    }

    private sealed class SyncProgress(Action<Common.ProgressUpdate> handler) : IProgress<Common.ProgressUpdate>
    {
        public void Report(Common.ProgressUpdate value) => handler(value);
    }
}
