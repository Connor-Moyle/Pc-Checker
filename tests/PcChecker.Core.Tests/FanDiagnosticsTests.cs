using PcChecker.Core.Checkup;
using PcChecker.Core.Fans;
using PcChecker.Core.Optimization;
using PcChecker.Core.Sensors;
using PcChecker.Core.Sensors.PawnIo;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Motherboard fans missing: PawnIO detection and install commands, fan list rules, conflicts and the diagnostic report.</summary>
public class FanDiagnosticsTests
{
    private const string Chip = "/lpc/nct6798d/0";

    private static FanSensorInfo Rpm(int index, double? value, string hw = Chip, HardwareKind kind = HardwareKind.Motherboard, string name = "") =>
        new($"{hw}/fan/{index}", hw, kind == HardwareKind.Gpu ? "NVIDIA GeForce RTX 4070" : "Nuvoton NCT6798D", kind, SensorKind.Fan,
            name.Length > 0 ? name : $"Fan #{index + 1}", index, value);

    private static FanSensorInfo Control(int index, double? value, string hw = Chip, HardwareKind kind = HardwareKind.Motherboard, string name = "") =>
        new($"{hw}/control/{index}", hw, kind == HardwareKind.Gpu ? "NVIDIA GeForce RTX 4070" : "Nuvoton NCT6798D", kind, SensorKind.Control,
            name.Length > 0 ? name : $"Fan #{index + 1}", index, value);

    private static FanDevice Device(string id, HardwareKind kind, double? rpm, bool controllable = true, bool hasRpmSensor = true, FanMode mode = FanMode.Auto) =>
        new(id, id, kind, "chip", rpm, controllable ? 40 : null, controllable, 0, 100, mode, controllable ? id + ".control" : null, hasRpmSensor ? id + ".rpm" : null);

    private static readonly PawnIoStatus Running = new(PawnIoState.Running, "2.2.0", true, true);
    private static readonly PawnIoStatus Missing = new(PawnIoState.NotInstalled, null, false, false);

