using PcChecker.Core.Lighting;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Finding RGB hardware without OpenRGB, vendor app conflicts, the combined device list and the SDK reader.</summary>
public class RgbDetectionTests
{
    // ---- ID tables ---------------------------------------------------------------------------------------------

    [Fact]
    public void Id_tables_load_and_hold_the_main_rgb_controllers()
    {
        Assert.True(RgbKnownDevices.UsbCount > 1000, $"only {RgbKnownDevices.UsbCount} USB entries");
        Assert.True(RgbKnownDevices.GpuCount > 800, $"only {RgbKnownDevices.GpuCount} graphics card entries");

        // ASUS Aura (motherboard USB controllers)
        foreach (ushort pid in new ushort[] { 0x1867, 0x1872, 0x18A3 })
        {
            Assert.Equal("ASUS Aura Addressable", RgbKnownDevices.FindUsb(0x0B05, pid)?.Name);
        }

        Assert.Equal("ASUS Aura Motherboard", RgbKnownDevices.FindUsb(0x0B05, 0x18F3)?.Name);
        Assert.Equal("ASUS Aura Motherboard", RgbKnownDevices.FindUsb(0x0B05, 0x19AF)?.Name);
        // MSI Mystic Light, Gigabyte RGB Fusion 2, Corsair, NZXT, Lian Li (ENE vendor ID)
        Assert.Equal("MSI Mystic Light MS_7B93", RgbKnownDevices.FindUsb(0x1462, 0x7B93)?.Name);
        foreach (ushort pid in new ushort[] { 0x5702, 0x5711, 0x8297 })
        {
            Assert.Equal("Gigabyte RGB Fusion 2 USB", RgbKnownDevices.FindUsb(0x048D, pid)?.Name);
        }

        Assert.Equal("Corsair Commander Core", RgbKnownDevices.FindUsb(0x1B1C, 0x0C1C)?.Name);
        Assert.Equal("NZXT Smart Device V2", RgbKnownDevices.FindUsb(0x1E71, 0x2006)?.Name);
        Assert.Equal("Lian Li Uni Hub - SL", RgbKnownDevices.FindUsb(0x0CF2, 0xA100)?.Name);
        Assert.Equal("Lian Li Uni Hub", RgbKnownDevices.FindUsb(0x0CF2, 0x7750)?.Name); // libusb controller, added by hand
        Assert.Null(RgbKnownDevices.FindUsb(0x1B1C, 0xFFFE));
        Assert.Null(RgbKnownDevices.FindUsb(0x8086, 0x7A23));

        var gpu = RgbKnownDevices.FindGpu(0x10DE, 0x2786, 0x1043, 0x88F3);
        Assert.Equal("ASUS ROG STRIX GeForce RTX 4070 Gaming OC", gpu?.Name);
        Assert.Equal("ASUS", gpu?.Brand);
    }

    [Fact]
    public void Brand_and_type_come_from_the_product_name_and_vendor_id()
    {
        Assert.Equal("Lian Li", RgbKnownDevices.BrandOf("Lian Li Uni Hub - SL", 0x0CF2));
        Assert.Equal("HyperX", RgbKnownDevices.BrandOf("HyperX Pulsefire Haste", 0x03F0));
        Assert.Equal("Cooler Master", RgbKnownDevices.BrandOf("Cooler Master MM530", 0x2516));
        Assert.Equal("Wooting", RgbKnownDevices.BrandOf("Wooting One (Legacy)", 0x03EB));

        Assert.Equal(OpenRgbDeviceTypes.Keyboard, RgbKnownDevices.GuessType("Corsair K70 RGB PRO", "CorsairPeripheralV2Controller"));
        Assert.Equal(OpenRgbDeviceTypes.MousePad, RgbKnownDevices.GuessType("HyperX Pulsefire Mat", "HyperXMousematController"));
        Assert.Equal(OpenRgbDeviceTypes.Cooler, RgbKnownDevices.GuessType("NZXT Kraken X3 Series RGB", "NZXTHue2Controller"));
        Assert.Equal(OpenRgbDeviceTypes.Motherboard, RgbKnownDevices.GuessType("MSI Mystic Light MS_7B93", "MSIMotherboardController"));
        Assert.Equal(RgbKnownDevices.LightingController, RgbKnownDevices.GuessType("Corsair Commander Core", "CorsairCommanderCoreController"));
        Assert.Equal(RgbKnownDevices.Microphone, RgbKnownDevices.GuessType("HyperX Quadcast S", "HyperXMicrophoneController"));
        Assert.Equal(OpenRgbDeviceTypes.Headset, RgbKnownDevices.GuessType("Razer Kraken Ultimate", "RazerController"));
        Assert.Equal(OpenRgbDeviceTypes.Mouse, RgbKnownDevices.GuessType("Razer DeathAdder V2", "RazerController"));
        Assert.Equal(OpenRgbDeviceTypes.MousePad, RgbKnownDevices.GuessType("Corsair MM800 RGB Polaris", "CorsairPeripheralController"));

        Assert.True(RgbKnownDevices.IsSharedChipVendor(0x258A)); // Sinowealth
        Assert.False(RgbKnownDevices.IsSharedChipVendor(0x1B1C)); // Corsair
    }

