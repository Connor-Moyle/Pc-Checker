using PcChecker.Core.Common;
using PcChecker.Core.Performance;
using PcChecker.Core.Startup;
using Xunit;

namespace PcChecker.Core.Tests;

public class StartupApprovedCodecTests
{
    [Fact]
    public void Enabled_value_is_12_bytes_with_flag_02_and_no_timestamp()
    {
        var bytes = StartupApprovedCodec.Encode(true);
        Assert.Equal(StartupApprovedCodec.Length, bytes.Length);
        Assert.Equal(0x02, bytes[0]);
        Assert.All(bytes.Skip(1), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Disabled_value_carries_the_filetime_in_bytes_4_to_11()
    {
        var when = new DateTime(2025, 3, 14, 15, 9, 26, DateTimeKind.Utc);
        var bytes = StartupApprovedCodec.Encode(false, when);
        Assert.Equal(0x03, bytes[0]);
        Assert.Equal(when.ToFileTimeUtc(), BitConverter.ToInt64(bytes, 4));
        var (enabled, disabledAt) = StartupApprovedCodec.Decode(bytes);
        Assert.False(enabled);
        Assert.Equal(when, disabledAt);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, false)]
    public void Missing_or_short_values_decode_sensibly(byte[]? value, bool expectedEnabled)
    {
        Assert.Equal(expectedEnabled, StartupApprovedCodec.IsEnabled(value));
    }

    [Fact]
    public void Enabled_and_disabled_values_round_trip()
    {
        var enabled = StartupApprovedCodec.Encode(true);
        Assert.Equal(12, enabled.Length);
        Assert.Equal(0x02, enabled[0]);
        Assert.True(StartupApprovedCodec.IsEnabled(enabled));
        Assert.Null(StartupApprovedCodec.DisabledAt(enabled));

        var when = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var disabled = StartupApprovedCodec.Encode(false, when);
        Assert.Equal(0x03, disabled[0]);
        Assert.False(StartupApprovedCodec.IsEnabled(disabled));
        Assert.Equal(when, StartupApprovedCodec.DisabledAt(disabled));
    }

    [Fact]
    public void Windows_own_flags_are_recognised_and_preserved()
    {
        Assert.True(StartupApprovedCodec.IsEnabled([0x06, 0, 0, 0]));
        Assert.False(StartupApprovedCodec.IsEnabled([0x07, 0, 0, 0]));
        var toggled = StartupApprovedCodec.Toggle([0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], enabled: false, DateTimeOffset.UtcNow);
        Assert.Equal(0x07, toggled[0]);
        Assert.False(StartupApprovedCodec.IsEnabled(toggled));
        Assert.Equal(0x02, StartupApprovedCodec.Toggle([0x03], enabled: true)[0]);
    }
}

public class StartupCommandParserTests
{
    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent", "C:\\Program Files (x86)\\Steam\\steam.exe", "-silent")]
    [InlineData("C:\\Users\\Alex\\AppData\\Local\\Discord\\Update.exe --processStart Discord.exe", "C:\\Users\\Alex\\AppData\\Local\\Discord\\Update.exe", "--processStart Discord.exe")]
    [InlineData("C:\\Tools\\tool.exe", "C:\\Tools\\tool.exe", "")]
    [InlineData("\"C:\\A B\\x.lnk\"", "C:\\A B\\x.lnk", "")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL", "rundll32.exe", "shell32.dll,Control_RunDLL")]
    public void Extracts_executable_and_arguments(string command, string exe, string args)
    {
        Assert.Equal(exe, StartupCommandParser.ExtractExecutable(command));
        Assert.Equal(args, StartupCommandParser.ExtractArguments(command));
    }

    [Fact]
    public void Expands_environment_variables_through_the_resolver_and_keeps_unknown_ones()
    {
        string? Env(string name) => name.Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase) ? @"C:\Users\Alex\AppData\Local" : null;
        Assert.Equal(@"C:\Users\Alex\AppData\Local\Spotify\Spotify.exe", StartupCommandParser.ExtractExecutable(@"%LOCALAPPDATA%\Spotify\Spotify.exe --minimized", Env));
        Assert.Equal(@"%NOPE%\x.exe", StartupCommandParser.ExtractExecutable(@"%NOPE%\x.exe", Env));
    }

