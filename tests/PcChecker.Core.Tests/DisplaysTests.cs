using PcChecker.Core.Displays;
using Xunit;

namespace PcChecker.Core.Tests;

public class DisplaysTests
{
    private static DisplayInfo Display(string name, int x, int y, int w, int h, bool primary, int hz = 60) =>
        new(name, "Monitor " + name[^1], "ASUS", null, primary, new DisplayMode(w, h, hz, 32), 100, x, y, 0, false, false, "HDMI", 27, "GPU",
            [new DisplayMode(w, h, hz, 32), new DisplayMode(1920, 1080, 60, 32)], false, null, null, false);

    [Fact]
    public void Normalize_dedupes_and_sorts_largest_resolution_and_highest_refresh_first()
    {
        var modes = new[]
        {
            new DisplayMode(1920, 1080, 60, 32), new DisplayMode(2560, 1440, 60, 32), new DisplayMode(2560, 1440, 144, 32),
            new DisplayMode(2560, 1440, 144, 32), new DisplayMode(1280, 720, 60, 32), new DisplayMode(0, 0, 0, 0),
        };
        var normalized = DisplayModeMath.Normalize(modes);
        Assert.Equal(4, normalized.Count);
        Assert.Equal(new DisplayMode(2560, 1440, 144, 32), normalized[0]);
        Assert.Equal(new DisplayMode(2560, 1440, 60, 32), normalized[1]);
        Assert.Equal(new DisplayMode(1280, 720, 60, 32), normalized[^1]);
        Assert.Equal([(2560, 1440), (1920, 1080), (1280, 720)], DisplayModeMath.Resolutions(modes));
    }

    [Fact]
    public void Closest_prefers_same_resolution_then_nearest_refresh()
    {
        var modes = new[] { new DisplayMode(2560, 1440, 240, 32), new DisplayMode(2560, 1440, 60, 32), new DisplayMode(1920, 1080, 165, 32) };
        Assert.Equal(new DisplayMode(2560, 1440, 240, 32), DisplayModeMath.Closest(modes, new DisplayMode(2560, 1440, 200, 32)));
        Assert.Equal(new DisplayMode(1920, 1080, 165, 32), DisplayModeMath.Closest(modes, new DisplayMode(1920, 1080, 60, 32)));
        Assert.Null(DisplayModeMath.Closest([], new DisplayMode(1, 1, 1, 32)));
    }