    // ---- PawnIO state ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, null, false, false, PawnIoState.NotInstalled)]
    [InlineData(true, "2.2.0", true, false, PawnIoState.InstalledNotRunning)]
    [InlineData(true, null, true, false, PawnIoState.InstalledNotRunning)] // service left behind, no uninstall entry
    [InlineData(true, null, false, true, PawnIoState.Running)]            // the device opens: running whatever the registry says
    [InlineData(true, "2.2.0", true, null, PawnIoState.InstalledNotRunning)]
    [InlineData(true, null, null, null, PawnIoState.Unknown)]
    [InlineData(true, null, false, null, PawnIoState.NotInstalled)]
    [InlineData(false, "2.2.0", true, true, PawnIoState.NotSupported)]
    public void PawnIo_state_is_classified_from_registry_service_and_device(bool windows, string? version, bool? service, bool? device, PawnIoState expected) =>
        Assert.Equal(expected, PawnIoRules.Classify(windows, version, service, device));

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]  // file not found: no device
    [InlineData(3, false)]
    [InlineData(5, true)]   // access denied: the device exists
    [InlineData(32, true)]
    [InlineData(87, null)]
    public void PawnIo_device_open_errors_are_read_correctly(int error, bool? expected) =>
        Assert.Equal(expected, PawnIoRules.DeviceOpenResult(error));

    [Fact]
    public void PawnIo_status_needs_attention_only_when_missing_or_stopped()
    {
        Assert.True(Missing.NeedsAttention);
        Assert.True(new PawnIoStatus(PawnIoState.InstalledNotRunning, "2.2.0", true, false).NeedsAttention);
        Assert.False(Running.NeedsAttention);
        Assert.False(PawnIoStatus.NotSupported.NeedsAttention);
        Assert.Contains("2.2.0", Running.Summary);
        Assert.Contains("Restart", new PawnIoStatus(PawnIoState.InstalledNotRunning, null, true, false).Summary);
        Assert.Equal(SensorWarnings.PawnIoMissing, SensorWarnings.DriverProblem(PawnIoState.NotInstalled));
        Assert.Equal(SensorWarnings.PawnIoNotRunning, SensorWarnings.DriverProblem(PawnIoState.InstalledNotRunning));
        Assert.Null(SensorWarnings.DriverProblem(PawnIoState.Running));
        Assert.Null(SensorWarnings.DriverProblem(PawnIoState.Unknown));
    }

    // ---- Install commands -----------------------------------------------------------------------------------------

    [Fact]
    public void Winget_command_installs_the_exact_official_package_silently()
    {
        var command = PawnIoRules.WingetInstall(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.24.25200.0_x64__8wekyb3d8bbwe\winget.exe");
        Assert.Equal(
            ["install", "--id", "namazso.PawnIO", "-e", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements", "--silent", "--disable-interactivity"],
            command.Arguments);
        Assert.StartsWith("winget.exe install --id namazso.PawnIO -e", command.Display);
    }

    [Fact]
    public void Setup_command_uses_the_documented_silent_switches_or_none()
    {
        var silent = PawnIoRules.SetupInstall(@"C:\ProgramData\PcChecker-pawnio-1\PawnIO_setup.exe");
        Assert.Equal(["-install", "-silent"], silent.Arguments);
        Assert.Equal("PawnIO_setup.exe -install -silent", silent.Display);
        Assert.Empty(PawnIoRules.SetupInstall("PawnIO_setup.exe", silent: false).Arguments);
        Assert.Equal("tool.exe --name \"two words\"", new InstallCommand(@"C:\x\tool.exe", ["--name", "two words"]).Display);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(unchecked((int)0x8A150061), true, false)] // already installed
    [InlineData(unchecked((int)0x8A150109), true, true)]
    [InlineData(unchecked((int)0x8A150014), false, false)] // no package found
    [InlineData(1, false, false)]
    public void Winget_exit_codes_are_interpreted(int code, bool success, bool reboot)
    {
        var (ok, needsReboot, problem) = PawnIoRules.InterpretWingetExit(code);
        Assert.Equal(success, ok);
        Assert.Equal(reboot, needsReboot);
        Assert.Equal(success, problem is null);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1223, false, false)]
    [InlineData(5, false, false)]
    public void Setup_exit_codes_are_interpreted(int code, bool success, bool reboot)
    {
        var (ok, needsReboot, _) = PawnIoRules.InterpretSetupExit(code);
        Assert.Equal(success, ok);
        Assert.Equal(reboot, needsReboot);
    }

    [Theory]
    [InlineData("https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/123", true)]
    [InlineData("https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest", true)]
    [InlineData("http://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe", false)]
    [InlineData("https://github.com.evil.example/PawnIO_setup.exe", false)]
    [InlineData("https://user@github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe", false)]
    [InlineData("https://github.com:8443/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe", false)]
    [InlineData("https://pawnio.eu/PawnIO_setup.exe", false)]
    public void Only_https_github_hosts_are_trusted_for_the_download(string url, bool trusted) =>
        Assert.Equal(trusted, PawnIoRules.IsTrustedDownload(new Uri(url)));

    [Fact]
    public void The_setup_asset_is_taken_only_from_the_official_repository()
    {
        const string release = """
            {"tag_name":"2.2.0","draft":false,"assets":[
              {"name":"PawnIO_setup.exe.sig","browser_download_url":"https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe.sig","size":10},
              {"name":"PawnIO_setup.exe","browser_download_url":"https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe","size":1234567}]}
            """;
        var asset = PawnIoRules.FindSetupAsset(release);
        Assert.NotNull(asset);
        Assert.Equal("2.2.0", asset!.Tag);
        Assert.Equal(1234567, asset.Size);
        Assert.EndsWith("/PawnIO_setup.exe", asset.DownloadUrl.AbsolutePath);

        Assert.Null(PawnIoRules.FindSetupAsset(release.Replace("namazso/PawnIO.Setup", "someone/PawnIO.Setup")));
        Assert.Null(PawnIoRules.FindSetupAsset(release.Replace("\"draft\":false", "\"draft\":true")));
        Assert.Null(PawnIoRules.FindSetupAsset("{\"assets\":[]}"));
        Assert.Null(PawnIoRules.FindSetupAsset("not json"));
        Assert.Null(PawnIoRules.FindSetupAsset(null));
    }

    [Fact]
    public void The_newest_protected_winget_for_this_architecture_is_picked()
    {
        string[] folders =
        [
            "Microsoft.DesktopAppInstaller_1.21.3482.0_x64__8wekyb3d8bbwe",
            "Microsoft.DesktopAppInstaller_1.24.25200.0_x64__8wekyb3d8bbwe",
            "Microsoft.DesktopAppInstaller_1.25.1.0_arm64__8wekyb3d8bbwe",
            "Microsoft.DesktopAppInstaller_1.99.0.0_x64__notmicrosoft",
            "Microsoft.DesktopAppInstaller_1.24.25200.0_neutral_~_8wekyb3d8bbwe",
        ];
        Assert.Equal("Microsoft.DesktopAppInstaller_1.24.25200.0_x64__8wekyb3d8bbwe", PawnIoRules.PickWingetFolder(folders, "x64"));
        Assert.Equal("Microsoft.DesktopAppInstaller_1.25.1.0_arm64__8wekyb3d8bbwe", PawnIoRules.PickWingetFolder(folders, "arm64"));
        Assert.Null(PawnIoRules.PickWingetFolder(["Something.Else_1.0.0.0_x64__8wekyb3d8bbwe"], "x64"));
    }

    // ---- Fan list rules -------------------------------------------------------------------------------------------

    [Fact]
    public void Fans_are_classified_as_controllable_control_only_or_read_only()
    {
        Assert.Equal(FanCapability.Controllable, Device("a", HardwareKind.Motherboard, 900).Capability);
        Assert.Equal(FanCapability.ControlWithoutRpm, Device("b", HardwareKind.Motherboard, null, hasRpmSensor: false).Capability);
        Assert.Equal(FanCapability.ReadOnly, Device("c", HardwareKind.Motherboard, 1200, controllable: false).Capability);
        Assert.Null(FanListRules.Describe(FanCapability.Controllable, HardwareKind.Motherboard));
        Assert.Contains("Read-only", FanListRules.Describe(FanCapability.ReadOnly, HardwareKind.Motherboard));
        Assert.Contains("USB", FanListRules.Describe(FanCapability.ReadOnly, HardwareKind.Cooler));
        Assert.Contains("without RPM", FanListRules.Describe(FanCapability.ControlWithoutRpm, HardwareKind.Motherboard));
    }

    [Fact]
    public void Only_idle_motherboard_headers_at_zero_rpm_count_as_empty()
    {
        Assert.True(FanListRules.IsEmptyHeader(Device("mb", HardwareKind.Motherboard, 0), everSpun: false));
        Assert.True(FanListRules.IsEmptyHeader(Device("ro", HardwareKind.Motherboard, 0, controllable: false), everSpun: false));
        Assert.False(FanListRules.IsEmptyHeader(Device("gpu", HardwareKind.Gpu, 0), everSpun: false));           // zero-RPM idle GPU fans stay
        Assert.False(FanListRules.IsEmptyHeader(Device("spun", HardwareKind.Motherboard, 0), everSpun: true));    // stopped, but was spinning
        Assert.False(FanListRules.IsEmptyHeader(Device("manual", HardwareKind.Motherboard, 0, mode: FanMode.Manual), everSpun: false));
        Assert.False(FanListRules.IsEmptyHeader(Device("unread", HardwareKind.Motherboard, null), everSpun: false));
        Assert.False(FanListRules.IsEmptyHeader(Device("norpm", HardwareKind.Motherboard, null, hasRpmSensor: false), everSpun: false));
        Assert.False(FanListRules.IsEmptyHeader(Device("spinning", HardwareKind.Motherboard, 850), everSpun: false));
    }

    [Fact]
    public void Empty_headers_are_hidden_until_shown_and_a_header_that_spun_once_stays_visible()
    {
        var everSpun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fans = new[] { Device("cpu", HardwareKind.Motherboard, 1200), Device("h5", HardwareKind.Motherboard, 0), Device("h6", HardwareKind.Motherboard, 600) };

        var (visible, hidden) = FanListRules.Visible(fans, showEmptyHeaders: false, everSpun);
        Assert.Equal(["cpu", "h6"], visible.Select(f => f.Id));
        Assert.Equal(1, hidden);

        (visible, hidden) = FanListRules.Visible(fans, showEmptyHeaders: true, everSpun);
        Assert.Equal(3, visible.Count);
        Assert.Equal(0, hidden);

        // h6 stops (BIOS zero-RPM mode): it was seen spinning, so it does not vanish from the page.
        (visible, hidden) = FanListRules.Visible([fans[0], fans[1], Device("h6", HardwareKind.Motherboard, 0)], showEmptyHeaders: false, everSpun);
        Assert.Equal(["cpu", "h6"], visible.Select(f => f.Id));
        Assert.Equal(1, hidden);
    }

    // ---- Conflicting software -------------------------------------------------------------------------------------

    [Fact]
    public void Conflicting_programs_are_matched_by_process_name_once_per_product()
    {
        var matches = FanConflictCatalog.Match(
        [
            "explorer", "ArmouryCrate.Service", "ArmourySocketServer.exe", "iCUE.exe", "NZXT CAM", "HWiNFO64", "FanControl",
            "SignalRgbLauncher", "MSI.CentralServer", "speedfan.exe", "svchost", "gcc", "camera", null, "  ",
        ]);
        Assert.Equal(
            ["ASUS Armoury Crate", "Corsair iCUE", "NZXT CAM", "MSI Center / Dragon Center", "SignalRGB", "Fan Control (Rem0o)", "HWiNFO", "SpeedFan"],
            matches.Select(m => m.Product));
        Assert.Equal("ArmouryCrate.Service", matches[0].ProcessName);
        Assert.Equal("HWiNFO64", matches.Single(m => m.Product == "HWiNFO").ProcessName);
    }

    [Fact]
    public void Conflict_matching_does_not_fire_on_lookalike_names()
    {
        Assert.Empty(FanConflictCatalog.Match(["FanControlHelper2", "fancontrolx", "speedfanatic", "notepad", "Teams", "SIVtool"]));
        Assert.Equal("armourycrateservice", FanConflictCatalog.Normalize("ArmouryCrate.Service.EXE"));
        Assert.Equal("nzxtcam", FanConflictCatalog.Normalize("NZXT CAM.exe"));
    }

    // ---- Diagnostics report ---------------------------------------------------------------------------------------

    private static FanDiagnosticInput GpuOnlyInput(PawnIoStatus pawnIo, IReadOnlyList<ConflictingApp>? conflicts = null)
    {
        var sensors = new[] { Rpm(0, 1450, "/gpu-nvidia/0", HardwareKind.Gpu, "GPU Fan 1"), Control(0, 38, "/gpu-nvidia/0", HardwareKind.Gpu, "GPU Fan 1") };
        var nodes = new[]
        {
            new FanHardwareNode("ASUS ROG STRIX B550-F GAMING", "Motherboard", HardwareKind.Motherboard, 0, 0, 0, 0),
            new FanHardwareNode("NVIDIA GeForce RTX 4070", "GpuNvidia", HardwareKind.Gpu, 0, 20, 1, 1),
        };
        return new FanDiagnosticInput(pawnIo, true, null, nodes, sensors, FanPairing.Pair(sensors), conflicts ?? [], false);
    }

    [Fact]
    public void Report_for_gpu_fans_only_points_at_pawnio_and_names_the_missing_chip()
    {
        var report = FanDiagnostics.Build(GpuOnlyInput(Missing));
        Assert.Equal("ASUS ROG STRIX B550-F GAMING", report.MotherboardName);
        Assert.Equal(FanDiagnostics.NoChipText, report.ChipText);
        Assert.Equal("PawnIO is not installed", report.Findings[0].Title);
        Assert.True(report.Findings[0].IsProblem);
        Assert.Contains("PawnIO driver: PawnIO is not installed", report.Text);
        Assert.Contains("Fan controller chip: no fan controller chip found", report.Text);
        Assert.Contains("GPU Fan 1\" | 1450 RPM | paired with \"GPU Fan 1\"", report.Text);
    }

    [Fact]
    public void Report_with_pawnio_running_but_no_chip_blames_the_chip_and_lists_conflicts()
    {
        var report = FanDiagnostics.Build(GpuOnlyInput(Running, [new ConflictingApp("Corsair iCUE", "iCUE")]));
        Assert.DoesNotContain(report.Findings, f => f.Title.StartsWith("PawnIO", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Title == "No fan controller chip was found on the motherboard");
        Assert.Contains(report.Findings, f => f.Title == "Corsair iCUE is running" && f.IsProblem);
        Assert.Contains("Corsair iCUE (iCUE) - close it while using fan control", report.Text);

        var laptop = FanDiagnostics.Build(GpuOnlyInput(Running) with { IsLaptop = true });
        Assert.Contains("embedded controller", laptop.Findings.Single(f => f.Title.StartsWith("No fan controller chip", StringComparison.Ordinal)).Advice);
    }

    [Fact]
    public void Report_lists_every_sensor_with_value_index_and_pairing()
    {
        var sensors = new[]
        {
            Rpm(0, 1210), Control(0, 45),          // paired
            Rpm(1, 0),                             // empty header, read-only
            Rpm(3, 980),                           // read-only
            Control(6, 60),                        // control without RPM
        };
        var nodes = new[]
        {
            new FanHardwareNode("MSI MAG B650 TOMAHAWK", "Motherboard", HardwareKind.Motherboard, 0, 0, 0, 0),
            new FanHardwareNode("Nuvoton NCT6798D", "SuperIO", HardwareKind.Motherboard, 1, 40, 3, 2),
        };
        var input = new FanDiagnosticInput(Running, true, null, nodes, sensors, FanPairing.Pair(sensors), [], false);
        var report = FanDiagnostics.Build(input, new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero));

        Assert.Equal(["Nuvoton NCT6798D"], report.FanControllerChips);
        Assert.Equal(5, report.SensorRows.Count);
        Assert.Contains(report.SensorRows, r => r.Kind == "Control" && r.Index == 6 && r.Pairing == "control without RPM reading" && r.Value == "60 %");
        Assert.Contains(report.SensorRows, r => r.Kind == "Fan" && r.Index == 3 && r.Pairing.StartsWith("read-only", StringComparison.Ordinal));
        Assert.Contains(report.SensorRows, r => r.Kind == "Fan" && r.Index == 0 && r.Pairing.StartsWith("paired with", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Title == "1 motherboard header reads 0 RPM");
        Assert.Contains(report.Findings, f => f.Title == "1 control without RPM reading");
        Assert.Contains(report.Findings, f => f.Title == "2 fans are read-only");
        Assert.Contains("Created: 2026-10-27 09:30", report.Text);
        Assert.Contains("    Nuvoton NCT6798D [SuperIO] 40 sensors, 3 fan, 2 control", report.Text);
        Assert.Contains("Why a fan cannot be controlled:", report.Text);
        Assert.Contains(FanDiagnostics.WhyNoControl, r => r.Contains("Q-Fan", StringComparison.Ordinal));
        Assert.Contains(FanDiagnostics.WhyNoControl, r => r.Contains("DC", StringComparison.Ordinal) && r.Contains("PWM", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_contains_no_personal_data()
    {
        var report = FanDiagnostics.Build(GpuOnlyInput(Running, [new ConflictingApp("HWiNFO", "HWiNFO64")]), DateTimeOffset.Now);
        Assert.DoesNotContain(Environment.UserName, report.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, report.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath(), report.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 3 } home ? home : "\0", report.Text);
    }

    [Fact]
    public void Report_without_problems_says_so()
    {
        var sensors = new[] { Rpm(0, 1210), Control(0, 45) };
        var nodes = new[]
        {
            new FanHardwareNode("Board", "Motherboard", HardwareKind.Motherboard, 0, 0, 0, 0),
            new FanHardwareNode("ITE IT8689E", "SuperIO", HardwareKind.Motherboard, 1, 30, 1, 1),
        };
        var report = FanDiagnostics.Build(new FanDiagnosticInput(Running, true, null, nodes, sensors, FanPairing.Pair(sensors), [], false));
        Assert.Equal("No problems found", Assert.Single(report.Findings).Title);
    }

    [Fact]
    public async Task Demo_diagnostics_show_a_read_only_fan_an_empty_header_and_a_control_without_rpm()
    {
        using var demo = new DemoFanControlService();
        var fans = await demo.GetFansAsync(CancellationToken.None);
        Assert.Contains(fans, f => f.Capability == FanCapability.ReadOnly);
        Assert.Contains(fans, f => FanListRules.IsEmptyHeader(f, everSpun: false));

        var report = await demo.GetDiagnosticsAsync(CancellationToken.None);
        Assert.True(report.PawnIo.IsRunning);
        Assert.Equal(["Nuvoton NCT6798D"], report.FanControllerChips);
        Assert.Contains(report.Findings, f => f.Title.Contains("read-only", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Title.Contains("0 RPM", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Title.Contains("control without RPM", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Title == "HWiNFO is running");

        var reload = await demo.ReloadSensorsAsync(CancellationToken.None);
        Assert.True(reload.Success);
        Assert.True(reload.After.ChipFound);
        Assert.Empty(demo.SoftwareControlledFanIds);
    }

    // ---- Reload text and sensor warnings --------------------------------------------------------------------------

    [Fact]
    public void Reload_text_says_how_many_motherboard_fans_appeared()
    {
        var before = MotherboardSensorCounts.None;
        Assert.Equal("Sensors reloaded: 5 motherboard fans and 4 fan controls appeared (38 motherboard sensors in total).",
            SensorReloadText.Describe(before, new MotherboardSensorCounts(5, 4, 38, true)));
        Assert.Equal("Sensors reloaded: 1 motherboard fan and 1 fan control found (12 motherboard sensors in total).",
            SensorReloadText.Describe(new MotherboardSensorCounts(1, 1, 12, true), new MotherboardSensorCounts(1, 1, 12, true)));
        Assert.Contains("still no motherboard fan controller chip", SensorReloadText.Describe(before, before));
    }

    [Fact]
    public void Sensor_warnings_explain_a_board_without_a_readable_chip()
    {
        var withoutDriver = SensorWarnings.Build(false, hasMotherboard: true, hasMotherboardChip: false, hasGpu: true, SensorWarnings.PawnIoMissing, "hint");
        Assert.Equal(SensorWarnings.PawnIoMissing, withoutDriver[0]);
        Assert.Contains(withoutDriver, n => n.Contains("Only graphics card fans", StringComparison.Ordinal));

        var noChip = SensorWarnings.Build(true, hasMotherboard: true, hasMotherboardChip: false, hasGpu: true, null, "hint");
        Assert.Contains("No motherboard sensor chip was found", Assert.Single(noChip));

        Assert.Empty(SensorWarnings.Build(true, true, true, true, null, "hint"));
    }

    // ---- Check-up --------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_up_offers_the_pawnio_install_without_a_restore_point_and_never_ticks_it_by_default()
    {
        var item = Assert.Single(CheckupItemMapper.FromPawnIo(Missing));
        Assert.Equal(CheckupFixKind.InstallPawnIo, item.Fix);
        Assert.Equal(CheckupSource.Sensors, item.Source);
        Assert.Equal(RiskLevel.Moderate, item.Risk);
        Assert.False(item.SuggestByDefault);
        Assert.Equal(CheckupRestorePointNeed.None, CheckupBatchPlanner.RestorePointNeed(CheckupFixKind.InstallPawnIo));
        Assert.False(CheckupBatchPlanner.CanStopPartWay(CheckupFixKind.InstallPawnIo));
        var plan = CheckupBatchPlanner.Plan([item], advancedMode: false, restorePointsOn: true);
        Assert.False(plan.SharedRestorePoint);
        Assert.Equal(CheckupFixKind.InstallPawnIo, Assert.Single(plan.Steps).Kind);

        var restart = Assert.Single(CheckupItemMapper.FromPawnIo(new PawnIoStatus(PawnIoState.InstalledNotRunning, "2.2.0", true, false)));
        Assert.False(restart.HasFix);
        Assert.Empty(CheckupItemMapper.FromPawnIo(Running));

        var results = CheckupScanResults.Empty(DateTimeOffset.Now) with { PawnIo = Missing };
        Assert.Contains(CheckupItemMapper.MapAll(results), i => i.Id == "sensors.pawnio");
    }

    [Fact]
    public async Task Demo_pawnio_install_reports_success_without_touching_the_machine()
    {
        var pawnIo = new DemoPawnIoService(running: false);
        Assert.Equal(PawnIoState.NotInstalled, pawnIo.GetStatus().State);
        var result = await pawnIo.InstallAsync(null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.After.IsRunning);
        Assert.True(pawnIo.GetStatus().IsRunning);
    }
}