    [Fact]
    public void Empty_commands_yield_null_and_file_name_helper_works()
    {
        Assert.Null(StartupCommandParser.ExtractExecutable(null));
        Assert.Null(StartupCommandParser.ExtractExecutable("   "));
        Assert.Equal("steam.exe", StartupCommandParser.FileNameOf("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent"));
        Assert.True(StartupCommandParser.LooksLikeExecutable(@"C:\x\y.EXE"));
        Assert.False(StartupCommandParser.LooksLikeExecutable(@"C:\x\y.txt"));
    }
}

public class StartupPublisherAndImpactTests
{
    [Fact]
    public void File_company_name_wins_then_known_products_then_vendor_folder()
    {
        Assert.Equal("Acme Ltd", StartupPublisherRules.Resolve(" Acme Ltd ", "Steam", @"C:\Program Files\Steam\steam.exe"));
        Assert.Equal("Valve Corporation", StartupPublisherRules.Resolve(null, "Steam", @"C:\Games\steam.exe"));
        Assert.Equal("Contoso", StartupPublisherRules.Resolve(null, "Thing", @"C:\Program Files (x86)\Contoso\Thing\thing.exe"));
        Assert.Equal("Widgets", StartupPublisherRules.Resolve(null, "Widget", @"C:\Program Files\Common Files\Widgets\w.exe"));
        Assert.Equal("Unknown", StartupPublisherRules.Resolve(null, "Mystery", @"D:\stuff\m.exe"));
        Assert.True(StartupPublisherRules.IsMicrosoft("Microsoft Corporation"));
        Assert.False(StartupPublisherRules.IsMicrosoft(null));
    }

    [Fact]
    public void Impact_prefers_known_heavy_apps_then_file_size()
    {
        Assert.Equal(StartupImpactRules.High, StartupImpactRules.Estimate("Discord", @"C:\x\Update.exe", 1_000).Impact);
        Assert.Equal(StartupImpactRules.High, StartupImpactRules.Estimate("Big", @"C:\x\big.exe", 200L * 1024 * 1024).Impact);
        Assert.Equal(StartupImpactRules.Medium, StartupImpactRules.Estimate("Mid", @"C:\x\mid.exe", 40L * 1024 * 1024).Impact);
        Assert.Equal(StartupImpactRules.Medium, StartupImpactRules.Estimate("Vendor Updater", @"C:\x\upd.exe", 1_000).Impact);
        Assert.Equal(StartupImpactRules.Low, StartupImpactRules.Estimate("Tiny", @"C:\x\tiny.exe", 1_000).Impact);
        Assert.Equal(StartupImpactRules.NotMeasured, StartupImpactRules.Estimate("Gone", @"C:\x\gone.exe", null).Impact);
        Assert.True(StartupImpactRules.Rank(StartupImpactRules.High) < StartupImpactRules.Rank(StartupImpactRules.Low));
    }

    [Fact]
    public void Describe_uses_the_size_probe_and_reports_missing_files()
    {
        var item = new StartupItem("Tool", "\"C:\\Tools\\tool.exe\" /bg", "HKCU", true, null, StartupKinds.Registry);
        var present = StartupImpactRules.Describe(item, _ => 5_000);
        Assert.Equal(@"C:\Tools\tool.exe", present.ExecutablePath);
        Assert.True(present.FileExists);
        Assert.Equal(StartupImpactRules.Low, present.Impact);

        var missing = StartupImpactRules.Describe(item, _ => null);
        Assert.False(missing.FileExists);
        Assert.Equal(StartupImpactRules.NotMeasured, missing.Impact);
    }
}

public class ProtectedServicesTests
{
    [Theory]
    [InlineData("RpcSs", ServiceProtection.Protected)]
    [InlineData("rpcss", ServiceProtection.Protected)]
    [InlineData("WinDefend", ServiceProtection.Protected)]
    [InlineData("Schedule", ServiceProtection.Protected)]
    [InlineData("Audiosrv", ServiceProtection.Warn)]
    [InlineData("Themes", ServiceProtection.Warn)]
    [InlineData("SysMain", ServiceProtection.Warn)]
    [InlineData("ArmouryCrateService", ServiceProtection.None)]
    [InlineData("", ServiceProtection.None)]
    public void Classifies_services(string name, ServiceProtection expected)
    {
        Assert.Equal(expected, ProtectedServices.Classify(name));
    }

    [Fact]
    public void Protected_services_cannot_be_stopped_from_starting_but_can_be_set_automatic()
    {
        var (allowed, message) = ProtectedServices.CanSetStartMode("RpcSs", "Disabled");
        Assert.False(allowed);
        Assert.Contains("RpcSs", message);
        Assert.True(ProtectedServices.CanSetStartMode("RpcSs", "Automatic").Allowed);
        Assert.True(ProtectedServices.CanSetStartMode("RpcSs", "Automatic (Delayed)").Allowed);
    }

