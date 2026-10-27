using System.Text.Json;
using OpenRGB.NET;
using PcChecker.Core.Lighting;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Pure lighting logic: colour maths, OpenRGB mapping, status wording, profiles and apply planning.</summary>
public class LightingTests
{
    private static readonly RgbMode[] Modes =
    [
        new(0, "Direct", true, false, true, null, null),
        new(1, "Static", true, false, true, null, null),
        new(2, "Breathing", true, true, true, 0, 100),
        new(3, "Rainbow", false, true, true, 5, 50),
        new(4, "Off", false, false, false, null, null),
    ];

    private static RgbDevice Device(string name = "Keyboard X", string type = OpenRgbDeviceTypes.Keyboard, int active = 3) =>
        new(0, name, "Vendor", type, null, 10, [new(0, "Keys", 10)], Modes, active, null);

    // ---- RgbColorMath -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("#4CC2FF", 0x4C, 0xC2, 0xFF)]
    [InlineData("4cc2ff", 0x4C, 0xC2, 0xFF)]
    [InlineData("0xFF0000", 0xFF, 0, 0)]
    [InlineData("#0F8", 0, 0xFF, 0x88)]
    public void Hex_parses_common_forms_and_round_trips(string text, int r, int g, int b)
    {
        Assert.True(RgbColorMath.TryParseHex(text, out var color));
        Assert.Equal(new RgbColor((byte)r, (byte)g, (byte)b), color);
        Assert.Equal(color, RgbColor.FromHex(color.ToHex()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void Hex_rejects_invalid_text(string? text)
    {
        Assert.False(RgbColorMath.TryParseHex(text, out _));
        Assert.Throws<FormatException>(() => RgbColor.FromHex(text ?? string.Empty));
    }

    [Theory]
    [InlineData(0, 1, 1, 255, 0, 0)]
    [InlineData(120, 1, 1, 0, 255, 0)]
    [InlineData(240, 1, 1, 0, 0, 255)]
    [InlineData(60, 1, 0.5, 128, 128, 0)]
    [InlineData(0, 0, 1, 255, 255, 255)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    public void Hsv_to_rgb_hits_primary_colours(double h, double s, double v, int r, int g, int b)
    {
        var c = RgbColorMath.FromHsv(h, s, v);
        Assert.Equal((byte)r, c.R);
        Assert.Equal((byte)g, c.G);
        Assert.Equal((byte)b, c.B);
    }

    [Fact]
    public void Hsv_round_trips_and_wraps_hue()
    {
        var original = new RgbColor(0x4C, 0xC2, 0xFF);
        var (h, s, v) = RgbColorMath.ToHsv(original);
        Assert.InRange(h, 200, 210);
        var back = RgbColorMath.FromHsv(h, s, v);
        Assert.InRange(Math.Abs(back.R - original.R), 0, 1);
        Assert.InRange(Math.Abs(back.G - original.G), 0, 1);
        Assert.InRange(Math.Abs(back.B - original.B), 0, 1);
        Assert.Equal(RgbColorMath.FromHsv(0, 1, 1), RgbColorMath.FromHsv(360, 1, 1));
        Assert.Equal(RgbColorMath.FromHsv(120, 1, 1), RgbColorMath.FromHsv(-240, 1, 1));
    }

    [Fact]
    public void Brightness_scaling_dims_and_clamps()
    {
        var white = RgbColor.White;
        Assert.Equal(new RgbColor(128, 128, 128), RgbColorMath.Scale(white, 50));
        Assert.Equal(RgbColor.Black, RgbColorMath.Scale(white, 0));
        Assert.Equal(white, RgbColorMath.Scale(white, 100));
        Assert.Equal(white, RgbColorMath.Scale(white, 250));
        Assert.Equal(RgbColor.Black, RgbColorMath.Scale(white, -5));
    }

    [Fact]
    public void Rainbow_palette_has_requested_length_and_increasing_hue()
    {
        var palette = RgbColorMath.Rainbow(12);
        Assert.Equal(12, palette.Count);
        Assert.Equal(new RgbColor(255, 0, 0), palette[0]);
        double last = -1;
        foreach (var c in palette)
        {
            var (h, _, _) = RgbColorMath.ToHsv(c);
            Assert.True(h > last, $"hue {h} should be after {last}");
            last = h;
        }

        Assert.Empty(RgbColorMath.Rainbow(0));
        Assert.Equal(6, LightingApplyPlanner.RainbowFill(6).Length);
    }

    // ---- OpenRGB mapping -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DeviceType.Motherboard, "Motherboard", false)]
    [InlineData(DeviceType.Dram, "RAM", false)]
    [InlineData(DeviceType.Gpu, "GPU", false)]
    [InlineData(DeviceType.Ledstrip, "LED strip", false)]
    [InlineData(DeviceType.Keyboard, "Keyboard", true)]
    [InlineData(DeviceType.Mouse, "Mouse", true)]
    [InlineData(DeviceType.Headset, "Headset", true)]
    [InlineData(DeviceType.Unknown, "Other", false)]
    public void Device_types_get_friendly_names_and_peripheral_flag(DeviceType type, string expected, bool peripheral)
    {
        var name = OpenRgbDeviceTypes.FriendlyName(type);
        Assert.Equal(expected, name);
        Assert.Equal(peripheral, OpenRgbDeviceTypes.IsPeripheral(name));
        Assert.Equal(expected, OpenRgbDeviceTypes.FriendlyName((int)type));
    }

    [Fact]
    public void Unknown_type_codes_and_fan_names_are_refined()
    {
        Assert.Equal("Other", OpenRgbDeviceTypes.FriendlyName(999));
        Assert.Equal("Case fan", OpenRgbDeviceTypes.Refine("Other", "Lian Li Uni Fan SL120"));
        Assert.Equal("Keyboard", OpenRgbDeviceTypes.Refine("Other", "Generic USB Keyboard"));
        Assert.Equal("GPU", OpenRgbDeviceTypes.Refine("GPU", "Some Fan Controller"));
        Assert.Equal("Other", OpenRgbDeviceTypes.Refine("Other", null));
    }

    [Fact]
    public void Mode_flags_map_to_colour_speed_and_brightness_support()
    {
        var direct = OpenRgbDeviceMapper.MapMode(0, "Direct", ModeFlags.HasPerLedColor | ModeFlags.HasBrightness, 0, 0);
        Assert.True(direct.HasColor);
        Assert.False(direct.HasSpeed);
        Assert.True(direct.HasBrightness);
        Assert.Null(direct.MinSpeed);

        var breathing = OpenRgbDeviceMapper.MapMode(2, "Breathing", ModeFlags.HasModeSpecificColor | ModeFlags.HasSpeed, 1, 5);
        Assert.True(breathing.HasColor);
        Assert.True(breathing.HasSpeed);
        Assert.Equal(1, breathing.MinSpeed);
        Assert.Equal(5, breathing.MaxSpeed);

        var rainbow = OpenRgbDeviceMapper.MapMode(3, "", ModeFlags.HasSpeed | ModeFlags.HasRandomColor, 0, 100);
        Assert.False(rainbow.HasColor);
        Assert.Equal("Mode 4", rainbow.Name);

        Assert.Equal("Corsair", OpenRgbDeviceMapper.GuessVendor("Corsair K70 RGB"));
        Assert.Equal("Unknown", OpenRgbDeviceMapper.GuessVendor("  "));
        var c = OpenRgbDeviceMapper.ToOpenRgb(new RgbColor(1, 2, 3));
        Assert.Equal(new RgbColor(1, 2, 3), OpenRgbDeviceMapper.ToRgb(c));
    }

    // ---- Status wording --------------------------------------------------------------------------------------

    [Fact]
    public void Status_text_picks_the_right_line()
    {
        Assert.Equal("Connected to OpenRGB 0.9 (6 devices)", LightingStatusText.For(true, true, "0.9", 6));
        Assert.Equal("Connected to OpenRGB (1 device)", LightingStatusText.For(true, false, null, 1));
        Assert.Equal(LightingStatusText.NotInstalled, LightingStatusText.For(false, false, null, 0));
        Assert.Equal(LightingStatusText.NotRunning, LightingStatusText.For(false, true, null, 0));
        Assert.StartsWith(LightingStatusText.NotRunning, LightingStatusText.For(false, true, null, 0, "refused"));
        Assert.Contains("refused", LightingStatusText.For(false, true, null, 0, "refused"));
        Assert.True(LightingStatusText.VendorSoftware.Count >= 9);
        Assert.All(LightingStatusText.VendorSoftware, v => Assert.StartsWith("https://", v.Url));
    }

    [Fact]
    public void Install_locator_resolves_executable_from_display_icon_text()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PcCheckerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "OpenRGB.exe");
            File.WriteAllText(exe, "x");
            Assert.Equal(exe, OpenRgbInstallLocator.ResolveExecutable(dir, null));
            Assert.Equal(exe, OpenRgbInstallLocator.ResolveExecutable(null, $"\"{exe}\",0"));
            Assert.Null(OpenRgbInstallLocator.ResolveExecutable(Path.Combine(dir, "missing"), null));
            Assert.Null(OpenRgbInstallLocator.ResolveExecutable(null, Path.Combine(dir, "other.ico")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- Profiles --------------------------------------------------------------------------------------------

    [Fact]
    public void Built_in_profiles_cover_gaming_work_night_and_rainbow()
    {
        var names = LightingProfileCatalog.BuiltIn.Select(p => p.Name).ToList();
        Assert.Equal(4, names.Count);
        Assert.Contains(names, n => n.StartsWith("Gaming"));
        Assert.Contains(names, n => n.StartsWith("Work"));
        Assert.Contains(names, n => n.StartsWith("Night"));
        Assert.Contains("Rainbow", names);
        Assert.All(LightingProfileCatalog.BuiltIn, p => Assert.True(p.IsBuiltIn));
        var night = LightingProfileCatalog.BuiltIn.Single(p => p.Name.StartsWith("Night"));
        Assert.Equal(RgbColor.Black, night.AllColor);
        var rainbow = LightingProfileCatalog.BuiltIn.Single(p => p.Name == "Rainbow");
        Assert.Equal("Rainbow", rainbow.Devices["*"].ModeName);
    }

    [Fact]
    public void Profile_serializes_and_deserializes_with_colours_and_assignments()
    {
        var profile = new LightingProfile
        {
            Name = "Streaming",
            AllColor = new RgbColor(0xA7, 0x8B, 0xFA),
            Devices =
            {
                ["Keyboard"] = new LightingAssignment { ModeName = "Breathing", Color = new RgbColor(1, 2, 3), Speed = 40, Brightness = 70 },
                ["*"] = new LightingAssignment { ModeName = "Static" },
            },
        };

        var json = JsonSerializer.Serialize(profile);
        var back = JsonSerializer.Deserialize<LightingProfile>(json)!;
        Assert.Equal("Streaming", back.Name);
        Assert.Equal(profile.AllColor, back.AllColor);
        Assert.Equal(2, back.Devices.Count);
        Assert.Equal("Breathing", back.Devices["Keyboard"].ModeName);
        Assert.True(LightingApplyPlanner.TryGetAssignment(back, "keyboard", out var insensitive));
        Assert.Equal("Breathing", insensitive.ModeName);
        Assert.Equal(new RgbColor(1, 2, 3), back.Devices["Keyboard"].Color);
        Assert.Equal(40, back.Devices["Keyboard"].Speed);
        Assert.Equal(70, back.Devices["Keyboard"].Brightness);
        Assert.Null(back.Devices["*"].Color);
        Assert.False(back.IsBuiltIn);
    }

    [Fact]
    public void Assignments_resolve_by_name_then_type_then_wildcard()
    {
        var profile = new LightingProfile
        {
            Name = "p",
            Devices =
            {
                ["Keyboard X"] = new LightingAssignment { ModeName = "ByName" },
                ["Keyboard"] = new LightingAssignment { ModeName = "ByType" },
                ["*"] = new LightingAssignment { ModeName = "Wildcard" },
            },
        };

        Assert.Equal("ByName", LightingApplyPlanner.ResolveAssignment(profile, Device())!.ModeName);
        Assert.Equal("ByType", LightingApplyPlanner.ResolveAssignment(profile, Device("Other keyboard"))!.ModeName);
        Assert.Equal("Wildcard", LightingApplyPlanner.ResolveAssignment(profile, Device("GPU", OpenRgbDeviceTypes.Gpu))!.ModeName);
        Assert.Null(LightingApplyPlanner.ResolveAssignment(new LightingProfile { Name = "empty" }, Device()));
    }

    // ---- Apply planning --------------------------------------------------------------------------------------

    [Fact]
    public void Colour_mode_is_kept_when_active_mode_takes_a_colour_otherwise_direct_is_preferred()
    {
        Assert.Equal(2, LightingApplyPlanner.PickColorModeIndex(Modes, 2));
        Assert.Equal(0, LightingApplyPlanner.PickColorModeIndex(Modes, 3));
        Assert.Equal(0, LightingApplyPlanner.PickColorModeIndex(Modes, 4));
        Assert.Equal(0, LightingApplyPlanner.PickColorModeIndex(Modes, -1));
        Assert.False(LightingApplyPlanner.NeedsModeSwitch(Modes, 2, 2));
        Assert.True(LightingApplyPlanner.NeedsModeSwitch(Modes, 3, 0));

        RgbMode[] staticOnly = [new(0, "Rainbow", false, true, false, 0, 100), new(1, "Static", true, false, false, null, null)];
        Assert.Equal(1, LightingApplyPlanner.PickColorModeIndex(staticOnly, 0));
        RgbMode[] noColor = [new(0, "Rainbow", false, true, false, 0, 100)];
        Assert.Null(LightingApplyPlanner.PickColorModeIndex(noColor, 0));
        Assert.Null(LightingApplyPlanner.PickColorModeIndex([], 0));
    }

    [Fact]
    public void Modes_are_found_by_name_case_insensitively()
    {
        Assert.Equal(3, LightingApplyPlanner.FindModeIndex(Modes, "rainbow"));
        Assert.Equal(4, LightingApplyPlanner.FindOffModeIndex(Modes));
        Assert.Null(LightingApplyPlanner.FindModeIndex(Modes, "Wave"));
        Assert.Null(LightingApplyPlanner.FindModeIndex(Modes, null));
        Assert.Null(LightingApplyPlanner.FindOffModeIndex(Modes.Take(4).ToList()));
    }

    [Fact]
    public void Rainbow_requests_fall_back_to_the_devices_own_rainbow_style_effect()
    {
        RgbMode[] keyboard = [new(0, "Direct", true, false, true, null, null), new(1, "Rainbow Wave", false, true, true, 0, 100)];
        RgbMode[] strip = [new(0, "Static", true, false, false, null, null), new(1, "Spectrum Cycle", false, true, false, 0, 3)];
        RgbMode[] plain = [new(0, "Static", true, false, false, null, null), new(1, "Breathing", true, true, false, 0, 3)];

        Assert.Equal(3, LightingApplyPlanner.ResolveModeIndex(Modes, "Rainbow"));
        Assert.Equal(1, LightingApplyPlanner.ResolveModeIndex(keyboard, "Rainbow"));
        Assert.Equal(1, LightingApplyPlanner.ResolveModeIndex(strip, "rainbow"));
        Assert.Null(LightingApplyPlanner.ResolveModeIndex(plain, "Rainbow"));
        Assert.Null(LightingApplyPlanner.ResolveModeIndex(keyboard, "Breathing"));
        Assert.Null(LightingApplyPlanner.ResolveModeIndex(keyboard, null));
        Assert.True(LightingApplyPlanner.IsRainbowRequest("Rainbow Wave"));
        Assert.False(LightingApplyPlanner.IsRainbowRequest("Static"));
    }

    [Fact]
    public void Steady_modes_are_recognised_so_only_their_reported_colour_is_trusted()
    {
        Assert.True(LightingApplyPlanner.IsSteadyMode(Modes[0]));
        Assert.True(LightingApplyPlanner.IsSteadyMode(Modes[1]));
        Assert.True(LightingApplyPlanner.IsSteadyMode(new RgbMode(7, "Solid", true, false, false, null, null)));
        Assert.False(LightingApplyPlanner.IsSteadyMode(Modes[2]));
        Assert.False(LightingApplyPlanner.IsSteadyMode(Modes[3]));
        Assert.False(LightingApplyPlanner.IsSteadyMode(null));
    }

    [Fact]
    public void Brightness_can_only_be_dimmed_in_modes_that_take_a_colour()
    {
        Assert.True(LightingApplyPlanner.CanDim(Modes[0]));
        Assert.True(LightingApplyPlanner.CanDim(Modes[2]));
        Assert.False(LightingApplyPlanner.CanDim(Modes[3]));
        Assert.False(LightingApplyPlanner.CanDim(null));
        Assert.Contains("not supported", LightingApplyPlanner.BrightnessUnsupportedNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reconnect_backoff_doubles_then_caps_at_thirty_seconds()
    {
        Assert.Equal(TimeSpan.Zero, LightingApplyPlanner.ReconnectBackoff(0));
        Assert.Equal(TimeSpan.FromMilliseconds(500), LightingApplyPlanner.ReconnectBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(1), LightingApplyPlanner.ReconnectBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(8), LightingApplyPlanner.ReconnectBackoff(5));
        Assert.Equal(TimeSpan.FromSeconds(16), LightingApplyPlanner.ReconnectBackoff(6));
        Assert.Equal(TimeSpan.FromSeconds(30), LightingApplyPlanner.ReconnectBackoff(7));
        Assert.Equal(TimeSpan.FromSeconds(30), LightingApplyPlanner.ReconnectBackoff(40));
    }

    [Fact]
    public void Speed_is_clamped_and_mapped_to_and_from_percent()
    {
        var rainbow = Modes[3];
        Assert.Equal(5, LightingApplyPlanner.ClampSpeed(rainbow, 0));
        Assert.Equal(50, LightingApplyPlanner.ClampSpeed(rainbow, 999));
        Assert.Equal(28, LightingApplyPlanner.SpeedFromPercent(rainbow, 50));
        Assert.Equal(100, LightingApplyPlanner.PercentFromSpeed(rainbow, 50));
        Assert.Equal(0, LightingApplyPlanner.PercentFromSpeed(rainbow, 5));
        var noRange = Modes[0];
        Assert.Equal(60, LightingApplyPlanner.ClampSpeed(noRange, 60));
        Assert.Equal(60, LightingApplyPlanner.SpeedFromPercent(noRange, 60));
    }

    [Fact]
    public void Effective_colour_applies_brightness_and_fill_builds_led_arrays()
    {
        var c = new RgbColor(200, 100, 50);
        Assert.Equal(c, LightingApplyPlanner.EffectiveColor(c, null));
        Assert.Equal(c, LightingApplyPlanner.EffectiveColor(c, 100));
        Assert.Equal(new RgbColor(100, 50, 25), LightingApplyPlanner.EffectiveColor(c, 50));
        var fill = LightingApplyPlanner.Fill(4, c);
        Assert.Equal(4, fill.Length);
        Assert.All(fill, x => Assert.Equal(c, x));
        Assert.Empty(LightingApplyPlanner.Fill(-1, c));
    }

    [Fact]
    public void Debounce_sends_first_change_immediately_then_waits()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(LightingApplyPlanner.ShouldSend(null, t0));
        Assert.False(LightingApplyPlanner.ShouldSend(t0, t0.AddMilliseconds(100)));
        Assert.True(LightingApplyPlanner.ShouldSend(t0, t0.AddMilliseconds(150)));
        Assert.True(LightingApplyPlanner.ShouldSend(t0, t0.AddMilliseconds(40), debounceMs: 30));
    }

    // ---- Demo service ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Demo_service_tracks_modes_colours_profiles_and_connection()
    {
        var svc = new DemoLightingService();
        var devices = await svc.GetDevicesAsync(CancellationToken.None);
        Assert.Equal(3, devices.Count);
        Assert.Contains(devices, d => OpenRgbDeviceTypes.IsPeripheral(d.Type));

        var keyboard = devices.Single(d => d.Type == OpenRgbDeviceTypes.Keyboard);
        Assert.False(keyboard.Modes[keyboard.ActiveModeIndex].HasColor);
        var set = await svc.SetDeviceColorAsync(keyboard.Index, new RgbColor(1, 2, 3), CancellationToken.None);
        Assert.True(set.Success);
        var after = (await svc.GetDevicesAsync(CancellationToken.None)).Single(d => d.Index == keyboard.Index);
        Assert.Equal(new RgbColor(1, 2, 3), after.Color);
        Assert.True(after.Modes[after.ActiveModeIndex].HasColor);

        var mode = await svc.SetModeAsync(keyboard.Index, 2, new RgbColor(9, 9, 9), 80, 30, CancellationToken.None);
        Assert.True(mode.Success);
        Assert.Equal((30, 80), svc.GetLevels(keyboard.Index));

        Assert.False((await svc.SetZoneColorAsync(keyboard.Index, 7, RgbColor.White, CancellationToken.None)).Success);
        Assert.False((await svc.SetDeviceColorAsync(99, RgbColor.White, CancellationToken.None)).Success);

        Assert.True((await svc.TurnOffAllAsync(CancellationToken.None)).Success);
        Assert.All(await svc.GetDevicesAsync(CancellationToken.None), d => Assert.Equal("Off", d.Modes[d.ActiveModeIndex].Name));

        var rainbow = svc.BuiltInProfiles.Single(p => p.Name == "Rainbow");
        Assert.True((await svc.ApplyProfileAsync(rainbow, CancellationToken.None)).Success);
        var lit = await svc.GetDevicesAsync(CancellationToken.None);
        Assert.All(lit, d => Assert.Contains("Rainbow", d.Modes[d.ActiveModeIndex].Name));
        // The keyboard has no mode called exactly "Rainbow"; its own "Rainbow wave" effect is used instead.
        Assert.Equal("Rainbow wave", lit.Single(d => d.Type == OpenRgbDeviceTypes.Keyboard).Modes[lit.Single(d => d.Type == OpenRgbDeviceTypes.Keyboard).ActiveModeIndex].Name);

        await svc.DisconnectAsync();
        Assert.False(svc.IsConnected);
        Assert.Empty(await svc.GetDevicesAsync(CancellationToken.None));
        Assert.False((await svc.SetDeviceColorAsync(0, RgbColor.White, CancellationToken.None)).Success);
        Assert.True((await svc.LaunchOpenRgbServerAsync(CancellationToken.None)).Success);
        Assert.True(svc.IsConnected);
        Assert.NotEmpty(svc.History);
    }
}
