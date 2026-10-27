using System.Text;
using PcChecker.Core.Debloat;
using PcChecker.Core.Optimization;
using PcChecker.Core.WindowsFeatures;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// The tweaks added to match current debloat tools: wildcard app names, software requirements, key removal, the
/// Game Bar link handler, AMD/NVIDIA control-panel protection, and the generated tweak tables in docs/DEBLOAT.md.
/// </summary>
public class DebloatCoverageTests
{
    private static DebloatTweak Tweak(string id) => DebloatCatalog.Find(id) ?? throw new InvalidOperationException("Missing tweak " + id);

    private static (DebloatEngine Engine, InMemoryDebloatSystem System) Create()
    {
        var system = new InMemoryDebloatSystem(DebloatCatalog.Windows11_24H2);
        return (new DebloatEngine(system, new DebloatCaptureBook()), system);
    }

    [Theory]
    [InlineData("Microsoft.BingNews", "Microsoft.BingNews", true)]
    [InlineData("Microsoft.BingNews", "microsoft.bingnews", true)]
    [InlineData("Microsoft.BingNews", "Microsoft.BingNewsPlus", false)]
    [InlineData("*.Flipboard", "Flipboard.Flipboard", true)]
    [InlineData("*.Flipboard", "Flipboard.FlipboardBeta", false)]
    [InlineData("PandoraMediaInc.*", "PandoraMediaInc.29680B314EFC2", true)]
    [InlineData("PandoraMediaInc.*", "Pandora.Media", false)]
    [InlineData("SAMSUNGELECTRONICS*Bixby*", "SAMSUNGELECTRONICSCO.LTD.Bixby", true)]
    [InlineData("SAMSUNGELECTRONICS*Bixby*", "SAMSUNGELECTRONICSCO.LTD.SamsungNotes", false)]
    [InlineData("B9ECED6F.*GlideX*", "B9ECED6F.GlideX", true)]
    [InlineData("*AB*AB", "AB", false)]
    public void Wildcard_app_names_match_like_get_appxpackage(string pattern, string name, bool expected) =>
        Assert.Equal(expected, DebloatRules.AppNameMatches(pattern, name));

    [Fact]
    public void Wildcard_names_that_are_vague_or_reach_protected_packages_are_refused()
    {
        Assert.True(InboxAppCatalog.IsRemovable("*.Flipboard", nonRemovable: false));
        Assert.False(InboxAppCatalog.IsRemovable("*Store*", nonRemovable: false));
        Assert.False(InboxAppCatalog.IsRemovable("*Radeon*", nonRemovable: false));
        Assert.False(InboxAppCatalog.IsRemovable("Microsoft.*", nonRemovable: false));
        Assert.False(InboxAppCatalog.IsRemovable("*a*", nonRemovable: false));
    }

    [Fact]
    public void Gpu_and_audio_control_panels_are_never_offered_for_removal()
    {
        var protectedPanels = new[]
        {
            "AdvancedMicroDevicesInc-2.AMDRadeonSoftware", "NVIDIACorp.NVIDIAControlPanel", "AppUp.IntelGraphicsExperience",
            "RealtekSemiconductorCorp.RealtekAudioControl", "DolbyLaboratories.DolbyAudioPremium", "WavesAudio.MaxxAudioProForDell2020",
        };
        var apps = DebloatCatalog.All.SelectMany(t => t.Operations).OfType<AppxRemovalOp>().ToList();
        foreach (var panel in protectedPanels)
        {
            Assert.False(InboxAppCatalog.IsRemovable(panel, nonRemovable: false), panel);
            Assert.DoesNotContain(apps, a => DebloatRules.AppNameMatches(a.PackageName, panel));
        }
    }