    [Fact]
    public void Warn_only_services_are_allowed_with_a_message_and_plain_ones_silently()
    {
        var (allowed, message) = ProtectedServices.CanSetStartMode("Audiosrv", "Manual");
        Assert.True(allowed);
        Assert.NotNull(message);
        var (plainAllowed, plainMessage) = ProtectedServices.CanSetStartMode("CorsairService", "Disabled");
        Assert.True(plainAllowed);
        Assert.Null(plainMessage);
        Assert.False(ProtectedServices.CanSetStartMode("CorsairService", "Sometimes").Allowed);
    }
}

public class ServiceStartModesTests
{
    [Theory]
    [InlineData("Auto", false, "Automatic")]
    [InlineData("Auto", true, "Automatic (Delayed)")]
    [InlineData("Auto (Delayed)", false, "Automatic (Delayed)")]
    [InlineData("Automatic (Delayed Start)", false, "Automatic (Delayed)")]
    [InlineData("demand", false, "Manual")]
    [InlineData("Manual", false, "Manual")]
    [InlineData("Disabled", false, "Disabled")]
    [InlineData("Boot", false, "Boot")]
    [InlineData("weird", false, null)]
    public void Normalizes_wmi_and_sc_spellings(string input, bool delayed, string? expected)
    {
        Assert.Equal(expected, ServiceStartModes.Normalize(input, delayed));
    }

    [Fact]
    public void Maps_to_sc_arguments_and_detects_automatic()
    {
        Assert.Equal("auto", ServiceStartModes.ToScArgument("Automatic"));
        Assert.Equal("delayed-auto", ServiceStartModes.ToScArgument("Auto (Delayed)"));
        Assert.Equal("demand", ServiceStartModes.ToScArgument("Manual"));
        Assert.Equal("disabled", ServiceStartModes.ToScArgument("Disabled"));
        Assert.Null(ServiceStartModes.ToScArgument("Boot"));
        Assert.True(ServiceStartModes.IsAutomatic("Auto (Delayed)"));
        Assert.False(ServiceStartModes.IsAutomatic("Manual"));
    }
}

public class BootConfigParserTests
{
    private const string BootMgr = """
        Windows Boot Manager
        --------------------
        identifier              {bootmgr}
        device                  partition=\Device\HarddiskVolume1
        path                    \EFI\MICROSOFT\BOOT\BOOTMGFW.EFI
        description             Windows Boot Manager
        locale                  en-US
        inherit                 {globalsettings}
        default                 {current}
        resumeobject            {1a2b3c4d-0000-0000-0000-000000000001}
        displayorder            {current}
                                {1a2b3c4d-0000-0000-0000-000000000002}
        toolsdisplayorder       {memdiag}
        bootsequence            {memdiag}
        timeout                 30
        """;

    private const string Current = """
        Windows Boot Loader
        -------------------
        identifier              {current}
        device                  partition=C:
        path                    \WINDOWS\system32\winload.efi
        description             Windows 11
        locale                  en-US
        inherit                 {bootloadersettings}
        recoveryenabled         Yes
        safeboot                Minimal
        osdevice                partition=C:
        systemroot              \WINDOWS
        nx                      OptIn
        bootmenupolicy          Legacy
        """;

    [Fact]
    public void Parses_boot_manager_timeout_entries_and_memdiag()
    {
        var info = BootConfigParser.ParseBootManager(BootMgr);
        Assert.Equal(30, info.TimeoutSeconds);
        Assert.Equal(2, info.EntryCount);
        Assert.True(info.MemoryDiagnosticScheduled);
        Assert.Equal("{current}", info.DefaultId);
        Assert.Equal("{current}", info.DisplayOrder[0]);
    }

    [Fact]
    public void Parses_boot_loader_description_safeboot_and_menu_policy()
    {
        var info = BootConfigParser.ParseBootLoader(Current);
        Assert.Equal("Windows 11", info.Description);
        Assert.True(info.SafeMode);
        Assert.Equal("Minimal", info.SafeModeKind);
        Assert.True(info.LegacyBootMenu);
        Assert.Equal("{current}", info.Identifier);
    }