    [Fact]
    public void Table_parser_skips_comments_and_bad_lines()
    {
        var usb = RgbKnownDevices.ParseUsbTable(["# header", "1B1C\t0C1C\tCorsair Commander Core\tCorsairCommanderCoreController", "ZZZZ\t0001\tBad", "1B1C\t0C1D", ""]);
        Assert.Single(usb);
        var gpu = RgbKnownDevices.ParseGpuTable(["10DE\t2786\t1043\t88F3\tASUS ROG STRIX GeForce RTX 4070 Gaming OC\tENESMBusController", "10DE\t2786\t1043"]);
        Assert.Single(gpu);
    }

    // ---- Windows device IDs ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"USB\VID_1B1C&PID_0C1C\7&1E2F3A4B&0&3", 0x1B1C, 0x0C1C)]
    [InlineData(@"HID\VID_0B05&PID_19AF&MI_02\8&2D4BD25E&0&0000", 0x0B05, 0x19AF)]
    [InlineData(@"\\?\hid#vid_1532&pid_0084&mi_00#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 0x1532, 0x0084)]
    [InlineData(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002046d_pid&b023#9&2f4c3d1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 0x046D, 0xB023)]
    public void Usb_ids_parse_from_every_windows_form(string id, int vid, int pid)
    {
        Assert.True(RgbDetectionRules.TryParseUsbIds(id, out var v, out var p));
        Assert.Equal((ushort)vid, v);
        Assert.Equal((ushort)pid, p);
    }

    [Fact]
    public void Pci_ids_parse_with_subsystem_device_first_and_smbus_is_class_0c05()
    {
        Assert.False(RgbDetectionRules.TryParseUsbIds(@"PCI\VEN_10DE&DEV_2786", out _, out _));
        Assert.False(RgbDetectionRules.TryParseUsbIds(null, out _, out _));
        Assert.True(RgbDetectionRules.TryParsePciIds(@"PCI\VEN_10DE&DEV_2786&SUBSYS_88F31043&REV_A1", out var ids));
        Assert.Equal((0x10DE, 0x2786, 0x1043, 0x88F3), ((int)ids.Vendor, (int)ids.Device, (int)ids.SubVendor, (int)ids.SubDevice));
        Assert.False(RgbDetectionRules.TryParsePciIds(@"PCI\VEN_10DE&DEV_2786&REV_A1", out _));

        Assert.True(RgbDetectionRules.IsSmbusController([@"PCI\VEN_8086&DEV_7A23&REV_11", @"PCI\VEN_8086&DEV_7A23&CC_0C0500"]));
        Assert.False(RgbDetectionRules.IsSmbusController([@"PCI\VEN_8086&DEV_7A23&CC_0C0330"]));
    }

    [Theory]
    [InlineData("F4-3600C16D-32GTZN", true)]
    [InlineData("F4-3200C16D-16GTZR", true)]
    [InlineData("F5-6000J3636F16GX2-TZ5RK", true)]
    [InlineData("F5-6000J3040G32GX2-TZ5NR", true)]
    [InlineData("F4-3200C16D-16GVKB", false)]       // Ripjaws V, no lighting
    [InlineData("F5-6000J3636F16GX2-TZ5K", false)]  // Trident Z5 without RGB
    [InlineData("CMW32GX4M2E3200C16", true)]        // Vengeance RGB PRO
    [InlineData("CMH32GX5M2B5600C36", true)]        // Vengeance RGB DDR5
    [InlineData("CMK16GX4M2B3200C16", false)]       // Vengeance LPX
    [InlineData("KF432C16BBA/8", true)]             // FURY Beast RGB
    [InlineData("KF560C36BBEA-16", true)]           // FURY Beast RGB EXPO
    [InlineData("KF432C16BB/8", false)]             // FURY Beast
    [InlineData("HX432C16PB3A/8", true)]            // Predator RGB
    [InlineData("HX432C16PB3/8", false)]
    [InlineData("", false)]
    public void Memory_part_numbers_recognise_rgb_kits_only(string part, bool rgb) =>
        Assert.Equal(rgb, RgbDetectionRules.MemoryRgbLine(part) is not null);

    [Fact]
    public void Build_merges_interfaces_and_adds_memory_board_and_dynamic_lighting()
    {
        var input = new RgbScanInput(
            [
                new(@"USB\VID_1B1C&PID_0C1C\1", [], "iCUE COMMANDER CORE"),
                new(@"HID\VID_1B1C&PID_0C1C&MI_00\2", [], null),
                new(@"HID\VID_1B1C&PID_0C1C&MI_01\3", [], null),
                new(@"USB\VID_046D&PID_C52B\4", [], "Logitech Unifying receiver"),
                new(@"PCI\VEN_10DE&DEV_2786&SUBSYS_88F31043&REV_A1\5", [@"PCI\VEN_10DE&DEV_2786&SUBSYS_88F31043&REV_A1"], "NVIDIA GeForce RTX 4070"),
            ],
            [@"\\?\hid#vid_045e&pid_0b40&col01#1#{4d1e55b2-f16f-11cf-88cb-001111000030}"],
            [new("DIMM_A2", "Kingston", "KF432C16BBA/8"), new("DIMM_B2", "Kingston", "KF432C16BBA/8"), new("DIMM_A1", "Samsung", "M378A1K43EB2-CWE")],
            "Micro-Star International Co., Ltd.",
            "MAG B550 TOMAHAWK (MS-7C91)");

        var found = RgbDetectionRules.Build(input);
        Assert.Single(found, f => f.Key == "usb:1B1C:0C1C");
        Assert.DoesNotContain(found, f => f.VendorId == 0x046D); // the receiver is not an RGB controller
        Assert.Single(found, f => f.Source == RgbFindingSource.GraphicsCard);
        var kit = Assert.Single(found, f => f.Source == RgbFindingSource.Memory); // one row per kit, not per stick
        Assert.EndsWith("2 sticks", kit.Name);
        Assert.Equal("Memory slots DIMM_A2, DIMM_B2", kit.Detail);
        var board = Assert.Single(found, f => f.Source == RgbFindingSource.Motherboard);
        Assert.Equal(RgbFindingConfidence.Possible, board.Confidence);
        Assert.Contains("MSI Mystic Light", board.Name);
        Assert.Single(found, f => f.Source == RgbFindingSource.DynamicLighting);
        Assert.Equal(RgbFindingSource.Motherboard, found[0].Source); // board, memory, graphics card, USB, then the rest

        // A board that already showed its USB lighting controller gets no "possible" hint.
        var withUsb = input with { Devices = [new(@"USB\VID_1462&PID_7B93\1", [], null)] };
        Assert.DoesNotContain(RgbDetectionRules.Build(withUsb), f => f.Source == RgbFindingSource.Motherboard);
    }

    // ---- Vendor app conflicts ----------------------------------------------------------------------------------

    [Fact]
    public void Vendor_apps_are_found_by_process_or_service_name()
    {
        var conflicts = RgbConflictRules.Detect(
            [new("iCUE.exe", 10, null), new("explorer", 11, null), new("LightingService", 12, null)],
            ["LightingService", "Spooler"]);
        Assert.Equal(["icue", "armourycrate"], conflicts.Select(c => c.App.Id));
        var asus = conflicts.Single(c => c.App.Id == "armourycrate");
        Assert.Equal(["LightingService"], asus.Services);
        Assert.True(asus.CanClose);

        // "gcc" only counts as Gigabyte Control Center when it runs from a Gigabyte folder (not a compiler).
        Assert.Empty(RgbConflictRules.Detect([new("gcc", 20, @"C:\msys64\ucrt64\bin\gcc.exe")], []));
        Assert.Empty(RgbConflictRules.Detect([new("gcc", 20, null)], []));
        Assert.Single(RgbConflictRules.Detect([new("GCC", 21, @"C:\Program Files\GIGABYTE\Control Center\GCC.exe")], []));
        Assert.Equal("nzxt cam", RgbConflictRules.NormalizeProcessName(@"C:\Program Files\NZXT CAM\NZXT CAM.exe"));
    }

    [Fact]
    public void Conflicts_are_matched_to_brands_and_the_confirmation_says_what_happens()
    {
        var icue = RgbConflictRules.Catalog.Single(a => a.Id == "icue");
        var signal = RgbConflictRules.Catalog.Single(a => a.Id == "signalrgb");
        var running = new List<RgbConflict> { new(icue, [new("iCUE", 1, null)], ["CorsairService"]) };
        Assert.Same(icue, RgbConflictRules.ConflictFor("Corsair", running)?.App);
        Assert.Null(RgbConflictRules.ConflictFor("Razer", running));
        running.Add(new RgbConflict(signal, [new("SignalRgb", 2, null)], []));
        Assert.Same(signal, RgbConflictRules.ConflictFor("Razer", running)?.App); // SignalRGB drives every brand

        string text = RgbConflictRules.CloseConfirmation(running[0]);
        Assert.Contains("Nothing is uninstalled", text);
        Assert.Contains("CorsairService", text);
        Assert.Contains("fans", text); // iCUE also controls fans and pumps
        Assert.DoesNotContain("fans", RgbConflictRules.CloseConfirmation(running[1]));
    }

    // ---- Combined list and status text -------------------------------------------------------------------------

    private static RgbDevice OpenRgbDevice(int index, string name, string type, string? location, params RgbZone[] zones) =>
        new(index, name, "Vendor", type, null, zones.Sum(z => z.LedCount), zones.Length == 0 ? [new(0, "Zone", 1, 1, 1)] : zones,
            [new(0, "Direct", true, false, true, null, null)], 0, null, location);

    [Fact]
    public void Combined_list_hides_found_hardware_that_openrgb_already_lists()
    {
        var scan = new RgbScanResult(RgbDetectionRules.Build(DemoRgbHardwareService.SampleInput), [], true, true, true, []);
        var devices = new[]
        {
            OpenRgbDevice(0, "ASUS Aura Motherboard", OpenRgbDeviceTypes.Motherboard, @"HID: \\?\HID#VID_0B05&PID_19AF&MI_02#8"),
            OpenRgbDevice(1, "NVIDIA GeForce RTX 4070", OpenRgbDeviceTypes.Gpu, "I2C: NVIDIA GPU"),
        };

        var list = RgbDeviceListBuilder.Build(devices, scan, OpenRgbAvailability.Connected);
        Assert.Equal(2, list.Count(e => e.IsControllable));
        Assert.DoesNotContain(list, e => e.Key == "found:usb:0B05:19AF");                      // matched by location
        Assert.DoesNotContain(list, e => e.Key.StartsWith("found:gpu:", StringComparison.Ordinal)); // OpenRGB lists a GPU
        Assert.Contains(list, e => e.Key == "found:usb:1B1C:0C1C" && e.State == RgbDeviceState.NeedsOpenRgb);
        Assert.Contains(list, e => e.Key == "found:usb:1B1C:1BC4");                             // keyboard not listed by OpenRGB
        Assert.Single(list, e => e.Type == OpenRgbDeviceTypes.Ram);
    }

    [Fact]
    public void Status_and_reason_follow_openrgb_state_conflicts_and_zone_sizes()
    {
        var corsairHub = new RgbHardwareFinding("usb:1B1C:0C1C", "Corsair Commander Core", "Corsair", RgbKnownDevices.LightingController,
            RgbFindingSource.Usb, RgbFindingConfidence.Certain, "USB 1B1C:0C1C", 0x1B1C, 0x0C1C);
        var ram = new RgbHardwareFinding("ram:DIMM_A2", "G.Skill Trident Z RGB", "G.Skill", OpenRgbDeviceTypes.Ram, RgbFindingSource.Memory,
            RgbFindingConfidence.Likely, "Memory slot DIMM_A2");
        var none = RgbScanResult.Empty with { Findings = [corsairHub, ram] };

        var notInstalled = RgbDeviceListBuilder.Build([], none, OpenRgbAvailability.NotInstalled);
        Assert.All(notInstalled, e => Assert.Equal(RgbDeviceListBuilder.StatusNeedsOpenRgb, e.Status));
        Assert.All(notInstalled, e => Assert.EndsWith("Install OpenRGB to control it.", e.Reason));
        Assert.Contains("SDK server is off", RgbDeviceListBuilder.Build([], none, OpenRgbAvailability.RunningWithoutServer)[0].Reason);

        var noPawnIo = none with { SmbusDriverReady = false };
        var connected = RgbDeviceListBuilder.Build([], noPawnIo, OpenRgbAvailability.Connected);
        Assert.Contains("PawnIO", connected.Single(e => e.Type == OpenRgbDeviceTypes.Ram).Reason);
        Assert.DoesNotContain("PawnIO", connected.Single(e => e.Name == "Corsair Commander Core").Reason);

        var icue = RgbConflictRules.Catalog.Single(a => a.Id == "icue");
        var held = none with { Conflicts = [new RgbConflict(icue, [new("iCUE", 1, null)], [])] };
        var blocked = RgbDeviceListBuilder.Build([], held, OpenRgbAvailability.Connected);
        Assert.Equal(RgbDeviceState.Blocked, blocked.Single(e => e.Vendor == "Corsair").State);
        Assert.Equal("Found, but Corsair iCUE is using it", blocked.Single(e => e.Vendor == "Corsair").Status);
        Assert.Equal(RgbDeviceState.NeedsOpenRgb, blocked.Single(e => e.Vendor == "G.Skill").State);

        var unsized = OpenRgbDevice(0, "ASUS Aura Addressable", OpenRgbDeviceTypes.Motherboard, null, new RgbZone(0, "Header 1", 0, 0, 120));
        var setup = RgbDeviceListBuilder.FromOpenRgb(unsized);
        Assert.Equal(RgbDeviceState.NeedsSetup, setup.State);
        Assert.Contains("\"Header 1\" has no LEDs set", setup.Reason);
        Assert.True(setup.IsControllable);

        var unreadable = RgbDeviceListBuilder.FromOpenRgb(unsized with { Problem = "Could not read it." });
        Assert.Equal(RgbDeviceState.Unreadable, unreadable.State);
        Assert.False(unreadable.IsControllable);

        Assert.Equal("No RGB devices found yet.", RgbDeviceListBuilder.Summary([]));
        Assert.Equal("4 RGB devices: 1 ready to control, 1 needs OpenRGB, 1 could not be read, 1 held by another app.",
            RgbDeviceListBuilder.Summary([setup, unreadable, blocked.Single(e => e.Vendor == "Corsair"), blocked.Single(e => e.Vendor == "G.Skill")]));
    }

    [Fact]
    public async Task Demo_shows_three_controllable_and_two_found_devices_held_by_icue()
    {
        var lighting = new DemoLightingService();
        var hardware = new DemoRgbHardwareService();
        var devices = await lighting.GetDevicesAsync(CancellationToken.None);
        var scan = await hardware.ScanAsync(CancellationToken.None);
        var list = RgbDeviceListBuilder.Build(devices, scan, lighting.Availability);

        Assert.Equal(3, list.Count(e => e.IsControllable));
        Assert.Equal(2, list.Count(e => e.State == RgbDeviceState.Blocked));
        Assert.Equal(5, list.Count);
        Assert.Equal("Corsair iCUE", Assert.Single(scan.Conflicts).App.Product);

        Assert.True((await hardware.CloseForSessionAsync(scan.Conflicts[0], CancellationToken.None)).Success);
        var after = await hardware.ScanAsync(CancellationToken.None);
        Assert.Empty(after.Conflicts);
        Assert.Equal(2, RgbDeviceListBuilder.Build(devices, after, OpenRgbAvailability.Connected).Count(e => e.State == RgbDeviceState.NeedsOpenRgb));
    }

    [Fact]
    public void Openrgb_availability_status_lines_and_start_rules()
    {
        Assert.Equal(OpenRgbAvailability.Connected, OpenRgbInstallRules.Availability(true, false, false, false));
        Assert.Equal(OpenRgbAvailability.Unreachable, OpenRgbInstallRules.Availability(false, false, true, true));
        Assert.Equal(OpenRgbAvailability.RunningWithoutServer, OpenRgbInstallRules.Availability(false, true, true, true));
        Assert.Equal(OpenRgbAvailability.InstalledNotRunning, OpenRgbInstallRules.Availability(false, true, true, false));
        Assert.Equal(OpenRgbAvailability.NotInstalled, OpenRgbInstallRules.Availability(false, true, false, false));
        Assert.True(OpenRgbInstallRules.IsLocalHost("localhost"));
        Assert.True(OpenRgbInstallRules.IsLocalHost("127.0.0.1"));
        Assert.False(OpenRgbInstallRules.IsLocalHost("192.168.1.20"));

        Assert.Equal(LightingStatusText.InstalledNotRunning, LightingStatusText.For(OpenRgbAvailability.InstalledNotRunning, "127.0.0.1:6742", null, 0));
        Assert.Equal(LightingStatusText.ServerOff, LightingStatusText.For(OpenRgbAvailability.RunningWithoutServer, "127.0.0.1:6742", null, 0));
        Assert.Equal("OpenRGB's SDK server does not answer at 192.168.1.20:6742.",
            LightingStatusText.For(OpenRgbAvailability.Unreachable, "192.168.1.20:6742", null, 0));
        Assert.Equal("Connected to OpenRGB (3 devices)", LightingStatusText.For(OpenRgbAvailability.Connected, "x", null, 3));

        string[] roots = [@"C:\Program Files", @"C:\Program Files (x86)"];
        Assert.True(OpenRgbInstallRules.MayStart(@"C:\Program Files\OpenRGB\OpenRGB.exe", roots));
        Assert.True(OpenRgbInstallRules.MayStart(@"C:\Program Files\WinGet\Packages\CalcProgrammer1.OpenRGB_Microsoft.Winget.Source_8wekyb3d8bbwe\OpenRGB Windows 64-bit\OpenRGB.exe", roots));
        Assert.False(OpenRgbInstallRules.MayStart(@"C:\Users\me\Downloads\OpenRGB\OpenRGB.exe", roots));
        Assert.False(OpenRgbInstallRules.MayStart(@"C:\Program Files\..\Users\me\OpenRGB.exe", roots));
        Assert.False(OpenRgbInstallRules.MayStart(@"C:\Program Files\Evil\notopenrgb.exe", roots));
        Assert.Contains("Program Files", OpenRgbInstallRules.StartRefusal(@"C:\Users\me\Downloads\OpenRGB\OpenRGB.exe"));

        var install = OpenRgbInstallRules.WingetInstall(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0_x64__8wekyb3d8bbwe\winget.exe");
        Assert.Contains("CalcProgrammer1.OpenRGB", install.Arguments);
        Assert.Contains("machine", install.Arguments);
        Assert.True(OpenRgbInstallRules.InterpretWingetExit(0).Success);
        Assert.True(OpenRgbInstallRules.InterpretWingetExit(unchecked((int)0x8A150061)).Success); // already installed
        Assert.False(OpenRgbInstallRules.InterpretWingetExit(unchecked((int)0x8A150014)).Success);
    }

    // ---- OpenRGB SDK controller data ---------------------------------------------------------------------------

    // Controller data blocks laid out exactly as OpenRGB's SDK documentation (Documentation/OpenRGBSDK.md) specifies
    // for protocol 3 (OpenRGB 0.7/0.8) and protocol 4 (0.9), little-endian: an ASUS Aura addressable controller with
    // a Direct and a Breathing mode. Protocol 3: one resizable header still at 0 LEDs plus a 1-LED logo. Protocol 4: the
    // header set to 30 LEDs with two segments, plus a 1×2 matrix zone.
    private const string Protocol3Sample =
        "5C010000000000001600415355532041757261204164647265737361626C650005004153555300150041535553204175726120555342204465766963" +
        "6500100041554C41332D415233322D30323037000B00393837363534333231300025004849443A205C5C3F5C484944235649445F3042303526504944" +
        "5F31383637264D495F303200020001000000070044697265637400FF0000002000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000100000000000A00427265617468696E67000200000051000000000000000400000000000000FF00000001000000010000" +
        "0002000000FF00000000000000020000000100FF0000000200130041757261204164647265737361626C652031000100000000000000780000000000" +
        "0000000005004C6F676F00000000000100000001000000010000000000010005004C6F676F000000000001000000FF00";

    private const string Protocol4Sample =
        "9F030000000000001600415355532041757261204164647265737361626C650005004153555300150041535553204175726120555342204465766963" +
        "6500100041554C41332D415233322D30323037000B00393837363534333231300025004849443A205C5C3F5C484944235649445F3042303526504944" +
        "5F31383637264D495F303200020001000000070044697265637400FF0000002000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000100000000000A00427265617468696E67000200000051000000000000000400000000000000FF00000001000000010000" +
        "0002000000FF00000000000000020000000100FF0000000200130041757261204164647265737361626C652031000100000000000000780000001E00" +
        "000000000200050046616E7300010000000000000018000000060053747269700001000000180000000600000005004B657973000200000002000000" +
        "02000000020000001000010000000200000000000000010000000000200006004C45442031000000000006004C45442032000100000006004C454420" +
        "33000200000006004C45442034000300000006004C45442035000400000006004C45442036000500000006004C45442037000600000006004C454420" +
        "38000700000006004C45442039000800000007004C4544203130000900000007004C4544203131000A00000007004C4544203132000B00000007004C" +
        "4544203133000C00000007004C4544203134000D00000007004C4544203135000E00000007004C4544203136000F00000007004C4544203137001000" +
        "000007004C4544203138001100000007004C4544203139001200000007004C4544203230001300000007004C4544203231001400000007004C454420" +
        "3232001500000007004C4544203233001600000007004C4544203234001700000007004C4544203235001800000007004C4544203236001900000007" +
        "004C4544203237001A00000007004C4544203238001B00000007004C4544203239001C00000007004C4544203330001D00000007004C454420333100" +
        "1E00000007004C4544203332001F000000200000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000" +
        "FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000" +
        "FF000000FF000000FF000000FF000000FF000000FF000000FF0000";

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void Protocol_3_sample_reads_modes_zones_and_a_resizable_header_at_zero()
    {
        var data = OpenRgbControllerDataReader.TryRead(Bytes(Protocol3Sample), 3, out var error);
        Assert.Null(error);
        Assert.NotNull(data);
        Assert.Equal("ASUS Aura Addressable", data.Name);
        Assert.Equal("ASUS", data.Vendor);
        Assert.Equal(1, data.ActiveMode);
        Assert.Equal(["Direct", "Breathing"], data.Modes.Select(m => m.Name));
        Assert.Equal(255u, data.Modes[1].BrightnessMax);
        Assert.Equal(new RgbColor(255, 0, 0), Assert.Single(data.Modes[1].Colors));
        Assert.Equal(2, data.Zones.Count);
        Assert.Empty(data.Zones[0].Segments); // segments arrived with protocol 4

        var device = OpenRgbControllerDataReader.ToDevice(data, 7);
        Assert.Equal(OpenRgbDeviceTypes.Motherboard, device.Type);
        Assert.Equal(1, device.LedCount);
        Assert.True(device.Zones[0].NeedsSize);
        Assert.Equal(120, device.Zones[0].LedsMax);
        Assert.False(device.Zones[1].IsResizable);
        Assert.True(device.Modes[0].HasColor);      // per-LED colour
        Assert.True(device.Modes[1].HasSpeed);
        Assert.Equal(4, device.Modes[1].MaxSpeed);
        Assert.Equal(RgbDeviceState.NeedsSetup, RgbDeviceListBuilder.FromOpenRgb(device).State);
        Assert.True(RgbDetectionRules.TryParseUsbIds(device.Location, out var vid, out var pid));
        Assert.Equal((0x0B05, 0x1867), ((int)vid, (int)pid));
    }

    [Fact]
    public void Protocol_4_sample_reads_segments_matrix_and_every_led()
    {
        var data = OpenRgbControllerDataReader.TryRead(Bytes(Protocol4Sample), 4, out var error);
        Assert.Null(error);
        Assert.NotNull(data);
        Assert.Equal(30u, data.Zones[0].LedCount);
        Assert.Equal(["Fans", "Strip"], data.Zones[0].Segments.Select(s => s.Name));
        Assert.Equal(24u, data.Zones[0].Segments[1].StartIndex);
        Assert.Equal((1u, 2u), (data.Zones[1].MatrixHeight, data.Zones[1].MatrixWidth));
        Assert.Equal(32, data.LedNames.Count);
        Assert.Equal(32, data.Colors.Count);

        var device = OpenRgbControllerDataReader.ToDevice(data, 0);
        Assert.Equal(32, device.LedCount);
        Assert.Equal(device.LedCount, device.Zones.Sum(z => z.LedCount)); // no off-by-one between zones and LEDs
        Assert.False(device.Zones[0].NeedsSize);
        Assert.Equal(new RgbColor(0, 255, 0), device.Color);
    }

    [Fact]
    public void Reader_refuses_short_or_inconsistent_data_without_throwing()
    {
        var bytes = Bytes(Protocol4Sample);
        Assert.Null(OpenRgbControllerDataReader.TryRead(bytes.AsSpan(0, bytes.Length - 3), 4, out var error));
        Assert.Contains("ends early", error);
        Assert.Null(OpenRgbControllerDataReader.TryRead(bytes, 6, out error));
        Assert.Contains("not supported", error);
        Assert.Null(OpenRgbControllerDataReader.TryRead([], 3, out _));

        // Extra bytes from a newer protocol after the known fields are ignored.
        var longer = bytes.Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray();
        Assert.NotNull(OpenRgbControllerDataReader.TryRead(longer, 4, out _));
    }

    [Fact]
    public void Sdk_packet_headers_round_trip_and_reject_garbage()
    {
        var packet = OpenRgbSdkProbe.BuildPacket(3, OpenRgbSdkProbe.RequestControllerData, [4, 0, 0, 0]);
        Assert.Equal(20, packet.Length);
        Assert.Equal("ORGB"u8.ToArray(), packet[..4]);
        Assert.True(OpenRgbSdkProbe.TryParseHeader(packet, out var device, out var id, out var size));
        Assert.Equal((3u, 1u, 4), (device, id, size));
        Assert.False(OpenRgbSdkProbe.TryParseHeader("XRGB0000000000000000"u8, out _, out _, out _));
        var huge = OpenRgbSdkProbe.BuildPacket(0, 0, []);
        huge[15] = 0xFF;
        Assert.False(OpenRgbSdkProbe.TryParseHeader(huge, out _, out _, out _));
    }

    [Fact]
    public void Zones_map_by_position_and_clamp_resizes()
    {
        var zone = OpenRgbDeviceMapper.MapZone(2, "  ", 0, 0, 120);
        Assert.Equal(2, zone.Index);
        Assert.Equal("Zone 3", zone.Name);
        Assert.True(zone.NeedsSize);
        Assert.Equal(120, LightingApplyPlanner.ClampZoneSize(zone, 500));
        Assert.Equal(0, LightingApplyPlanner.ClampZoneSize(zone, -4));

        var odd = OpenRgbDeviceMapper.MapZone(0, "Fixed", 8, 10, 4); // minimum above maximum: treat as fixed
        Assert.False(odd.IsResizable);
        Assert.Equal(8, LightingApplyPlanner.ClampZoneSize(odd, 20));

        Assert.Equal(8, OpenRgbDeviceMapper.LedCount(0, 8, []));
        Assert.Equal(12, OpenRgbDeviceMapper.LedCount(null, null, [new(0, "A", 5), new(1, "B", 7)]));
    }

    [Fact]
    public async Task Demo_zone_resize_updates_the_device()
    {
        var svc = new DemoLightingService();
        var board = (await svc.GetDevicesAsync(CancellationToken.None)).Single(d => d.Type == OpenRgbDeviceTypes.Motherboard);
        Assert.True(board.Zones[1].IsResizable);
        Assert.True((await svc.ResizeZoneAsync(board.Index, 1, 40, CancellationToken.None)).Success);
        var after = (await svc.GetDevicesAsync(CancellationToken.None)).Single(d => d.Index == board.Index);
        Assert.Equal(40, after.Zones[1].LedCount);
        Assert.Equal(48, after.LedCount);
        Assert.False((await svc.ResizeZoneAsync(board.Index, 0, 40, CancellationToken.None)).Success); // fixed zone
    }
}
