using PcChecker.Core.Hardware;
using PcChecker.Core.Input;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Pure logic of the Keyboard &amp; Mouse module: value mapping, profiles, vendor links, polling rate math, action provider.</summary>
public class InputTests
{
    [Theory]
    [InlineData(true, new[] { 6, 10, 1 })]
    [InlineData(false, new[] { 0, 0, 0 })]
    public void Mouse_threshold_triplet_matches_enhance_pointer_precision(bool precision, int[] expected)
    {
        Assert.Equal(expected, InputMath.MouseThresholds(precision));
        Assert.Equal(precision, InputMath.PrecisionFromThresholds(expected));
    }

    [Fact]
    public void Precision_is_read_from_the_acceleration_flag_only()
    {
        Assert.True(InputMath.PrecisionFromThresholds([0, 0, 1]));
        Assert.False(InputMath.PrecisionFromThresholds([6, 10, 0]));
        Assert.False(InputMath.PrecisionFromThresholds(null));
        Assert.False(InputMath.PrecisionFromThresholds([1]));
    }

    [Theory]
    [InlineData(10, "6/11")]
    [InlineData(1, "1/11")]
    [InlineData(2, "2/11")]
    [InlineData(20, "11/11")]
    [InlineData(8, "5/11")]
    [InlineData(11, "≈ 6.5/11")]
    [InlineData(3, "≈ 2.5/11")]
    [InlineData(99, "11/11")]
    public void Pointer_speed_maps_to_control_panel_slider_text(int speed, string expected) =>
        Assert.Equal(expected, InputMath.SpeedToSliderText(speed));

    [Fact]
    public void Slider_notches_round_trip_to_speeds()
    {
        for (int notch = 1; notch <= 11; notch++)
        {
            int speed = InputMath.SliderNotchToSpeed(notch);
            Assert.Equal($"{notch}/11", InputMath.SpeedToSliderText(speed));
        }

        Assert.True(InputMath.IsDefaultSpeed(InputMath.SliderNotchToSpeed(6)));
        Assert.Contains("1:1", InputMath.SpeedNote(10));
        Assert.Contains("skips", InputMath.SpeedNote(4));
        Assert.Contains("multiplies", InputMath.SpeedNote(16));
    }

    [Theory]
    [InlineData(0, 2.5)]
    [InlineData(31, 30)]
    [InlineData(15, 15.8)]
    [InlineData(-5, 2.5)]
    [InlineData(40, 30)]
    public void Repeat_rate_index_converts_to_characters_per_second(int index, double expected)
    {
        Assert.Equal(expected, InputMath.RepeatRateToCharsPerSecond(index));
        Assert.Contains("characters per second", InputMath.RepeatRateText(index));
    }

    [Theory]
    [InlineData(0, 250, "Short")]
    [InlineData(1, 500, "Normal")]
    [InlineData(2, 750, "Long")]
    [InlineData(3, 1000, "Longest")]
    public void Repeat_delay_index_converts_to_milliseconds(int index, int ms, string word)
    {
        Assert.Equal(ms, InputMath.RepeatDelayToMs(index));
        Assert.StartsWith(word, InputMath.RepeatDelayText(index));
    }

    [Fact]
    public void Normalize_clamps_values_to_what_windows_accepts()
    {
        var mouse = InputMath.Normalize(new MouseSettings { Speed = 50, DoubleClickMs = 10, ScrollLines = 0, PointerTrails = 1 });
        Assert.Equal(20, mouse.Speed);
        Assert.Equal(100, mouse.DoubleClickMs);
        Assert.Equal(1, mouse.ScrollLines);
        Assert.Equal(0, mouse.PointerTrails);

        var keyboard = InputMath.Normalize(new KeyboardSettings { RepeatDelay = 9, RepeatRate = -1, CursorBlinkMs = 0 });
        Assert.Equal(3, keyboard.RepeatDelay);
        Assert.Equal(0, keyboard.RepeatRate);
        Assert.Equal(-1, keyboard.CursorBlinkMs);
    }

    [Fact]
    public void SameAs_and_Describe_report_only_real_differences()
    {
        var a = new MouseSettings();
        var b = a.Clone();
        Assert.True(InputMath.SameAs(a, b));
        Assert.Empty(InputMath.Describe(a, b));

        b.EnhancePointerPrecision = false;
        b.Speed = 12;
        Assert.False(InputMath.SameAs(a, b));
        var lines = InputMath.Describe(a, b);
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Contains("6/11 → 7/11"));
        Assert.Contains(lines, l => l.Contains("precision on → off"));