    [Fact]
    public void Missing_values_default_safely()
    {
        var mgr = BootConfigParser.ParseBootManager("Windows Boot Manager\n--------------------\nidentifier {bootmgr}\n");
        Assert.Null(mgr.TimeoutSeconds);
        Assert.Equal(0, mgr.EntryCount);
        Assert.False(mgr.MemoryDiagnosticScheduled);
        var loader = BootConfigParser.ParseBootLoader(string.Empty);
        Assert.False(loader.SafeMode);
        Assert.False(loader.LegacyBootMenu);
        Assert.Null(loader.Description);
        Assert.Empty(BootConfigParser.ParseEntries(null));
    }

    [Theory]
    [InlineData("18420", 18.4)]
    [InlineData("  65000\r\n", 65.0)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("0", null)]
    public void Parses_boot_time_event_milliseconds(string input, double? expectedSeconds)
    {
        Assert.Equal(expectedSeconds, BootConfigParser.ParseBootTimeSeconds(input));
    }
}

public class BootConfigRulesAndWinlogonTests
{
    private static BootConfig Healthy() => new(3, "Windows 11", true, true, false, false, DateTimeOffset.Now.AddHours(-2), 18.4, false);

    [Fact]
    public void Healthy_boot_and_few_items_produce_no_warnings()
    {
        var items = Enumerable.Range(0, 3).Select(i => new StartupItem($"App{i}", "x.exe", "HKCU", true, null, StartupKinds.Registry)).ToList();
        Assert.Empty(BootConfigRules.Evaluate(Healthy(), items));
    }

    [Fact]
    public void Flags_safe_mode_memdiag_long_timeout_and_slow_boot_sorted_by_severity()
    {
        var boot = Healthy() with { SafeModeNextBoot = true, MemoryDiagnosticScheduled = true, TimeoutSeconds = 30, LastBootSeconds = 130 };
        var warnings = BootConfigRules.Evaluate(boot, []);
        Assert.Contains(warnings, w => w.Id == "boot.safemode" && w.Severity == Severity.High);
        Assert.Contains(warnings, w => w.Id == "boot.memdiag");
        Assert.Contains(warnings, w => w.Id == "boot.timeout");
        Assert.Contains(warnings, w => w.Id == "boot.veryslow");
        Assert.True(warnings[0].Severity >= warnings[^1].Severity);
    }

    [Fact]
    public void Many_startup_items_respect_the_threshold_and_the_setting()
    {
        var items = Enumerable.Range(0, 12).Select(i => new StartupItem($"App{i}", "x.exe", "HKCU", true, null, StartupKinds.Registry)).ToList();
        Assert.Contains(BootConfigRules.Evaluate(Healthy(), items, manyStartupItems: 10), w => w.Id == "startup.many" && w.Severity == Severity.Medium);
        Assert.Contains(BootConfigRules.Evaluate(Healthy(), items, manyStartupItems: 5), w => w.Id == "startup.many" && w.Severity == Severity.High);
        Assert.DoesNotContain(BootConfigRules.Evaluate(Healthy(), items, manyStartupItems: 20), w => w.Id == "startup.many");
        Assert.DoesNotContain(BootConfigRules.Evaluate(Healthy(), items, manyStartupItems: 10, warnOnManyItems: false), w => w.Id == "startup.many");
    }

    [Fact]
    public void Winlogon_defaults_pass_and_anomalies_are_reported()
    {
        Assert.Empty(WinlogonRules.Check("explorer.exe", @"C:\Windows\system32\userinit.exe,"));
        Assert.Empty(WinlogonRules.Check("\"explorer.exe\"", @"C:\WINDOWS\System32\Userinit.exe,"));
        var anomalies = WinlogonRules.Check("explorer.exe, evil.exe", @"C:\Windows\system32\userinit.exe,C:\bad\p.exe,");
        Assert.Equal(2, anomalies.Count);
        Assert.All(anomalies, a => Assert.Equal(StartupKinds.Winlogon, a.Kind));
        Assert.Contains(BootConfigRules.Evaluate(Healthy(), anomalies), w => w.Id == "startup.winlogon" && w.Severity == Severity.Critical);
    }
}

public class StartupActionsProviderTests
{
    [Fact]
    public void Declares_the_three_startup_actions()
    {
        var provider = new StartupActionsProvider(new DemoStartupService());
        Assert.Equal(3, provider.Actions.Count);
        Assert.True(provider.Handles("startup.faststartup.on"));
        Assert.True(provider.Handles("STARTUP.TIMEOUT.3S"));
        Assert.False(provider.Handles("latency.hags.on"));
        Assert.All(provider.Actions, a => Assert.Equal("Startup", a.Category));
    }