    [Theory]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(3440, 1440, "21:9")]
    [InlineData(5120, 1440, "32:9")]
    [InlineData(1920, 1200, "16:10")]
    [InlineData(1280, 1024, "5:4")]
    [InlineData(2256, 1504, "3:2")]
    public void Aspect_ratio_text_recognizes_common_ratios(int width, int height, string expected) =>
        Assert.Equal(expected, DisplayModeMath.AspectRatioText(width, height));

    [Theory]
    [InlineData(5u, "HDMI")]
    [InlineData(10u, "DisplayPort")]
    [InlineData(18u, "DisplayPort over USB-C")]
    [InlineData(0x80000000u, "Built-in panel")]
    [InlineData(11u, "Embedded DisplayPort (built-in)")]
    [InlineData(999u, "Unknown")]
    public void Output_technology_maps_to_connection_text(uint code, string expected)
    {
        Assert.Equal(expected, DisplayText.OutputTechnologyText(code));
        Assert.Equal(code is 11 or 0x80000000, DisplayText.IsInternalOutput(code));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    [InlineData(-2, false)]
    [InlineData(-5, false)]
    public void Disp_change_codes_have_messages_and_success_flag(int code, bool success)
    {
        Assert.Equal(success, DisplayText.IsDispChangeSuccess(code));
        Assert.False(string.IsNullOrWhiteSpace(DisplayText.DispChangeMessage(code)));
        Assert.Contains("restart", DisplayText.DispChangeMessage(1));
        Assert.Contains("does not support", DisplayText.DispChangeMessage(-2));
    }

    [Fact]
    public void Orientation_helpers_round_trip_and_detect_dimension_swaps()
    {
        Assert.Equal("Portrait", DisplayText.OrientationText(90));
        Assert.Equal("Landscape (flipped)", DisplayText.OrientationText(180));
        Assert.Equal(270, DisplayText.Normalize(-90));
        Assert.Equal(0, DisplayText.Normalize(360));
        Assert.Equal(3, DisplayText.OrientationToDevMode(270));
        Assert.Equal(90, DisplayText.OrientationFromDevMode(1));
        Assert.True(DisplayText.SwapsDimensions(0, 90));
        Assert.False(DisplayText.SwapsDimensions(0, 180));
        Assert.False(DisplayText.SwapsDimensions(90, 270));
    }

    [Fact]
    public void Edid_manufacturer_decodes_and_maps_to_brand()
    {
        Assert.Equal("DEL", DisplayText.DecodeEdidManufacturer(0xAC10));
        Assert.Equal("Dell", DisplayText.ManufacturerName("DEL"));
        Assert.Equal("ASUS", DisplayText.ManufacturerName("AUS"));
        Assert.Equal("ZZZ", DisplayText.ManufacturerName("ZZZ"));
        Assert.Equal(string.Empty, DisplayText.ManufacturerName(null));
        Assert.Equal(27.0, DisplayText.DiagonalInches(60, 34)!.Value, 0);
        Assert.Null(DisplayText.DiagonalInches(0, 34));
        Assert.Equal("27-inch", DisplayText.DiagonalText(26.9));
        Assert.Equal("ASUS PG279QM", DisplayText.WmiText([65, 83, 85, 83, 32, 80, 71, 50, 55, 57, 81, 77, 0, 0]));
    }

    [Fact]
    public void Arrangement_layout_scales_negative_coordinates_into_the_canvas()
    {
        var inputs = new[]
        {
            new ArrangementInput(@"\\.\DISPLAY1", 0, 0, 2560, 1440, true, "Main"),
            new ArrangementInput(@"\\.\DISPLAY2", -1920, 180, 1920, 1080, false, "Left"),
        };
        var rects = ArrangementLayout.Compute(inputs, 560, 230, padding: 10, gap: 0);
        Assert.Equal(2, rects.Count);
        var main = rects[0];
        var left = rects[1];
        Assert.Equal(1, main.Number);
        Assert.True(main.IsPrimary);
        Assert.Equal(2, left.Number);
        Assert.True(left.X >= 10 && left.X < main.X);
        Assert.True(main.X + main.Width <= 560 - 10 + 0.01);
        Assert.True(main.Y + main.Height <= 230 - 10 + 0.01);
        Assert.Equal(2560.0 / 1440, main.Width / main.Height, 2);
        Assert.Equal(main.Width / 2560, left.Width / 1920, 3);
        Assert.Empty(ArrangementLayout.Compute([], 100, 100));
    }

    [Fact]
    public void Arrangement_numbers_follow_display_order()
    {
        var displays = new[] { Display(@"\\.\DISPLAY1", 0, 0, 2560, 1440, true), Display(@"\\.\DISPLAY2", 2560, 0, 1920, 1080, false) };
        Assert.Equal(2, ArrangementLayout.NumberOf(displays, @"\\.\display2"));
        Assert.Equal(0, ArrangementLayout.NumberOf(displays, @"\\.\DISPLAY9"));
        var inputs = ArrangementLayout.FromDisplays(displays);
        Assert.Equal(2560, inputs[1].X);
        Assert.Equal("Monitor 2", inputs[1].Label);
    }

    [Fact]
    public void Profile_capture_describe_and_json_round_trip()
    {
        var displays = new[] { Display(@"\\.\DISPLAY1", 0, 0, 2560, 1440, true, 240), Display(@"\\.\DISPLAY2", 2560, 0, 1920, 1080, false) };
        var profile = DisplayProfiles.Capture("Gaming", displays);
        Assert.Equal(@"\\.\DISPLAY1", profile.PrimaryDevice);
        Assert.Equal(240, profile.Modes[@"\\.\display1"].RefreshHz);
        Assert.Equal("2 displays · DISPLAY1 2560 x 1440 @ 240 Hz (main) · DISPLAY2 1920 x 1080 @ 60 Hz", DisplayProfiles.Describe(profile));

        var json = DisplayProfiles.ToJson(profile);
        var back = DisplayProfiles.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal("Gaming", back!.Name);
        Assert.Equal(profile.Modes[@"\\.\DISPLAY2"], back.Modes[@"\\.\display2"]);
        Assert.Null(DisplayProfiles.FromJson("not json"));
        Assert.Equal("DISPLAY1", DisplayProfiles.ShortDeviceName(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void Profile_matching_reports_active_state_and_missing_devices()
    {
        var displays = new[] { Display(@"\\.\DISPLAY1", 0, 0, 2560, 1440, true, 240), Display(@"\\.\DISPLAY2", 2560, 0, 1920, 1080, false) };
        var profile = DisplayProfiles.Capture("Desk", displays);
        Assert.True(DisplayProfiles.IsActive(profile, displays));

        profile.Modes[@"\\.\DISPLAY1"] = new DisplayMode(2560, 1440, 60, 32);
        Assert.False(DisplayProfiles.IsActive(profile, displays));

        profile.Modes[@"\\.\DISPLAY3"] = new DisplayMode(1920, 1080, 60, 32);
        var (present, missing) = DisplayProfiles.Match(profile, displays);
        Assert.Equal(2, present.Count);
        Assert.Equal([@"\\.\DISPLAY3"], missing);
        Assert.False(DisplayProfiles.IsActive(new DisplayProfile(), displays));
    }

    [Fact]
    public void Profile_name_validation_rejects_empty_and_duplicates()
    {
        var existing = new[] { new DisplayProfile { Name = "Gaming" } };
        Assert.NotNull(DisplayProfiles.ValidateName("  ", existing));
        Assert.NotNull(DisplayProfiles.ValidateName("gaming", existing));
        Assert.Null(DisplayProfiles.ValidateName("gaming", existing, allowOverwrite: true));
        Assert.Null(DisplayProfiles.ValidateName("Work", existing));
        Assert.NotNull(DisplayProfiles.ValidateName(new string('x', 61), existing));
    }

    [Fact]
    public void Monitor_ids_normalize_across_windows_spellings()
    {
        const string interfacePath = @"\\?\DISPLAY#AUS27A1#5&1a2b3c&0&UID4357#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string wmiInstance = @"DISPLAY\AUS27A1\5&1A2B3C&0&UID4357_0";
        Assert.Equal(@"DISPLAY\AUS27A1\5&1A2B3C&0&UID4357", DisplayVendorTools.NormalizeMonitorId(interfacePath));
        Assert.Equal(@"DISPLAY\AUS27A1\5&1A2B3C&0&UID4357", DisplayVendorTools.NormalizeMonitorId(wmiInstance));
        Assert.True(DisplayVendorTools.SameMonitor(interfacePath, wmiInstance));
        Assert.False(DisplayVendorTools.SameMonitor(interfacePath, @"DISPLAY\DEL4321\5&1A2B3C&0&UID4358_0"));
        Assert.False(DisplayVendorTools.SameMonitor(null, null));
    }

    [Fact]
    public void Graphics_vendor_detection_and_control_panel_candidates()
    {
        Assert.Equal(GraphicsVendor.Nvidia, DisplayVendorTools.VendorFrom(@"PCI\VEN_10DE&DEV_2786&SUBSYS_88D21043"));
        Assert.Equal(GraphicsVendor.Amd, DisplayVendorTools.VendorFrom(@"PCI\VEN_1002&DEV_744C"));
        Assert.Equal(GraphicsVendor.Intel, DisplayVendorTools.VendorFrom(null, "Intel Arc A770"));
        Assert.Equal(GraphicsVendor.Unknown, DisplayVendorTools.VendorFrom(@"PCI\VEN_15AD", "VMware SVGA"));

        var nvidia = DisplayVendorTools.ControlPanelCandidates(GraphicsVendor.Nvidia, @"C:\Program Files", @"C:\Windows");
        Assert.True(nvidia.Count >= 3);
        Assert.StartsWith(@"C:\Program Files\NVIDIA Corporation", nvidia[0].Target);
        Assert.True(nvidia[0].IsFilePath);
        Assert.False(nvidia[^1].IsFilePath);

        var amd = DisplayVendorTools.ControlPanelCandidates(GraphicsVendor.Amd, @"C:\Program Files");
        Assert.Contains(amd, c => c.Target.EndsWith("RadeonSoftware.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Single(DisplayVendorTools.ControlPanelCandidates(GraphicsVendor.Unknown, ""));
        Assert.Contains("AMD", DisplayVendorTools.ControlPanelName(GraphicsVendor.Amd));
    }

    [Fact]
    public async Task Demo_display_service_supports_every_operation()
    {
        var svc = new DemoDisplayService();
        var displays = await svc.GetDisplaysAsync(CancellationToken.None);
        Assert.Equal(2, displays.Count);
        var main = displays.Single(d => d.IsPrimary);
        Assert.True(main.HdrSupported);
        Assert.True((await svc.SetModeAsync(main.DeviceName, new DisplayMode(2560, 1440, 144, 32), CancellationToken.None)).Success);
        Assert.True((await svc.SetHdrAsync(main.DeviceName, true, CancellationToken.None)).Success);
        Assert.True((await svc.SetOrientationAsync(@"\\.\DISPLAY2", 90, CancellationToken.None)).Success);
        Assert.True((await svc.SetBrightnessAsync(main.DeviceName, 80, CancellationToken.None)).Success);
        Assert.True((await svc.SetContrastAsync(main.DeviceName, 60, CancellationToken.None)).Success);
        Assert.False((await svc.SetBrightnessAsync(@"\\.\DISPLAY9", 80, CancellationToken.None)).Success);

        var after = await svc.GetDisplaysAsync(CancellationToken.None);
        var mainAfter = after.Single(d => d.IsPrimary);
        Assert.Equal(144, mainAfter.Current.RefreshHz);
        Assert.True(mainAfter.HdrEnabled);
        Assert.Equal(80, mainAfter.Brightness);
        Assert.Equal(90, after.Single(d => d.DeviceName == @"\\.\DISPLAY2").OrientationDegrees);

        var profile = DisplayProfiles.Capture("Test", displays);
        Assert.True((await svc.ApplyProfileAsync(profile, CancellationToken.None)).Success);
        Assert.Equal(60, (await svc.GetDisplaysAsync(CancellationToken.None)).Single(d => d.IsPrimary).Current.RefreshHz);
        Assert.True(svc.OpenWindowsDisplaySettings().Success);
        Assert.True(svc.OpenGpuControlPanel().Success);
    }
}
