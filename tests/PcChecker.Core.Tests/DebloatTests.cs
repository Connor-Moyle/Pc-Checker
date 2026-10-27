using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Optimization;
using PcChecker.Core.Restore;
using PcChecker.Core.WindowsFeatures;
using Xunit;

namespace PcChecker.Core.Tests;

public class DebloatCatalogTests
{
    [Fact]
    public void Catalog_has_broad_coverage_with_unique_ids()
    {
        var ids = DebloatCatalog.All.Select(t => t.Id).ToList();
        Assert.True(ids.Count >= 80, $"Only {ids.Count} tweaks.");
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_tweak_has_operations_or_a_delegate_but_not_both()
    {
        foreach (var tweak in DebloatCatalog.All)
        {
            bool hasOps = tweak.Operations.Count > 0;
            bool delegates = tweak.DelegatesToOptimizationId is not null;
            Assert.True(hasOps ^ delegates, tweak.Id);
        }
    }

    [Fact]
    public void Delegated_tweaks_point_at_existing_optimization_actions()
    {
        foreach (var tweak in DebloatCatalog.All.Where(t => t.DelegatesToOptimizationId is not null))
        {
            Assert.NotNull(OptimizationCatalog.Find(tweak.DelegatesToOptimizationId!));
        }
    }

    [Fact]
    public void Every_tweak_names_known_source_tools_and_a_known_category()
    {
        foreach (var tweak in DebloatCatalog.All)
        {
            Assert.NotEmpty(tweak.SourceTools);
            Assert.All(tweak.SourceTools, s => Assert.Contains(s, DebloatSources.All));
            Assert.Contains(tweak.Category, DebloatCatalog.Categories);
            Assert.False(string.IsNullOrWhiteSpace(tweak.Title));
            Assert.False(string.IsNullOrWhiteSpace(tweak.Description));
        }

        Assert.All(DebloatCatalog.Categories, c => Assert.Contains(DebloatCatalog.All, t => t.Category == c));
    }

    [Fact]
    public void Windows11_only_tweaks_need_build_22000_or_newer()
    {
        foreach (var tweak in DebloatCatalog.All)
        {
            if (tweak.Windows11Only)
            {
                Assert.True(tweak.MinWindowsBuild >= DebloatCatalog.Windows11, tweak.Id);
            }

            if (tweak.MinWindowsBuild >= DebloatCatalog.Windows11)
            {
                Assert.True(tweak.Windows11Only, tweak.Id);
            }

            if (tweak.MaxWindowsBuild is int max)
            {
                Assert.True(max < DebloatCatalog.Windows11, tweak.Id);
                Assert.False(tweak.Windows11Only, tweak.Id);
            }
        }
    }

    [Fact]
    public void Registry_operations_use_valid_paths_and_values()
    {
        foreach (var op in DebloatCatalog.All.SelectMany(t => t.Operations).OfType<RegistryValueOp>())
        {
            Assert.False(op.Path.StartsWith('\\') || op.Path.EndsWith('\\'), op.Key);
            Assert.DoesNotContain("HKEY", op.Path, StringComparison.OrdinalIgnoreCase);
            if (op.Hive == DebloatHive.LocalMachine)
            {
                Assert.True(op.Path.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase) || op.Path.StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase), op.Key);
            }

            foreach (var value in new[] { op.AppliedValue, op.DefaultValue }.Where(v => v is not null))
            {
                switch (op.Kind)
                {
                    case DebloatValueKind.DWord:
                        Assert.True(RegistryValueCodec.TryParseDword(value, out _), op.Key);
                        break;
                    case DebloatValueKind.Binary:
                        Assert.NotNull(RegistryValueCodec.ParseHex(value));
                        break;
                }
            }

            Assert.NotEqual(op.AppliedValue, op.DefaultValue);
        }
    }

    [Fact]
    public void Reversible_operations_define_how_to_revert()
    {
        foreach (var tweak in DebloatCatalog.All)
        {
            foreach (var op in tweak.Operations)
            {
                switch (op)
                {
                    case ServiceOp svc:
                        Assert.NotEqual(DebloatStartMode.Boot, svc.AppliedMode);
                        break;
                    case PowerShellOp ps:
                        Assert.True(ps.RevertScript is not null || ps.UndoHint is not null, ps.Key);
                        Assert.False(string.IsNullOrWhiteSpace(ps.DetectScript), ps.Key);
                        Assert.DoesNotContain("Invoke-WebRequest", ps.ApplyScript, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("http", ps.ApplyScript, StringComparison.OrdinalIgnoreCase);
                        break;
                    case AppxRemovalOp:
                        Assert.False(tweak.Reversible, tweak.Id);
                        Assert.Contains("Microsoft Store", DebloatEngine.UndoHint(tweak));
                        break;
                }
            }
        }
    }

    [Fact]
    public void Tweaks_do_not_mix_in_place_and_external_operations()
    {
        foreach (var tweak in DebloatCatalog.All.Where(t => t.Operations.Count > 0))
        {
            Assert.True(tweak.Operations.All(o => o.IsExternal) || tweak.Operations.All(o => !o.IsExternal), tweak.Id);
        }
    }

    [Fact]
    public void App_tweaks_never_target_protected_packages()
    {
        var apps = DebloatCatalog.All.SelectMany(t => t.Operations).OfType<AppxRemovalOp>().ToList();
        Assert.True(apps.Count >= 25);
        Assert.All(apps, a => Assert.True(InboxAppCatalog.IsRemovable(a.PackageName, nonRemovable: false), a.PackageName));
        Assert.DoesNotContain(apps, a => a.PackageName.Equals("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(DebloatCatalog.All, t => t.Title.Contains("Edge", StringComparison.OrdinalIgnoreCase) && t.Operations.Any(o => o is AppxRemovalOp or PowerShellOp));
    }

    [Fact]
    public void Advanced_risk_tweaks_are_hidden_outside_advanced_mode_except_none()
    {
        var hidden = DebloatCatalog.ForMode(advancedMode: false).ToList();
        Assert.DoesNotContain(hidden, t => t.AdvancedOnly);
        Assert.All(DebloatCatalog.All.Where(t => t.Category is DebloatCatalog.UpdateCategory or DebloatCatalog.ServicesCategory), t => Assert.True(t.AdvancedOnly, t.Id));
        Assert.True(DebloatCatalog.Find("onedrive.remove")!.AdvancedOnly);
    }
}

public class DebloatPresetTests
{
    [Fact]
    public void Presets_reference_existing_visible_tweaks()
    {
        foreach (var preset in DebloatPresets.All)
        {
            foreach (var id in preset.TweakIds)
            {
                var tweak = DebloatCatalog.Find(id);
                Assert.NotNull(tweak);
                Assert.False(tweak!.AdvancedOnly, $"{preset.Id} → {id}");
            }

            Assert.Equal(preset.TweakIds.Count, preset.TweakIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Recommended_is_flagged_safe_and_reversible()
    {
        var recommended = DebloatPresets.Find(DebloatPresets.RecommendedId)!;
        Assert.True(recommended.IsRecommended);
        Assert.Single(DebloatPresets.All, p => p.IsRecommended);
        var tweaks = recommended.TweakIds.Select(id => DebloatCatalog.Find(id)!).ToList();
        Assert.All(tweaks, t => Assert.Equal(RiskLevel.Safe, t.Risk));
        Assert.All(tweaks, t => Assert.True(t.Reversible, t.Id));
        Assert.Contains("ai.copilot", recommended.TweakIds);
        Assert.Contains("ai.bing", recommended.TweakIds);
        Assert.Contains("explorer.extensions", recommended.TweakIds);
    }

    [Fact]
    public void Gaming_and_privacy_max_extend_recommended()
    {
        var recommended = DebloatPresets.Find(DebloatPresets.RecommendedId)!.TweakIds;
        Assert.Superset(recommended.ToHashSet(), DebloatPresets.Find(DebloatPresets.GamingId)!.TweakIds.ToHashSet());
        Assert.Superset(recommended.ToHashSet(), DebloatPresets.Find(DebloatPresets.PrivacyMaxId)!.TweakIds.ToHashSet());
        Assert.Contains("gaming.gamemode", DebloatPresets.Find(DebloatPresets.GamingId)!.TweakIds);
        Assert.Contains("privacy.location", DebloatPresets.Find(DebloatPresets.PrivacyMaxId)!.TweakIds);
    }

    [Fact]
    public void Presets_never_contain_conflicting_tweaks()
    {
        foreach (var preset in DebloatPresets.All)
        {
            Assert.Empty(DebloatRules.Conflicts(preset.TweakIds.Select(id => DebloatCatalog.Find(id)!)));
        }

        Assert.Contains(("taskbar.searchicon", "taskbar.searchhide"), DebloatRules.Conflicts(DebloatCatalog.All));
    }

    [Fact]
    public void Consumer_apps_preset_leaves_useful_apps_alone()
    {
        var ids = DebloatPresets.Find(DebloatPresets.ConsumerAppsId)!.TweakIds;
        Assert.True(ids.Count >= 15);
        foreach (var keep in new[] { "apps.calculator", "apps.photos", "apps.notepad", "apps.terminal", "apps.paint", "apps.snippingtool", "apps.camera", "apps.xbox" })
        {
            Assert.NotNull(DebloatCatalog.Find(keep));
            Assert.DoesNotContain(keep, ids);
        }

        Assert.Contains("apps.clipchamp", ids);
        Assert.True(DebloatPresets.Find(DebloatPresets.CustomId)!.IsCustom);
    }

    [Fact]
    public void Unsafe_list_explains_what_is_deliberately_not_offered()
    {
        var titles = string.Join("|", DebloatPresets.NotOffered.Select(u => u.Title));
        Assert.Contains("Defender", titles);
        Assert.Contains("Windows Update", titles);
        Assert.Contains("Edge", titles);
        Assert.Contains("Store", titles);
        Assert.All(DebloatPresets.NotOffered, u =>
        {
            Assert.False(string.IsNullOrWhiteSpace(u.Reason));
            Assert.NotEmpty(u.OfferedBy);
        });
        Assert.All(DebloatPresets.Notes, n => Assert.True(n.SettingsUri.StartsWith("ms-settings:") || n.SettingsUri.StartsWith("https://")));
    }
}

public class DebloatEngineTests
{
    private static (DebloatEngine Engine, InMemoryDebloatSystem System) Create(int? build = DebloatCatalog.Windows11_24H2)
    {
        var system = new InMemoryDebloatSystem(build);
        return (new DebloatEngine(system, new DebloatCaptureBook()), system);
    }

    private static DebloatTweak Tweak(string id) => DebloatCatalog.Find(id)!;

    [Fact]
    public async Task State_moves_from_not_applied_to_partial_to_applied()
    {
        var (engine, system) = Create();
        var tweak = Tweak("privacy.activity");
        Assert.Equal(TweakStatus.NotApplied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);

        system.Memory.SetDword(DebloatHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0);
        var partial = (await engine.GetStatesAsync([tweak], default))[tweak.Id];
        Assert.Equal(TweakStatus.Partial, partial.Status);
        Assert.Equal("1 of 3 changes in place", partial.Detail);

        var result = await engine.ApplyAsync(tweak, default);
        Assert.Equal(TweakOutcome.Applied, result.Outcome);
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);
    }

    [Fact]
    public async Task Revert_restores_the_captured_value_not_the_default()
    {
        var (engine, system) = Create();
        system.Memory.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "Hidden", 1);
        system.Memory.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt", 7);
        var tweak = Tweak("explorer.extensions");

        await engine.ApplyAsync(tweak, default);
        Assert.Equal("0", system.Registry.Read(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt")!.Value);
        Assert.True(engine.Captures.Has(tweak.Id));

        var revert = await engine.RevertAsync(tweak, default);
        Assert.Equal(TweakOutcome.Reverted, revert.Outcome);
        Assert.Contains("saved before", revert.Message);
        Assert.Equal("7", system.Registry.Read(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt")!.Value);
        Assert.False(engine.Captures.Has(tweak.Id));
    }

    [Fact]
    public async Task Revert_without_capture_uses_the_documented_default_and_says_so()
    {
        var (engine, system) = Create();
        system.Memory.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt", 0);
        system.Memory.SetDword(DebloatHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0);

        var ext = await engine.RevertAsync(Tweak("explorer.extensions"), default);
        Assert.Contains("Windows default", ext.Message);
        Assert.Equal("1", system.Registry.Read(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt")!.Value);

        await engine.RevertAsync(Tweak("privacy.feedback"), default);
        Assert.Null(system.Registry.Read(DebloatHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod"));
    }

    [Fact]
    public async Task Applying_twice_keeps_the_original_capture()
    {
        var (engine, system) = Create();
        system.Memory.SetDword(DebloatHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1);
        var tweak = Tweak("privacy.advertisingid");
        await engine.ApplyAsync(tweak, default);
        await engine.ApplyAsync(tweak, default);
        var capture = engine.Captures.Get(tweak.Id)!;
        Assert.Equal("dword:1", capture[@"reg:HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo\Enabled"]);
        Assert.Null(capture[@"reg:HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo\DisabledByGroupPolicy"]);

        await engine.RevertAsync(tweak, default);
        Assert.Equal("1", system.Registry.Read(DebloatHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled")!.Value);
        Assert.Null(system.Registry.Read(DebloatHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy"));
    }

    [Fact]
    public async Task Classic_context_menu_revert_removes_the_key_it_created()
    {
        var (engine, system) = Create();
        var tweak = Tweak("explorer.classicmenu");
        const string clsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(tweak, default)).Outcome);
        Assert.Equal(string.Empty, system.Registry.Read(DebloatHive.CurrentUser, clsid + @"\InprocServer32", string.Empty)!.Value);
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);

        await engine.RevertAsync(tweak, default);
        Assert.False(system.Registry.KeyExists(DebloatHive.CurrentUser, clsid));
    }

    [Fact]
    public async Task Services_are_read_from_the_registry_and_restored_on_revert()
    {
        var (engine, system) = Create();
        system.SetService("MapsBroker", DebloatStartMode.AutomaticDelayed);
        var tweak = Tweak("svc.mapsbroker");
        var before = (await engine.GetStatesAsync([tweak], default))[tweak.Id];
        Assert.Equal(TweakStatus.NotApplied, before.Status);
        Assert.Equal("Automatic (delayed start)", before.Detail);

        await engine.ApplyAsync(tweak, default);
        Assert.Equal(DebloatStartMode.Manual, DebloatStateEvaluator.ReadStartMode(system.Registry, "MapsBroker"));

        await engine.RevertAsync(tweak, default);
        Assert.Equal(DebloatStartMode.AutomaticDelayed, DebloatStateEvaluator.ReadStartMode(system.Registry, "MapsBroker"));
    }

    [Fact]
    public async Task Missing_services_and_tasks_are_skipped_not_failed()
    {
        var (engine, system) = Create();
        var xbox = await engine.ApplyAsync(Tweak("gaming.xboxservices"), default);
        Assert.Equal(TweakOutcome.Skipped, xbox.Outcome);
        Assert.False(engine.Captures.Has("gaming.xboxservices"));

        system.Tasks[@"\Microsoft\Windows\Autochk\Proxy"] = true;
        var ceip = Tweak("privacy.ceiptasks");
        Assert.Equal(TweakStatus.NotApplied, (await engine.GetStatesAsync([ceip], default))[ceip.Id].Status);
        var result = await engine.ApplyAsync(ceip, default);
        Assert.Equal(TweakOutcome.Applied, result.Outcome);
        Assert.False(system.Tasks[@"\Microsoft\Windows\Autochk\Proxy"]);
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([ceip], default))[ceip.Id].Status);

        await engine.RevertAsync(ceip, default);
        Assert.True(system.Tasks[@"\Microsoft\Windows\Autochk\Proxy"]);
    }

    [Fact]
    public async Task Failed_queries_give_unknown_state()
    {
        var (engine, system) = Create();
        system.FailQueries = true;
        var states = await engine.GetStatesAsync([Tweak("privacy.ceiptasks"), Tweak("apps.news")], default);
        Assert.Equal(TweakStatus.Unknown, states["privacy.ceiptasks"].Status);
        Assert.Equal(TweakStatus.Unknown, states["apps.news"].Status);
    }

    [Fact]
    public async Task Build_gating_marks_tweaks_not_applicable_and_skips_them()
    {
        var (w11, _) = Create(DebloatCatalog.Windows11_24H2);
        var news = Tweak("taskbar.news");
        var state = (await w11.GetStatesAsync([news], default))[news.Id];
        Assert.Equal(TweakStatus.NotApplicable, state.Status);
        Assert.Equal("Only for Windows 10", state.Detail);
        Assert.Equal(TweakOutcome.Skipped, (await w11.ApplyAsync(news, default)).Outcome);

        var (w10, _) = Create(DebloatCatalog.Windows10Last);
        var recall = Tweak("ai.recall");
        Assert.Equal("Needs Windows 11 24H2 or newer", (await w10.GetStatesAsync([recall], default))[recall.Id].Detail);
        Assert.False(DebloatRules.IsSupported(Tweak("taskbar.endtask"), DebloatCatalog.Windows11_22H2));
        Assert.True(DebloatRules.IsSupported(Tweak("taskbar.endtask"), null));
    }

    [Fact]
    public async Task Print_spooler_is_held_back_while_a_physical_printer_exists()
    {
        var (engine, system) = Create();
        system.SetService("Spooler", DebloatStartMode.Automatic);
        system.Printers.AddRange(["Microsoft Print to PDF", "Brother HL-L2350DW"]);
        var spooler = Tweak("svc.spooler");

        var held = await engine.ApplyAsync(spooler, default);
        Assert.Equal(TweakOutcome.Skipped, held.Outcome);
        Assert.Contains("Brother", held.Message);
        Assert.Contains("Brother", (await engine.GetStatesAsync([spooler], default))[spooler.Id].Detail);

        system.Printers.Remove("Brother HL-L2350DW");
        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(spooler, default)).Outcome);
        Assert.Equal(DebloatStartMode.Disabled, DebloatStateEvaluator.ReadStartMode(system.Registry, "Spooler"));
    }

    [Fact]
    public async Task Blocked_registry_write_fails_cleanly_and_drops_the_unused_capture()
    {
        var (engine, system) = Create();
        system.Memory.BlockedKeys.Add(@"HKCU\" + DebloatCatalog.ExplorerAdvanced);
        var result = await engine.ApplyAsync(Tweak("taskbar.widgets"), default);
        Assert.Equal(TweakOutcome.Failed, result.Outcome);
        Assert.Contains("access denied", result.Message);
        Assert.False(engine.Captures.Has("taskbar.widgets"));
    }

    [Fact]
    public async Task App_removal_is_one_way_and_feature_changes_round_trip()
    {
        var (engine, system) = Create();
        system.Apps.Add("Clipchamp.Clipchamp");
        var app = Tweak("apps.clipchamp");
        Assert.Equal("Installed", (await engine.GetStatesAsync([app], default))[app.Id].Detail);
        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(app, default)).Outcome);
        Assert.DoesNotContain("Clipchamp.Clipchamp", system.Apps);
        var undo = await engine.RevertAsync(app, default);
        Assert.Equal(TweakOutcome.Skipped, undo.Outcome);
        Assert.Contains("Microsoft Store", undo.Message);

        system.Features["Recall"] = FeatureState.Enabled;
        var recall = Tweak("ai.recallfeature");
        await engine.ApplyAsync(recall, default);
        Assert.Equal(FeatureState.Disabled, system.Features["Recall"]);
        await engine.RevertAsync(recall, default);
        Assert.Equal(FeatureState.Enabled, system.Features["Recall"]);
    }

    [Fact]
    public async Task Removal_for_all_users_runs_when_only_other_accounts_still_have_the_app()
    {
        var (engine, system) = Create();
        system.OtherUserApps.Add("Clipchamp.Clipchamp");
        var app = Tweak("apps.clipchamp");

        Assert.Equal(TweakOutcome.Skipped, (await engine.ApplyAsync(app, default)).Outcome);
        Assert.Contains("Clipchamp.Clipchamp", system.OtherUserApps);

        engine.RemoveAppsForAllUsers = true;
        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(app, default)).Outcome);
        Assert.DoesNotContain("Clipchamp.Clipchamp", system.OtherUserApps);

        Assert.Equal(TweakOutcome.Skipped, (await engine.ApplyAsync(app, default)).Outcome);
    }

    [Fact]
    public async Task Feature_revert_after_an_unknown_capture_restores_the_state_the_tweak_changed()
    {
        var (engine, system) = Create();
        system.Features["Recall"] = FeatureState.Unknown; // the state query failed while capturing
        var recall = Tweak("ai.recallfeature");
        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(recall, default)).Outcome);
        Assert.Equal(FeatureState.Disabled, system.Features["Recall"]);

        Assert.Equal(TweakOutcome.Reverted, (await engine.RevertAsync(recall, default)).Outcome);
        Assert.Equal(FeatureState.Enabled, system.Features["Recall"]);
    }

    [Fact]
    public async Task Feature_revert_keeps_a_captured_disabled_state()
    {
        var (engine, system) = Create();
        system.Features["Recall"] = FeatureState.Disabled;
        var recall = Tweak("ai.recallfeature");
        await engine.ApplyAsync(recall, default);
        await engine.RevertAsync(recall, default);
        Assert.Equal(FeatureState.Disabled, system.Features["Recall"]);
    }

    [Fact]
    public async Task Capture_book_persists_between_sessions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-debloat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = DebloatCaptureBook.DefaultPath(dir);
            var system = new InMemoryDebloatSystem();
            system.Memory.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "LaunchTo", 2);
            await new DebloatEngine(system, new DebloatCaptureBook(path)).ApplyAsync(Tweak("explorer.thispc"), default);
            Assert.True(File.Exists(path));

            var reopened = new DebloatEngine(system, new DebloatCaptureBook(path));
            Assert.True(reopened.Captures.Has("explorer.thispc"));
            await reopened.RevertAsync(Tweak("explorer.thispc"), default);
            Assert.Equal("2", system.Registry.Read(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "LaunchTo")!.Value);
            Assert.Empty(new DebloatCaptureBook(path).TweakIds);
            Assert.Empty(DebloatCaptureBook.Deserialize("{ not json"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

public class DebloatHelperTests
{
    [Fact]
    public void Codec_round_trips_and_compares_like_the_registry()
    {
        var data = new RegistryData(DebloatValueKind.String, "a:b");
        Assert.Equal(data, RegistryValueCodec.Decode(RegistryValueCodec.Encode(data)));
        Assert.Null(RegistryValueCodec.Decode(null));
        Assert.Null(RegistryValueCodec.Decode("nonsense"));
        Assert.True(RegistryValueCodec.Matches(new RegistryData(DebloatValueKind.String, "0"), DebloatValueKind.DWord, "0"));
        Assert.True(RegistryValueCodec.Matches(new RegistryData(DebloatValueKind.DWord, "4294967295"), DebloatValueKind.DWord, "-1"));
        Assert.True(RegistryValueCodec.Matches(new RegistryData(DebloatValueKind.Binary, "03000000aabbccdd"), DebloatValueKind.Binary, "030000000000000000000000"));
        Assert.False(RegistryValueCodec.Matches(new RegistryData(DebloatValueKind.Binary, "02000000"), DebloatValueKind.Binary, "03"));
        Assert.True(RegistryValueCodec.Matches(null, DebloatValueKind.DWord, null));
        Assert.False(RegistryValueCodec.Matches(null, DebloatValueKind.DWord, "0"));
    }

    [Theory]
    [InlineData(2u, null, DebloatStartMode.Automatic, "auto")]
    [InlineData(2u, 1u, DebloatStartMode.AutomaticDelayed, "delayed-auto")]
    [InlineData(3u, null, DebloatStartMode.Manual, "demand")]
    [InlineData(4u, null, DebloatStartMode.Disabled, "disabled")]
    public void Service_start_modes_map_both_ways(uint start, uint? delayed, DebloatStartMode mode, string sc)
    {
        Assert.Equal(mode, ServiceStartModes.FromRegistry(start, delayed));
        Assert.Equal(sc, ServiceStartModes.ScArgument(mode));
        Assert.True(ServiceStartModes.Satisfies(DebloatStartMode.Disabled, DebloatStartMode.Manual));
        Assert.False(ServiceStartModes.Satisfies(DebloatStartMode.Manual, DebloatStartMode.Disabled));
    }

    [Theory]
    [InlineData("Enabled", FeatureState.Enabled)]
    [InlineData("EnablePending", FeatureState.Enabled)]
    [InlineData("DisabledWithPayloadRemoved", FeatureState.Disabled)]
    [InlineData("NotPresent", FeatureState.NotPresent)]
    [InlineData("", FeatureState.Unknown)]
    public void Dism_feature_states_are_parsed(string text, FeatureState expected) =>
        Assert.Equal(expected, WindowsDebloatSystem.ParseFeatureState(text));

    [Fact]
    public void Aggregation_ignores_parts_missing_on_this_pc()
    {
        Assert.Equal(TweakStatus.Applied, DebloatStateEvaluator.Aggregate([OperationState.Applied, OperationState.NotApplicable]));
        Assert.Equal(TweakStatus.Partial, DebloatStateEvaluator.Aggregate([OperationState.Applied, OperationState.NotApplied]));
        Assert.Equal(TweakStatus.NotApplicable, DebloatStateEvaluator.Aggregate([OperationState.NotApplicable]));
        Assert.Equal(TweakStatus.Unknown, DebloatStateEvaluator.Aggregate([OperationState.Unknown]));
        Assert.Equal(TweakStatus.Partial, DebloatStateEvaluator.Aggregate([OperationState.Applied, OperationState.Unknown]));
    }

    [Fact]
    public void Printers_from_windows_and_pdf_tools_are_not_physical()
    {
        var physical = DebloatRules.PhysicalPrinters(["Microsoft Print to PDF", "Microsoft XPS Document Writer", "OneNote (Desktop)", "Fax", "Canon TS5300 series"]);
        Assert.Equal(["Canon TS5300 series"], physical);
    }

    [Fact]
    public void Export_and_import_round_trip()
    {
        var ids = new[] { "privacy.activity", "ai.copilot", "explorer.extensions" };
        var json = DebloatSelectionFormat.Export(ids);
        Assert.Contains(DebloatSelectionFormat.FormatName, json);
        var result = DebloatSelectionFormat.Import(json, DebloatCatalog.All.Select(t => t.Id));
        Assert.Equal(ids, result.Ids);
        Assert.Empty(result.Unmapped);

        var plain = DebloatSelectionFormat.Import("[\"ai.bing\", \"not.a.tweak\"]", DebloatCatalog.All.Select(t => t.Id));
        Assert.Equal(["ai.bing"], plain.Ids);
        Assert.Equal(["not.a.tweak"], plain.Unmapped);
    }

    [Fact]
    public void WinUtil_exports_are_mapped_and_unknown_names_reported()
    {
        const string json = """
            {
              "WPFTweaks": ["WPFTweaksActivity", "WPFTweaksTele", "WPFTweaksRightClickMenu", "WPFTweaksServices", "WPFTweaksUTC"],
              "WPFFeature": ["WPFFeaturesSandbox"],
              "WPFInstall": []
            }
            """;
        var result = DebloatSelectionFormat.Import(json, DebloatCatalog.All.Select(t => t.Id));
        Assert.Equal("winutil", result.Format);
        Assert.Contains("privacy.activity", result.Ids);
        Assert.Contains("privacy.telemetry", result.Ids);
        Assert.Contains("explorer.classicmenu", result.Ids);
        Assert.Equal(["WPFTweaksServices", "WPFTweaksUTC"], result.Unmapped);
        Assert.All(DebloatSelectionFormat.WinUtilMap.Values.SelectMany(v => v), id => Assert.NotNull(DebloatCatalog.Find(id)));
    }

    [Fact]
    public void Import_rejects_garbage_without_throwing()
    {
        var bad = DebloatSelectionFormat.Import("{ broken", DebloatCatalog.All.Select(t => t.Id));
        Assert.Empty(bad.Ids);
        Assert.NotEmpty(bad.Unmapped);
        Assert.Empty(DebloatSelectionFormat.Import("{\"hello\": 1}", []).Ids);
        Assert.Empty(DebloatSelectionFormat.Import(null, []).Ids);
    }

    [Fact]
    public void Confirmation_lists_changes_by_risk_with_restore_point_and_one_way_notes()
    {
        var tweaks = new[] { "explorer.extensions", "privacy.location", "apps.clipchamp", "taskbar.taskview" }.Select(id => DebloatCatalog.Find(id)!).ToList();
        var text = DebloatConfirmationText.Build(tweaks, revert: false);
        Assert.True(text.IndexOf("Moderate (1)", StringComparison.Ordinal) < text.IndexOf("Safe (3)", StringComparison.Ordinal));
        Assert.Contains("restore point", text);
        Assert.Contains("one-way", text);
        Assert.Contains("File Explorer restarts", text);
        Assert.True(DebloatConfirmationText.IsDanger(tweaks, revert: false));
        Assert.False(DebloatConfirmationText.IsDanger([DebloatCatalog.Find("explorer.extensions")!], revert: false));
    }
}

public class DemoDebloatServiceTests
{
    [Fact]
    public async Task Demo_shows_a_realistic_mix_of_states()
    {
        var svc = new DemoDebloatService();
        var states = await svc.GetStatesAsync(default);
        Assert.Equal(DebloatCatalog.All.Count, states.Count);
        Assert.Equal(TweakStatus.Applied, states["explorer.extensions"].Status);
        Assert.Equal(TweakStatus.Partial, states["ads.start"].Status);
        Assert.Equal(TweakStatus.NotApplied, states["ai.copilot"].Status);
        Assert.Equal(TweakStatus.NotApplicable, states["taskbar.news"].Status);
        Assert.Equal(TweakStatus.Applied, states["gaming.gamemode"].Status);
        Assert.Contains("printer", states["svc.spooler"].Detail);
    }

    [Fact]
    public async Task Demo_apply_and_revert_change_the_simulated_pc()
    {
        var svc = new DemoDebloatService();
        svc.SampleSystem.DelayMs = 0;
        var ids = new[] { "ai.copilot", "privacy.telemetry", "apps.clipchamp", "taskbar.news", "unknown.id" };
        var applied = await svc.ApplyAsync(ids, null, default);
        Assert.Equal(3, applied.Succeeded);
        Assert.Equal(2, applied.Skipped);
        Assert.True(applied.RestartExplorerSuggested);

        var states = await svc.GetStatesAsync(default);
        Assert.Equal(TweakStatus.Applied, states["ai.copilot"].Status);
        Assert.Equal(TweakStatus.Applied, states["privacy.telemetry"].Status);
        Assert.Equal("Not installed", states["apps.clipchamp"].Detail);

        var reverted = await svc.RevertAsync(["ai.copilot", "privacy.telemetry"], null, default);
        Assert.Equal(2, reverted.Succeeded);
        Assert.Equal(TweakStatus.NotApplied, (await svc.GetStatesAsync(default))["ai.copilot"].Status);

        var (importIds, _) = svc.ImportSelection(svc.ExportSelection(["ai.bing"]));
        Assert.Equal(["ai.bing"], importIds);
    }
}

public class DebloatBatchTests
{
    /// <summary>Debloat service over the demo PC that records every guarded batch and can cancel part-way.</summary>
    private sealed class ProbeService : DebloatServiceBase
    {
        private readonly IOptimizationService _optimization = new DemoOptimizationService([]);

        public ProbeService()
            : base(new DebloatEngine(DemoDebloatService.CreateSampleSystem(), new DebloatCaptureBook()))
        {
            ((InMemoryDebloatSystem)Engine.System).DelayMs = 0;
        }

        public List<IReadOnlyList<string>> Batches { get; } = [];

        public CancellationTokenSource? CancelAfterFirst { get; set; }

        public int DelegatedCalls { get; private set; }

        public override Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok("ok"));

        public override OperationResult OpenSettings(string settingsUri) => OperationResult.Ok("ok");

        protected override async Task<OperationResult<IReadOnlyList<TweakRunResult>>> RunGuardedAsync(string description, IReadOnlyList<DebloatTweak> tweaks, bool revert,
            Func<CancellationToken, Task<IReadOnlyList<TweakRunResult>>> work, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            Batches.Add(tweaks.Select(t => t.Id).ToList());
            var results = await work(cancellationToken);
            return OperationResult<IReadOnlyList<TweakRunResult>>.Ok(results, "done");
        }

        protected override Task<OperationResult> RunDelegatedAsync(string optimizationId, bool revert, CancellationToken cancellationToken)
        {
            DelegatedCalls++;
            CancelAfterFirst?.Cancel();
            return revert ? _optimization.RevertAsync(optimizationId, cancellationToken) : _optimization.ApplyAsync(optimizationId, cancellationToken);
        }

        protected override Task<ActionState> GetDelegatedStateAsync(string optimizationId, CancellationToken cancellationToken) =>
            _optimization.GetStateAsync(optimizationId, cancellationToken);
    }

    [Fact]
    public async Task App_removals_and_delegated_actions_run_inside_the_one_guarded_batch()
    {
        var svc = new ProbeService();
        var ids = new[] { "apps.clipchamp", "privacy.telemetry", "ai.copilot", "gaming.gamemode" };
        var result = await svc.ApplyAsync(ids, null, default);

        var batch = Assert.Single(svc.Batches);
        Assert.Equal(ids.Length, batch.Count);
        Assert.Equal("ai.copilot", batch[0]); // in-place tweaks run first, then the external ones
        Assert.Contains("apps.clipchamp", batch);
        Assert.Equal(ids.Length, result.Results.Count);
    }

    [Fact]
    public async Task Cancel_stops_between_tweaks_and_reports_the_rest_as_skipped()
    {
        var svc = new ProbeService();
        using var cts = new CancellationTokenSource();
        svc.CancelAfterFirst = cts;
        var ids = new[] { "ai.copilot", "privacy.telemetry", "gaming.gamemode", "apps.clipchamp" };
        var result = await svc.ApplyAsync(ids, null, cts.Token);

        Assert.Equal(TweakOutcome.Applied, result.Results.Single(r => r.TweakId == "ai.copilot").Outcome);
        Assert.Equal(TweakOutcome.Applied, result.Results.Single(r => r.TweakId == "privacy.telemetry").Outcome);
        Assert.Equal(1, svc.DelegatedCalls);
        var skipped = result.Results.Where(r => r.Outcome == TweakOutcome.Skipped).ToList();
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, r => Assert.Contains("Cancelled", r.Message));
        Assert.Equal(TweakStatus.Applied, (await svc.GetStatesAsync(default))["ai.copilot"].Status);
    }

    [Fact]
    public void Batch_guard_skips_per_change_restore_points_but_keeps_the_rest()
    {
        var options = new ChangeOptions(null, true, "Windows Features", "Clipchamp.Clipchamp", true, "Reinstall from the Store")
        { OnRestorePointFailed = RestorePointFailedBehavior.Abort };
        var inner = DebloatBatchChangeGuard.WithoutRestorePoint(options);
        Assert.False(inner.CreateBefore);
        Assert.False(inner.CreateAfter);
        Assert.Equal(RestorePointFailedBehavior.Continue, inner.OnRestorePointFailed);
        Assert.Equal(options.Module, inner.Module);
        Assert.Equal(options.Target, inner.Target);
        Assert.Equal(options.UndoHint, inner.UndoHint);
    }

    [Fact]
    public async Task Batch_guard_forwards_to_the_real_guard()
    {
        var recorder = new RecordingGuard();
        var guard = new DebloatBatchChangeGuard(recorder);
        var result = await guard.RunGuardedAsync("Remove Clipchamp", ChangeOptions.For("Windows Features", "Clipchamp"),
            _ => Task.FromResult(OperationResult.Ok("removed")), null, default);
        Assert.True(result.Success);
        Assert.Equal(false, Assert.Single(recorder.Options).CreateBefore);
    }

    [Fact]
    public void Tips_tweak_does_not_share_registry_values_with_start_suggestions()
    {
        var tips = DebloatCatalog.Find("ads.tips")!;
        Assert.Null(tips.DelegatesToOptimizationId);
        var startKeys = DebloatCatalog.Find("ads.start")!.Operations.Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(tips.Operations, o => startKeys.Contains(o.Key));
        Assert.Empty(DebloatRules.Conflicts([tips, DebloatCatalog.Find("ads.start")!]));
    }

    [Fact]
    public void Fax_and_spooler_wait_for_no_printers_and_error_reporting_sets_the_service()
    {
        Assert.Equal(DebloatPrecondition.NoPhysicalPrinters, DebloatCatalog.Find("svc.fax")!.Precondition);
        Assert.Equal(DebloatPrecondition.NoPhysicalPrinters, DebloatCatalog.Find("svc.spooler")!.Precondition);
        Assert.Contains(DebloatCatalog.Find("privacy.wer")!.Operations, o => o is ServiceOp { Name: "WerSvc", AppliedMode: DebloatStartMode.Manual });
    }

    [Fact]
    public void Confirmation_lists_the_exact_changes()
    {
        var tweaks = new[] { "privacy.wer", "apps.clipchamp", "gaming.gamemode" }.Select(id => DebloatCatalog.Find(id)!).ToList();
        var text = DebloatConfirmationText.Build(tweaks, revert: false);
        Assert.Contains(@"Set HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\Disabled = 1", text);
        Assert.Contains("Set service WerSvc to Manual", text);
        Assert.Contains("Remove the Clipchamp app", text);
        Assert.Contains("One restore point is created before the first change", text);

        var revert = DebloatConfirmationText.Build(tweaks, revert: true);
        Assert.Contains(@"Restore HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\Disabled", revert);
    }

    [Fact]
    public void Confirmation_stays_bounded_for_large_batches()
    {
        var text = DebloatConfirmationText.Build(DebloatCatalog.All, revert: false);
        int changeLines = text.Split('\n').Count(l => l.StartsWith("      - ", StringComparison.Ordinal));
        Assert.True(changeLines <= DebloatConfirmationText.MaxChangeLines + DebloatConfirmationText.MaxChangesPerTweak + 1, $"{changeLines} change lines");
        Assert.Contains("point at their rows", text);
    }

    [Theory]
    [InlineData(DebloatValueKind.QWord, "18446744073709551615")]
    [InlineData(DebloatValueKind.ExpandString, @"%SystemRoot%\system32")]
    [InlineData(DebloatValueKind.MultiString, "a\nb")]
    public void Captured_value_kinds_survive_encoding(DebloatValueKind kind, string value)
    {
        var name = RegistryValueCodec.KindName(kind);
        var parsed = RegistryValueCodec.Decode(name + ":" + value);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(value, parsed.Value);
    }

    [Fact]
    public void Qword_text_parses_beyond_the_dword_range()
    {
        Assert.True(RegistryValueCodec.TryParseQword("5000000000", out var a));
        Assert.Equal(5_000_000_000UL, a);
        Assert.True(RegistryValueCodec.TryParseQword("0xFFFFFFFFFF", out var b));
        Assert.Equal(0xFFFFFFFFFFUL, b);
        Assert.True(RegistryValueCodec.TryParseQword("-1", out var c));
        Assert.Equal(ulong.MaxValue, c);
        Assert.False(RegistryValueCodec.TryParseQword("nope", out _));
    }

    private sealed class RecordingGuard : IChangeGuard
    {
        public List<ChangeOptions> Options { get; } = [];

        public Task<OperationResult<T>> RunGuardedAsync<T>(string description, ChangeOptions options,
            Func<CancellationToken, Task<OperationResult<T>>> change, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            Options.Add(options);
            return change(cancellationToken);
        }

        public Task<OperationResult> RunGuardedAsync(string description, ChangeOptions options,
            Func<CancellationToken, Task<OperationResult>> change, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            Options.Add(options);
            return change(cancellationToken);
        }
    }
}

public class DebloatReviewFixTests
{
    private static DebloatTweak Tweak(string id) => DebloatCatalog.Find(id)!;

    [Fact]
    public void Network_printer_connections_are_named_and_hold_back_the_spooler()
    {
        Assert.Equal(@"\\printsrv\Floor2-Laser", DebloatRules.ConnectionKeyToPrinterName(",,printsrv,Floor2-Laser"));
        Assert.Equal("Brother HL-L2350DW", DebloatRules.ConnectionKeyToPrinterName("Brother HL-L2350DW"));

        var blocked = DebloatRules.PreconditionFailure(Tweak("svc.spooler"),
            ["Microsoft Print to PDF", DebloatRules.ConnectionKeyToPrinterName(",,printsrv,Floor2-Laser")]);
        Assert.NotNull(blocked);
        Assert.Contains(@"\\printsrv\Floor2-Laser", blocked);
    }

    [Fact]
    public void Unreadable_capture_file_is_kept_instead_of_overwritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-debloat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = DebloatCaptureBook.DefaultPath(dir);
            File.WriteAllText(path, "{ \"explorer.thispc\": { broken");
            var book = new DebloatCaptureBook(path);
            Assert.NotNull(book.LoadWarning);
            Assert.NotNull(book.BackupPath);
            Assert.Equal("{ \"explorer.thispc\": { broken", File.ReadAllText(book.BackupPath!));
            Assert.False(DebloatCaptureBook.TryDeserialize("{ not json", out _));
            Assert.True(DebloatCaptureBook.TryDeserialize("", out var empty));
            Assert.Empty(empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Capture_save_failure_on_revert_does_not_fail_the_tweak()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-debloat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = DebloatCaptureBook.DefaultPath(dir);
            var system = new InMemoryDebloatSystem();
            system.Memory.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "LaunchTo", 2);
            var engine = new DebloatEngine(system, new DebloatCaptureBook(path));
            Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(Tweak("explorer.thispc"), default)).Outcome);

            // Make the next save fail: the capture file's place is taken by a folder.
            File.Delete(path);
            Directory.CreateDirectory(path);

            var revert = await engine.RevertAsync(Tweak("explorer.thispc"), default);
            Assert.Equal(TweakOutcome.Reverted, revert.Outcome);
            Assert.Contains("could not be updated", revert.Message);
            Assert.Equal("2", system.Registry.Read(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "LaunchTo")!.Value);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Feature_changes_and_one_way_changes_need_the_restore_point()
    {
        Assert.True(WindowsDebloatService.RequiresRestorePoint([Tweak("ai.recallfeature")], revert: false));
        Assert.True(WindowsDebloatService.RequiresRestorePoint([Tweak("ai.recallfeature")], revert: true));
        Assert.True(WindowsDebloatService.RequiresRestorePoint([Tweak("apps.clipchamp")], revert: false));
        Assert.False(WindowsDebloatService.RequiresRestorePoint([Tweak("explorer.thispc"), Tweak("privacy.telemetry")], revert: false));
    }

    [Fact]
    public void Explorer_restart_stays_in_this_session_and_never_starts_an_elevated_shell()
    {
        var script = WindowsDebloatService.RestartExplorerScript;
        Assert.Contains("(Get-Process -Id $PID).SessionId", script);
        Assert.Contains("Get-Shell | Stop-Process -Force", script);
        Assert.DoesNotContain("Get-Process explorer -ErrorAction SilentlyContinue | Stop-Process", script);
        Assert.DoesNotContain("Start-Process", script);
        Assert.Contains("-LogonType Interactive -RunLevel Limited", script);
        Assert.Contains("Unregister-ScheduledTask", script);
    }

    [Fact]
    public void OneDrive_uninstall_handles_per_machine_installs_and_checks_the_result()
    {
        var op = Tweak("onedrive.remove").Operations.OfType<PowerShellOp>().Single();
        Assert.Contains("Arguments = '/uninstall /allusers'", op.ApplyScript);
        Assert.Contains("$machineDir = \"$env:ProgramFiles\\Microsoft OneDrive\"", op.ApplyScript);
        Assert.Contains("if (Test-Installed) {", op.ApplyScript);
        Assert.DoesNotContain("exit $p.ExitCode\"", op.ApplyScript);

        // The final check looks at the same places as the detection that drives the tweak's state.
        Assert.Contains("OneDrive.exe", op.DetectScript);
        Assert.Contains("$env:ProgramFiles\\Microsoft OneDrive", op.DetectScript);
        Assert.Contains("$env:LOCALAPPDATA\\Microsoft\\OneDrive", op.DetectScript);
    }

    [Fact]
    public void Conflicting_selection_is_detected_and_related_tweaks_are_rechecked()
    {
        var pairs = DebloatRules.ConflictingPairs([Tweak("taskbar.searchicon"), Tweak("taskbar.searchhide"), Tweak("explorer.thispc")]);
        var (a, b) = Assert.Single(pairs);
        Assert.Equal("taskbar.searchicon", a.Id);
        Assert.Equal("taskbar.searchhide", b.Id);

        var related = DebloatRules.WithRelated(["taskbar.searchicon"], DebloatCatalog.All).Select(t => t.Id).ToList();
        Assert.Contains("taskbar.searchhide", related);
        Assert.Contains("taskbar.searchbox", related);
        Assert.Contains("taskbar.searchlabel", related);
        Assert.DoesNotContain("explorer.thispc", related);
    }

    [Fact]
    public void Brief_options_are_offered()
    {
        Assert.Contains("ai.copilotapp", DebloatPresets.Find(DebloatPresets.ConsumerAppsId)!.TweakIds);
        Assert.NotNull(DebloatCatalog.Find("taskbar.aligncenter"));
        Assert.NotNull(DebloatCatalog.Find("taskbar.searchbox"));
        Assert.NotNull(DebloatCatalog.Find("taskbar.searchlabel"));
    }

    [Fact]
    public async Task OneDrive_switched_off_in_task_manager_counts_as_applied()
    {
        var system = new InMemoryDebloatSystem();
        var engine = new DebloatEngine(system, new DebloatCaptureBook());
        system.Memory.Write(DebloatHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "OneDrive",
            new RegistryData(DebloatValueKind.Binary, "0300000066A9C2D4E5F6D901"));
        var tweak = Tweak("onedrive.autostart");
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);
    }

    [Fact]
    public async Task Batch_reads_the_installed_apps_once()
    {
        var svc = new DemoDebloatService(new DemoOptimizationService([]));
        var appIds = DebloatCatalog.PreselectedAppIds.Take(6).ToList();
        foreach (var id in appIds)
        {
            svc.SampleSystem.Apps.Add(Tweak(id).Operations.OfType<AppxRemovalOp>().Single().PackageName);
        }

        svc.SampleSystem.DelayMs = 0;
        int before = svc.SampleSystem.AppQueries;
        var result = await svc.ApplyAsync(appIds, null, default);
        Assert.All(result.Results, r => Assert.Equal(TweakOutcome.Applied, r.Outcome));
        Assert.Equal(before + 1, svc.SampleSystem.AppQueries);

        var states = await svc.GetStatesAsync(appIds.Take(1).ToList(), default);
        Assert.Equal(TweakStatus.Applied, states[appIds[0]].Status);
    }
}