    [Fact]
    public async Task Timeout_action_applies_and_reverts_through_the_startup_service()
    {
        var demo = new DemoStartupService();
        var provider = new StartupActionsProvider(demo);
        Assert.False((await provider.GetStateAsync(StartupActionsProvider.Timeout3s, CancellationToken.None)).IsApplied);
        Assert.True((await provider.ApplyAsync(StartupActionsProvider.Timeout3s, CancellationToken.None)).Success);
        Assert.Equal(3, (await demo.GetBootConfigAsync(CancellationToken.None)).TimeoutSeconds);
        Assert.True((await provider.GetStateAsync(StartupActionsProvider.Timeout3s, CancellationToken.None)).IsApplied);
        Assert.True((await provider.RevertAsync(StartupActionsProvider.Timeout3s, CancellationToken.None)).Success);
        Assert.Equal(30, (await demo.GetBootConfigAsync(CancellationToken.None)).TimeoutSeconds);
    }

    [Fact]
    public async Task Fast_startup_actions_mirror_each_other_and_unknown_ids_fail()
    {
        var demo = new DemoStartupService();
        var provider = new StartupActionsProvider(demo);
        Assert.True((await provider.GetStateAsync(StartupActionsProvider.FastStartupOn, CancellationToken.None)).IsApplied);
        Assert.False((await provider.GetStateAsync(StartupActionsProvider.FastStartupOff, CancellationToken.None)).IsApplied);
        Assert.True((await provider.ApplyAsync(StartupActionsProvider.FastStartupOff, CancellationToken.None)).Success);
        Assert.False((await demo.GetBootConfigAsync(CancellationToken.None)).FastStartup);
        Assert.True((await provider.GetStateAsync(StartupActionsProvider.FastStartupOff, CancellationToken.None)).IsApplied);
        Assert.False((await provider.ApplyAsync("startup.nope", CancellationToken.None)).Success);
    }
}

public class DemoStartupServiceTests
{
    [Fact]
    public async Task Demo_service_toggles_adds_and_removes_items()
    {
        var demo = new DemoStartupService();
        var items = await demo.GetStartupItemsAsync(CancellationToken.None);
        Assert.Contains(items, i => i.Kind == StartupKinds.Task);
        var steam = items.First(i => i.Name == "Steam");
        Assert.True((await demo.SetStartupItemEnabledAsync(steam, false, CancellationToken.None)).Success);
        Assert.False((await demo.GetStartupItemsAsync(CancellationToken.None)).First(i => i.Name == "Steam").Enabled);

        Assert.False((await demo.AddStartupItemAsync("", "x.exe", CancellationToken.None)).Success);
        Assert.True((await demo.AddStartupItemAsync("My Tool", @"C:\Tools\tool.exe", CancellationToken.None)).Success);
        var added = (await demo.GetStartupItemsAsync(CancellationToken.None)).First(i => i.Name == "My Tool");
        Assert.True((await demo.RemoveStartupItemAsync(added, CancellationToken.None)).Success);
        Assert.DoesNotContain(await demo.GetStartupItemsAsync(CancellationToken.None), i => i.Name == "My Tool");
    }

    [Fact]
    public async Task Demo_service_protects_core_services_and_normalizes_start_modes()
    {
        var demo = new DemoStartupService();
        Assert.False((await demo.SetServiceStartModeAsync("RpcSs", "Disabled", CancellationToken.None)).Success);
        Assert.True((await demo.SetServiceStartModeAsync("SysMain", "manual", CancellationToken.None)).Success);
        var sysmain = (await demo.GetAutoStartServicesAsync(CancellationToken.None)).First(s => s.Name == "SysMain");
        Assert.Equal(ServiceStartModes.Manual, sysmain.StartMode);
    }

    [Fact]
    public async Task Demo_service_schedules_and_cancels_restarts()
    {
        var demo = new DemoStartupService();
        IRestartCanceller canceller = demo;
        Assert.Equal(10, canceller.RestartDelaySeconds);
        var restart = await demo.RestartToUefiAsync(CancellationToken.None);
        Assert.True(restart.Success);
        Assert.True(restart.RebootRequired);
        Assert.Contains("cancelled", (await canceller.CancelPendingRestartAsync(CancellationToken.None)).Message);
        Assert.Contains("no restart", (await canceller.CancelPendingRestartAsync(CancellationToken.None)).Message);
    }
}