    [Fact]
    public void Catalog_app_patterns_are_specific_and_never_match_useful_or_protected_apps()
    {
        var keep = InboxAppCatalog.NeverRemove
            .Concat(["Microsoft.WindowsCalculator", "Microsoft.Windows.Photos", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.WindowsTerminal", "Microsoft.GamingApp"])
            .ToList();
        var patterns = DebloatCatalog.All.SelectMany(t => t.Operations).OfType<AppxRemovalOp>().Where(a => a.PackageName.Contains('*')).ToList();
        Assert.True(patterns.Count >= 20);
        foreach (var app in patterns)
        {
            Assert.True(app.PackageName.Replace("*", string.Empty).Length >= 5, app.PackageName);
            Assert.True(InboxAppCatalog.IsRemovable(app.PackageName, nonRemovable: false), app.PackageName);
            Assert.DoesNotContain(keep, k => DebloatRules.AppNameMatches(app.PackageName, k));
        }

        var names = DebloatCatalog.All.SelectMany(t => t.Operations).OfType<AppxRemovalOp>().Select(a => a.PackageName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Wildcard_removal_script_uses_like_and_skips_non_removable_packages()
    {
        var wildcard = WindowsFeatureService.BuildRemoveAppScript("*.Flipboard", "Flipboard", allUsers: true);
        Assert.Contains("Get-AppxPackage -AllUsers -Name '*.Flipboard' | Where-Object { -not $_.NonRemovable -and (", wildcard);
        Assert.Contains("$_.DisplayName -like '*.Flipboard' -and (", wildcard);

        // Every never-remove name is filtered per package, for installed and provisioned packages alike.
        foreach (var never in InboxAppCatalog.NeverRemove)
        {
            Assert.Contains($"$_.Name -notlike '{never}*'", wildcard);
            Assert.Contains($"$_.DisplayName -notlike '{never}*'", wildcard);
        }

        var exact = WindowsFeatureService.BuildRemoveAppScript("Microsoft.BingNews", "News", allUsers: false);
        Assert.DoesNotContain("-like", exact);
        Assert.DoesNotContain("NonRemovable", exact);
    }

    [Fact]
    public async Task Wildcard_app_rows_follow_the_installed_package()
    {
        var (engine, system) = Create();
        system.Apps.Add("Flipboard.Flipboard");
        var tweak = Tweak("apps.flipboard");
        Assert.Equal(TweakStatus.NotApplied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);

        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(tweak, default)).Outcome);
        Assert.DoesNotContain("Flipboard.Flipboard", system.Apps);
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);
    }

    [Fact]
    public async Task Tweaks_for_other_software_wait_until_it_is_installed()
    {
        var (engine, system) = Create();
        var brave = Tweak("other.brave");
        var state = (await engine.GetStatesAsync([brave], default))[brave.Id];
        Assert.Equal(TweakStatus.NotApplicable, state.Status);
        Assert.Equal("Brave is not installed", state.Detail);

        var skipped = await engine.ApplyAsync(brave, default);
        Assert.Equal(TweakOutcome.Skipped, skipped.Outcome);
        Assert.False(system.Memory.KeyExists(DebloatHive.LocalMachine, @"SOFTWARE\Policies\BraveSoftware\Brave"));

        system.Memory.CreateKey(DebloatHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe");
        Assert.Equal(TweakStatus.NotApplied, (await engine.GetStatesAsync([brave], default))[brave.Id].Status);
        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(brave, default)).Outcome);
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([brave], default))[brave.Id].Status);
        Assert.Equal(TweakOutcome.Reverted, (await engine.RevertAsync(brave, default)).Outcome);
        Assert.Null(system.Memory.Read(DebloatHive.LocalMachine, @"SOFTWARE\Policies\BraveSoftware\Brave", "BraveRewardsDisabled"));
    }

    [Fact]
    public void A_requirement_that_cannot_be_read_never_hides_a_tweak()
    {
        Assert.Null(DebloatRules.RequirementFailure(Tweak("other.chrome"), new ThrowingRegistry()));
        Assert.Equal("Google Chrome is not installed", DebloatRules.RequirementFailure(Tweak("other.chrome"), new InMemoryRegistry()));
        Assert.Null(DebloatRules.RequirementFailure(Tweak("privacy.advertisingid"), new InMemoryRegistry()));
    }

    [Fact]
    public void Requirements_name_real_keys_and_cover_every_other_app_tweak()
    {
        foreach (var tweak in DebloatCatalog.All.Where(t => t.Requires is not null))
        {
            Assert.NotEmpty(tweak.Requires!.AnyOf);
            Assert.False(string.IsNullOrWhiteSpace(tweak.Requires.MissingText), tweak.Id);
            Assert.All(tweak.Requires.AnyOf, k => Assert.False(k.Path.StartsWith('\\') || k.Path.EndsWith('\\'), tweak.Id));
        }

        foreach (var id in new[] { "other.office", "other.newoutlook", "other.brave", "other.chrome", "other.firefox", "other.nvidia", "privacy.perm.genai" })
        {
            Assert.NotNull(Tweak(id).Requires);
        }
    }

    [Fact]
    public async Task Duplicate_removable_drives_key_is_deleted_and_recreated_on_revert()
    {
        var (engine, system) = Create();
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\DelegateFolders\{F5FB2C77-0E2F-4A16-A381-3E560C68BC83}";
        const string wowKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\DelegateFolders\{F5FB2C77-0E2F-4A16-A381-3E560C68BC83}";
        system.Memory.Write(DebloatHive.LocalMachine, key, string.Empty, new RegistryData(DebloatValueKind.String, "Removable Drives"));
        var tweak = Tweak("explorer.dupdrives");
        Assert.Equal(TweakStatus.Partial, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);
        system.Memory.Write(DebloatHive.LocalMachine, wowKey, string.Empty, new RegistryData(DebloatValueKind.String, "Removable Drives"));
        Assert.Equal(TweakStatus.NotApplied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);

        var applied = await engine.ApplyAsync(tweak, default);
        Assert.Equal(TweakOutcome.Applied, applied.Outcome);
        Assert.False(system.Memory.KeyExists(DebloatHive.LocalMachine, key));
        Assert.False(system.Memory.KeyExists(DebloatHive.LocalMachine, wowKey));
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);

        Assert.Equal(TweakOutcome.Reverted, (await engine.RevertAsync(tweak, default)).Outcome);
        Assert.Equal("Removable Drives", system.Memory.Read(DebloatHive.LocalMachine, key, string.Empty)?.Value);
        Assert.Equal("Removable Drives", system.Memory.Read(DebloatHive.LocalMachine, wowKey, string.Empty)?.Value);

        // A PC without the keys has nothing to change, and a revert does not invent them.
        var (fresh, freshSystem) = Create();
        Assert.Equal(TweakStatus.Applied, (await fresh.GetStatesAsync([tweak], default))[tweak.Id].Status);
        Assert.Equal(TweakOutcome.AlreadyApplied, (await fresh.ApplyAsync(tweak, default)).Outcome);
        await fresh.RevertAsync(tweak, default);
        Assert.False(freshSystem.Memory.KeyExists(DebloatHive.LocalMachine, key), "Revert puts back what was there before Apply: the key was absent, so it stays absent.");
    }

    [Fact]
    public async Task Game_bar_link_handler_is_only_added_after_game_bar_is_removed()
    {
        var (engine, system) = Create();
        system.Apps.Add("Microsoft.XboxGamingOverlay");
        var tweak = Tweak("gaming.gamebarprotocol");
        var state = (await engine.GetStatesAsync([tweak], default))[tweak.Id];
        Assert.Equal(TweakStatus.NotApplicable, state.Status);
        Assert.StartsWith("Only needed after removing", state.Detail);
        Assert.Equal(TweakOutcome.Skipped, (await engine.ApplyAsync(tweak, default)).Outcome);
        Assert.False(system.Memory.KeyExists(DebloatHive.LocalMachine, @"SOFTWARE\Classes\ms-gamebar"));

        system.Apps.Remove("Microsoft.XboxGamingOverlay");
        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(tweak, default)).Outcome);
        var command = system.Memory.Read(DebloatHive.LocalMachine, @"SOFTWARE\Classes\ms-gamebar\shell\open\command", string.Empty);
        Assert.Equal(DebloatValueKind.ExpandString, command?.Kind);
        Assert.EndsWith("systray.exe", command?.Value);

        Assert.Equal(TweakOutcome.Reverted, (await engine.RevertAsync(tweak, default)).Outcome);
        Assert.False(system.Memory.KeyExists(DebloatHive.LocalMachine, @"SOFTWARE\Classes\ms-gamebar"));
        Assert.False(system.Memory.KeyExists(DebloatHive.LocalMachine, @"SOFTWARE\Classes\ms-gamebarservices"));
    }

    [Fact]
    public async Task Game_bar_link_handler_stays_revertible_after_game_bar_is_reinstalled()
    {
        var (engine, system) = Create();
        var tweak = Tweak("gaming.gamebarprotocol");
        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(tweak, default)).Outcome);

        // Game Bar comes back from the Store: the redirect is still in place and must stay revertible.
        system.Apps.Add("Microsoft.XboxGamingOverlay");
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);
        Assert.Equal(TweakOutcome.Reverted, (await engine.RevertAsync(tweak, default)).Outcome);
        Assert.False(system.Memory.KeyExists(DebloatHive.LocalMachine, @"SOFTWARE\Classes\ms-gamebar"));
        Assert.Equal(TweakStatus.NotApplicable, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);
    }

    [Fact]
    public async Task Policies_for_uninstalled_software_stay_revertible()
    {
        var (engine, system) = Create();
        var appPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe";
        system.Memory.CreateKey(DebloatHive.CurrentUser, appPath);
        var brave = Tweak("other.brave");
        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(brave, default)).Outcome);

        system.Memory.DeleteKeyTree(DebloatHive.CurrentUser, appPath);
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([brave], default))[brave.Id].Status);
        Assert.Equal(TweakOutcome.Reverted, (await engine.RevertAsync(brave, default)).Outcome);
        Assert.Equal(TweakStatus.NotApplicable, (await engine.GetStatesAsync([brave], default))[brave.Id].Status);
    }

    [Theory]
    [InlineData("AdvancedMicroDevicesInc-2.AMDRadeonSoftware_10.23.0.0_x64__0a9344xs7nr4m")]
    [InlineData("AdvancedMicroDevicesInc-RSXCM_22.10.0.0_x64__fhmx3h6dzfmvj")]
    [InlineData("AdvancedMicroDevicesInc-2.AMDLink")]
    public void Every_amd_package_is_protected(string package)
    {
        Assert.False(InboxAppCatalog.IsRemovable(package, nonRemovable: false));
        Assert.False(InboxAppCatalog.IsRemovable("AdvancedMicroDevices*", nonRemovable: false));
        Assert.Equal(RiskLevel.Advanced, InboxAppCatalog.Find(package)?.Risk);
    }

    [Fact]
    public async Task Num_lock_writes_the_sign_in_screen_profile_under_hkey_users()
    {
        var (engine, system) = Create();
        system.Memory.Write(DebloatHive.Users, @".DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators", new RegistryData(DebloatValueKind.String, "2147483648"));
        var tweak = Tweak("prefs.numlock");
        Assert.Contains(tweak.Operations.OfType<RegistryValueOp>(), o => o.Hive == DebloatHive.Users && o.Path.StartsWith(@".DEFAULT\", StringComparison.Ordinal));
        Assert.Contains(@"Set HKU\.DEFAULT\Control Panel\Keyboard\InitialKeyboardIndicators", tweak.Operations[0].Describe());

        await engine.ApplyAsync(tweak, default);
        Assert.Equal("2147483650", system.Memory.Read(DebloatHive.Users, @".DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators")?.Value);
        await engine.RevertAsync(tweak, default);
        Assert.Equal("2147483648", system.Memory.Read(DebloatHive.Users, @".DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators")?.Value);
    }

    [Fact]
    public void Mutually_exclusive_preferences_conflict_with_each_other()
    {
        var conflicts = DebloatRules.Conflicts(DebloatCatalog.All);
        Assert.Contains(("explorer.thispc", "explorer.launch.downloads"), conflicts);
        Assert.Contains(("prefs.dark", "prefs.light"), conflicts);
        Assert.Contains(("start.allapps.grid", "start.allapps.list"), conflicts);
        Assert.Contains(("taskbar.combine.always", "taskbar.combine.never"), conflicts);
        Assert.Contains(("wu.dolan", "wu.dohttponly"), conflicts);
        Assert.Contains(("prefs.alttab.hide", "prefs.alttab.twenty"), conflicts);
    }

    [Fact]
    public void New_tweaks_do_not_duplicate_a_setting_another_tweak_already_owns()
    {
        // Two tweaks may only share a value when they set it differently (a choice such as dark or light mode).
        var owners = new Dictionary<string, (string Id, string? Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var tweak in DebloatCatalog.All)
        {
            foreach (var op in tweak.Operations.OfType<RegistryValueOp>())
            {
                if (owners.TryGetValue(op.Key, out var owner))
                {
                    Assert.False(string.Equals(owner.Value, op.AppliedValue, StringComparison.OrdinalIgnoreCase), $"{tweak.Id} repeats {op.Key} from {owner.Id}");
                }
                else
                {
                    owners[op.Key] = (tweak.Id, op.AppliedValue);
                }
            }
        }
    }

    [Fact]
    public void Requested_fixes_extend_the_existing_tweaks()
    {
        Assert.Contains(Tweak("edge.nags").Operations.OfType<RegistryValueOp>(), o => o.Name == "ShowMicrosoftRewards");
        Assert.True(Tweak("edge.nags").Operations.Count >= 20);
        Assert.Contains(Tweak("privacy.ceiptasks").Operations.OfType<ScheduledTaskOp>(), t => t.Path.EndsWith("Microsoft Compatibility Appraiser Exp", StringComparison.Ordinal));
        Assert.DoesNotContain(DebloatCatalog.All.SelectMany(t => t.Operations).OfType<ScheduledTaskOp>(), t => t.Path.EndsWith("StartupAppTask", StringComparison.Ordinal) || t.Path.EndsWith("PcaPatchDbTask", StringComparison.Ordinal));
        Assert.Contains(Tweak("privacy.wer").Operations.OfType<ScheduledTaskOp>(), t => t.Path.EndsWith("QueueReporting", StringComparison.Ordinal));
        Assert.DoesNotContain(Tweak("privacy.wer").Operations.OfType<RegistryValueOp>(), o => o.Name == "LoggingDisabled");
        Assert.Contains(Tweak("privacy.speech").Operations.OfType<RegistryValueOp>(), o => o.Name == "AcceptedPrivacyPolicy");
        Assert.Contains(Tweak("privacy.speech").Operations.OfType<RegistryValueOp>(), o => o.Name == "AllowInputPersonalization");
        Assert.DoesNotContain(Tweak("privacy.inking").Operations.OfType<RegistryValueOp>(), o => o.Name is "AcceptedPrivacyPolicy" or "AllowInputPersonalization");
        Assert.Contains(Tweak("ai.paint").Operations.OfType<RegistryValueOp>(), o => o.Name == "DisableGenerativeErase");
        Assert.Contains(Tweak("ai.recall").Operations.OfType<RegistryValueOp>(), o => o.Name == "TurnOffSavingSnapshots");
        Assert.Contains(Tweak("ai.copilotapp").Operations.OfType<AppxRemovalOp>(), a => a.PackageName == "Microsoft.Windows.Ai.Copilot.Provider");
        Assert.Contains(Tweak("gaming.xboxservices").Operations.OfType<ScheduledTaskOp>(), t => t.Path == @"\Microsoft\XblGameSave\XblGameSaveTask");
        Assert.Contains(Tweak("svc.mapsbroker").Operations.OfType<RegistryValueOp>(), o => o.Name == "AutoUpdateEnabled");
        Assert.Contains(Tweak("ads.settings").Operations.OfType<RegistryValueOp>(), o => o.Name == "AllowOnlineTips");
        Assert.Contains(Tweak("ads.notificationsuggest").Operations.OfType<RegistryValueOp>(), o => o.Path.EndsWith("BackupReminder", StringComparison.Ordinal));
        Assert.DoesNotContain(DebloatPresets.Notes, n => n.Title.Contains("Find my device", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scripts_find_the_fax_queue_by_driver_and_keep_reserved_storage_on_small_disks()
    {
        var fax = Tweak("svc.fax").Operations.OfType<PowerShellOp>().Single();
        Assert.Contains("DriverName -eq 'Microsoft Shared Fax Driver'", fax.ApplyScript);
        Assert.Contains("Add-Printer", fax.RevertScript);
        Assert.Equal(DebloatPrecondition.NoPhysicalPrinters, Tweak("svc.fax").Precondition);

        var storage = Tweak("wu.reservedstorage").Operations.OfType<PowerShellOp>().Single();
        Assert.True(storage.ApplyScript.IndexOf("30GB", StringComparison.Ordinal) < storage.ApplyScript.IndexOf("-State Disabled", StringComparison.Ordinal));
        Assert.Contains("-State Enabled", storage.RevertScript);
        Assert.True(Tweak("wu.reservedstorage").AdvancedOnly);
    }

    [Fact]
    public void Risky_new_tweaks_stay_in_advanced_mode_with_a_warning()
    {
        foreach (var id in new[] { "installs.wpbt", "installs.coinstallers", "privacy.locationservice", "privacy.diagtrackautologger", "start.hiderecommended", "ai.fabricservice", "privacy.cdppolicy" })
        {
            var tweak = Tweak(id);
            Assert.True(tweak.AdvancedOnly, id);
        }

        Assert.All(DebloatCatalog.All.Where(t => t.Risk == RiskLevel.Advanced && t.Operations.Count > 0), t => Assert.True(t.AdvancedOnly, t.Id));
        Assert.All(DebloatCatalog.All.Where(t => t.Id.StartsWith("apps.oem.", StringComparison.Ordinal)), t =>
        {
            Assert.Equal(RiskLevel.Moderate, t.Risk);
            Assert.NotNull(t.Warning);
        });
        Assert.DoesNotContain(DebloatPresets.All.SelectMany(p => p.TweakIds), id => id.StartsWith("apps.oem.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Demo_shows_the_new_categories_populated()
    {
        var engine = new DebloatEngine(DemoDebloatService.CreateSampleSystem(), new DebloatCaptureBook());
        var states = await engine.GetStatesAsync(DebloatCatalog.All, default);
        Assert.Equal(TweakStatus.NotApplied, states["other.chrome"].Status);
        Assert.Equal(TweakStatus.NotApplicable, states["other.brave"].Status);
        Assert.Equal(TweakStatus.NotApplied, states["other.nvidia"].Status);
        Assert.Equal(TweakStatus.NotApplied, states["explorer.dupdrives"].Status);
        Assert.Equal(TweakStatus.NotApplicable, states["gaming.gamebarprotocol"].Status);
        foreach (var category in new[] { DebloatCatalog.PreferencesCategory, DebloatCatalog.OtherAppsCategory, DebloatCatalog.InstallsCategory })
        {
            Assert.Contains(DebloatCatalog.All, t => t.Category == category && states.TryGetValue(t.Id, out var s) && s.Status == TweakStatus.NotApplied);
        }
    }

    [Fact]
    public void Debloat_doc_lists_every_tweak_as_generated_from_the_catalog()
    {
        var path = Path.Combine(RepoRoot(), "docs", "DEBLOAT.md");
        var doc = File.ReadAllText(path).ReplaceLineEndings("\n");
        const string marker = "### " + DebloatCatalog.PrivacyCategory + "\n";
        int start = doc.IndexOf(marker, doc.IndexOf("## All tweaks", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(start > 0, "The All tweaks section is missing.");
        var expected = DebloatDocTables.Render();
        if (Environment.GetEnvironmentVariable("PCCHECKER_WRITE_DEBLOAT_DOCS") == "1")
        {
            File.WriteAllText(path, doc[..start] + expected);
            return;
        }

        Assert.Equal(expected, doc[start..]);
        Assert.DoesNotContain("links to them instead of changing them: Find my device", doc);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcChecker.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class ThrowingRegistry : IRegistryAccessor
    {
        public RegistryData? Read(DebloatHive hive, string path, string name) => throw new UnauthorizedAccessException();
        public bool KeyExists(DebloatHive hive, string path) => throw new UnauthorizedAccessException();
        public IReadOnlyList<string> GetSubKeyNames(DebloatHive hive, string path) => throw new UnauthorizedAccessException();
        public void Write(DebloatHive hive, string path, string name, RegistryData data) => throw new UnauthorizedAccessException();
        public void DeleteValue(DebloatHive hive, string path, string name) => throw new UnauthorizedAccessException();
        public void DeleteKeyTree(DebloatHive hive, string path) => throw new UnauthorizedAccessException();
    }
}

/// <summary>Renders the "All tweaks" tables of docs/DEBLOAT.md from the catalog.</summary>
internal static class DebloatDocTables
{
    private const int MaxOperations = 3;

    public static string Render()
    {
        var sb = new StringBuilder();
        foreach (var category in DebloatCatalog.Categories)
        {
            sb.Append("### ").Append(category).Append("\n\n");
            sb.Append("| Tweak | What it does | Offered by | Risk | Reversible |\n|---|---|---|---|---|\n");
            foreach (var tweak in DebloatCatalog.All.Where(t => t.Category == category))
            {
                sb.Append("| **").Append(Cell(tweak.Title)).Append("**<br>`").Append(tweak.Id).Append('`').Append(BuildNote(tweak)).Append(" | ");
                sb.Append(Cell(tweak.Description));
                if (tweak.DelegatesToOptimizationId is { } optimization)
                {
                    sb.Append(" (uses the Optimize action `").Append(optimization).Append("`)");
                }

                if (tweak.Requires is { } requires)
                {
                    sb.Append(" Only when installed (").Append(Cell(requires.MissingText.Replace(" is not installed", string.Empty, StringComparison.Ordinal))).Append(").");
                }

                foreach (var op in tweak.Operations.Take(MaxOperations))
                {
                    sb.Append(op is PowerShellOp ? "<br>" + Cell(op.Describe()) : "<br>`" + op.Describe().Replace("|", "\\|", StringComparison.Ordinal) + "`");
                }

                if (tweak.Operations.Count > MaxOperations)
                {
                    sb.Append("<br>… and ").Append(tweak.Operations.Count - MaxOperations).Append(" more");
                }

                sb.Append(" | ").Append(string.Join(", ", tweak.SourceTools));
                sb.Append(" | ").Append(tweak.Risk).Append(tweak.AdvancedOnly ? " (Advanced mode)" : string.Empty);
                sb.Append(" | ").Append(tweak.Reversible ? "Yes" : "No (one-way)").Append(" |\n");
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static string BuildNote(DebloatTweak tweak) => tweak switch
    {
        { MaxWindowsBuild: not null } => " · Windows 10 only",
        { MinWindowsBuild: int min } => $" · Windows 11 (build {min}+)",
        _ => string.Empty,
    };

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