        var k = new KeyboardSettings();
        var k2 = new KeyboardSettings { StickyKeys = true };
        Assert.Single(InputMath.Describe(k, k2));
        Assert.True(InputMath.SameAs(k, k.Clone()));
    }

    [Fact]
    public void Built_in_profiles_are_sane()
    {
        var profiles = InputProfileCatalog.BuiltIn;
        Assert.Equal(4, profiles.Count);
        Assert.Equal(profiles.Count, profiles.Select(p => p.Id).Distinct().Count());
        Assert.All(profiles, p => Assert.True(p.IsBuiltIn));
        Assert.All(profiles, p => Assert.True(InputMath.SameAs(p.Mouse, InputMath.Normalize(p.Mouse)), p.Name + " mouse values out of range"));
        Assert.All(profiles, p => Assert.True(InputMath.SameAs(p.Keyboard, InputMath.Normalize(p.Keyboard)), p.Name + " keyboard values out of range"));

        var defaults = InputProfileCatalog.FindBuiltIn(InputProfileCatalog.DefaultProfileId)!;
        Assert.True(InputMath.SameAs(defaults.Mouse, new MouseSettings()));
        Assert.True(InputMath.SameAs(defaults.Keyboard, new KeyboardSettings()));

        var gaming = InputProfileCatalog.FindBuiltIn(InputProfileCatalog.GamingProfileId)!;
        Assert.False(gaming.Mouse.EnhancePointerPrecision);
        Assert.Equal(InputMath.DefaultSpeed, gaming.Mouse.Speed);
        Assert.Equal(0, gaming.Mouse.PointerTrails);

        Assert.True(InputProfileCatalog.FindBuiltIn(InputProfileCatalog.AccessibilityProfileId)!.Keyboard.StickyKeys);
        Assert.Null(InputProfileCatalog.FindBuiltIn("nope"));
    }

    [Fact]
    public void Vendor_links_match_device_names_and_always_include_powertoys()
    {
        var none = new Dictionary<string, string>();
        var devices = new List<InputDeviceInfo>
        {
            new("Logitech G PRO X SUPERLIGHT", "Logitech", "HID\\1", "OK", "Mouse", "HID-compliant mouse", null, 5, null, null, none),
            new("Corsair K70 RGB PRO", "Corsair", "HID\\2", "OK", "Keyboard", "USB keyboard", "00000409", null, 12, null, none),
            new("Corsair SCIMITAR", "Corsair", "HID\\3", "OK", "Mouse", "HID-compliant mouse", null, 17, null, null, none),
            new("HID Keyboard Device", "(Standard keyboards)", "HID\\4", "OK", "Keyboard", "HID Keyboard Device", "00000409", null, 12, null, none),
        };

        var links = InputProfileCatalog.VendorLinksFor(devices);
        Assert.Contains(links, l => l.Vendor == "Logitech" && l.Url.Contains("logitech", StringComparison.OrdinalIgnoreCase));
        Assert.Single(links, l => l.Vendor == "Corsair");
        Assert.Contains(links, l => l.Vendor == "Microsoft PowerToys");
        Assert.Equal(3, links.Count);
        Assert.All(links, l => Assert.StartsWith("https://", l.Url));

        var empty = InputProfileCatalog.VendorLinksFor([]);
        Assert.Single(empty);
        Assert.Single(InputProfileCatalog.VendorLinksFor(null));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(118, 125)]
    [InlineData(480, 500)]
    [InlineData(700, 500)]
    [InlineData(996, 1000)]
    [InlineData(3900, 4000)]
    [InlineData(12000, 8000)]
    public void Polling_rate_is_classified_to_the_closest_standard_rate(int measured, int nominal)
    {
        var reading = PollingRateMath.Interpret(measured);
        Assert.Equal(nominal, reading.NominalHz);
        Assert.False(string.IsNullOrWhiteSpace(reading.Verdict));
    }

    [Fact]
    public void Polling_rate_text_is_actionable()
    {
        Assert.Contains("Move the mouse", PollingRateMath.PollingRateText(0));
        Assert.Contains("1000 Hz", PollingRateMath.PollingRateText(998));
        Assert.Contains("Excellent", PollingRateMath.PollingRateText(998));
        Assert.Contains("set 1000 Hz", PollingRateMath.PollingRateText(125));
        Assert.Contains("below", PollingRateMath.PollingRateText(380));
        Assert.Equal(7, PollingRateMath.Table.Count);
        Assert.Equal("1 ms", PollingRateMath.Table.Single(r => r.NominalHz == 1000).IntervalText);
        Assert.Equal("125 µs", PollingRateMath.Table.Single(r => r.NominalHz == 8000).IntervalText);
    }

    [Fact]
    public void Polling_rate_estimate_ignores_pauses_and_uses_the_median_gap()
    {
        var intervals = new List<double>();
        for (int i = 0; i < 200; i++)
        {
            intervals.Add(i % 2 == 0 ? 0.98 : 1.02);
        }

        intervals.Add(400); // the user stopped moving for a moment
        intervals.Add(0.3); // one jittery report
        Assert.Equal(1000, PollingRateMath.EstimateHz(intervals));

        Assert.Equal(125, PollingRateMath.EstimateHz(Enumerable.Repeat(8.0, 50).ToList()));
        Assert.Equal(0, PollingRateMath.EstimateHz([]));
        Assert.Equal(0, PollingRateMath.EstimateHz(Enumerable.Repeat(1.0, 3).ToList()));
    }

    [Fact]
    public void Live_rate_counts_reports_inside_the_last_window()
    {
        var timestamps = Enumerable.Range(0, 2500).Select(i => i * 1.0).ToList(); // 1 kHz for 2.5 s
        Assert.Equal(1000, PollingRateMath.CountInWindow(timestamps, 2499));
        Assert.Equal(1000, PollingRateMath.CountInWindow(timestamps, 2499, 500)); // half-second window, still scaled to Hz
        Assert.Equal(500, PollingRateMath.CountInWindow(timestamps, 499));
        Assert.Equal(0, PollingRateMath.CountInWindow([], 10));
    }

    [Fact]
    public async Task Action_provider_toggles_pointer_precision_and_applies_the_gaming_profile()
    {
        var input = new DemoInputService();
        var provider = new InputActionsProvider(input);
        Assert.Equal(3, provider.Actions.Count);
        Assert.All(provider.Actions, a => Assert.True(provider.Handles(a.Id)));
        Assert.False(provider.Handles("latency.hags.on"));

        var before = await provider.GetStateAsync(InputActionIds.PointerPrecisionOff, CancellationToken.None);
        Assert.False(before.IsApplied);

        Assert.True((await provider.ApplyAsync(InputActionIds.PointerPrecisionOff, CancellationToken.None)).Success);
        Assert.False((await input.GetMouseAsync(CancellationToken.None)).EnhancePointerPrecision);
        Assert.True((await provider.GetStateAsync(InputActionIds.PointerPrecisionOff, CancellationToken.None)).IsApplied);

        Assert.True((await provider.RevertAsync(InputActionIds.PointerPrecisionOff, CancellationToken.None)).Success);
        Assert.True((await input.GetMouseAsync(CancellationToken.None)).EnhancePointerPrecision);

        Assert.True((await provider.ApplyAsync(InputActionIds.GamingProfileApply, CancellationToken.None)).Success);
        Assert.True((await provider.GetStateAsync(InputActionIds.GamingProfileApply, CancellationToken.None)).IsApplied);
        Assert.Equal(0, (await input.GetKeyboardAsync(CancellationToken.None)).RepeatDelay);

        Assert.True((await provider.RevertAsync(InputActionIds.GamingProfileApply, CancellationToken.None)).Success);
        Assert.False((await provider.GetStateAsync(InputActionIds.GamingProfileApply, CancellationToken.None)).IsApplied);
        Assert.False((await provider.ApplyAsync("input.unknown", CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Demo_polling_meter_reports_live_values_and_settles_near_1000_hz()
    {
        var live = new List<int>();
        var meter = new DemoMousePollingMeter();
        int hz = await meter.MeasureAsync(1, new SyncProgress<int>(live.Add), CancellationToken.None);
        Assert.InRange(hz, 950, 1050);
        Assert.True(live.Count >= 4);
        Assert.True(live[0] < live[^1]);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
