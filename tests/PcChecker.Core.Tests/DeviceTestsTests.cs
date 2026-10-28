using System.Buffers.Binary;
using System.Text;
using PcChecker.Core.DeviceTests;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// Pure logic of the Device tests page: stick drift and roundness, keyboard codes and rollover, click timing, frame
/// timing, the test-tone and WAV generator, and the microphone level maths.
/// </summary>
public class DeviceTestsTests
{
    // ----- Controller: drift -----

    [Fact]
    public void A_centred_stick_has_no_drift()
    {
        var samples = Enumerable.Range(0, 120).Select(i => new StickPoint(0.005 * Math.Sin(i), 0.005 * Math.Cos(i))).ToList();
        var result = StickMath.AnalyzeDrift(samples);
        Assert.Equal(DriftVerdict.Centred, result.Verdict);
        Assert.True(result.Offset < 0.01);
        Assert.Equal(120, result.Samples);
    }

    [Theory]
    [InlineData(0.06, -0.04, DriftVerdict.SlightDrift)]
    [InlineData(0.12, 0.05, DriftVerdict.Drift)]
    [InlineData(0.01, 0.0, DriftVerdict.Centred)]
    public void Resting_offset_is_judged_against_the_deadzone(double x, double y, DriftVerdict expected)
    {
        var samples = Enumerable.Repeat(new StickPoint(x, y), 60).ToList();
        var result = StickMath.AnalyzeDrift(samples);
        Assert.Equal(expected, result.Verdict);
        Assert.Equal(Math.Sqrt(x * x + y * y), result.Offset, 6);
        Assert.Equal(x, result.OffsetX, 6);
        Assert.Equal(y, result.OffsetY, 6);
    }

    [Fact]
    public void A_stick_that_moves_during_the_drift_check_is_reported_as_moved()
    {
        var samples = Enumerable.Range(0, 60).Select(i => new StickPoint(i < 30 ? 0 : 0.8, 0)).ToList();
        Assert.Equal(DriftVerdict.Moved, StickMath.AnalyzeDrift(samples).Verdict);
    }

    [Fact]
    public void Too_few_readings_cannot_be_judged()
    {
        Assert.Equal(DriftVerdict.NotEnoughData, StickMath.AnalyzeDrift([new StickPoint(0.5, 0.5)]).Verdict);
        Assert.Equal(DriftVerdict.NotEnoughData, StickMath.AnalyzeDrift([]).Verdict);
    }

    [Fact]
    public void A_wider_deadzone_turns_drift_into_slight_drift()
    {
        var samples = Enumerable.Repeat(new StickPoint(0.15, 0), 40).ToList();
        Assert.Equal(DriftVerdict.Drift, StickMath.AnalyzeDrift(samples).Verdict);
        Assert.Equal(DriftVerdict.SlightDrift, StickMath.AnalyzeDrift(samples, deadzone: 0.24).Verdict);
    }

    // ----- Controller: roundness -----

    private static IEnumerable<StickPoint> Circle(double radius, int points = 720, Func<double, double>? shape = null) =>
        Enumerable.Range(0, points).Select(i =>
        {
            double a = i * 2 * Math.PI / points;
            double r = shape?.Invoke(a) ?? radius;
            return new StickPoint(r * Math.Cos(a), r * Math.Sin(a));
        });

    [Fact]
    public void A_perfect_circle_has_no_roundness_error()
    {
        var result = StickMath.AnalyzeCircularity(Circle(1.0));
        Assert.Equal(CircularityVerdict.Good, result.Verdict);
        Assert.Equal(100, result.CoveragePercent, 6);
        Assert.Equal(0, result.AverageErrorPercent, 6);
        Assert.Equal(StickMath.DefaultSectors, result.SectorsFilled);
    }

    [Fact]
    public void A_small_outer_edge_shows_as_roundness_error()
    {
        var result = StickMath.AnalyzeCircularity(Circle(0.85));
        Assert.Equal(15, result.AverageErrorPercent, 6);
        Assert.Equal(CircularityVerdict.Fair, result.Verdict);
        Assert.Equal(CircularityVerdict.Poor, StickMath.AnalyzeCircularity(Circle(0.7)).Verdict);
    }

    [Fact]
    public void A_square_gate_reads_as_error_at_the_diagonals()
    {
        // A square gate reaches 1.414 in the corners: the average distance from the circle is well above zero.
        var square = Circle(0, shape: a => Math.Min(1 / Math.Max(Math.Abs(Math.Cos(a)), Math.Abs(Math.Sin(a))), 1.42));
        var result = StickMath.AnalyzeCircularity(square);
        Assert.True(result.AverageErrorPercent > 15, $"error {result.AverageErrorPercent}");
    }

    [Fact]
    public void Roundness_waits_until_most_of_the_edge_is_covered()
    {
        var half = Circle(1.0).Where(p => p.Y >= 0).ToList();
        var result = StickMath.AnalyzeCircularity(half);
        Assert.Equal(CircularityVerdict.KeepSpinning, result.Verdict);
        Assert.InRange(result.CoveragePercent, 45, 60);

        // Readings near the middle never count towards the edge.
        Assert.Equal(0, StickMath.AnalyzeCircularity(Circle(0.3)).SectorsFilled);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 9)]
    [InlineData(-1, 0, 18)]
    [InlineData(0, -1, 27)]
    [InlineData(0.7, -0.7, 31)]
    public void Sectors_count_anticlockwise_from_the_right(double x, double y, int sector)
    {
        Assert.Equal(sector, StickMath.SectorOf(new StickPoint(x, y), 36));
    }

    [Fact]
    public void Demo_controller_rests_with_slight_drift_then_spins_round()
    {
        var resting = Enumerable.Range(0, 120).Select(i => DemoGamepadTestService.StateAt(i / 60.0).Left).ToList();
        Assert.Equal(DriftVerdict.SlightDrift, StickMath.AnalyzeDrift(resting).Verdict);
        var rightResting = Enumerable.Range(0, 120).Select(i => DemoGamepadTestService.StateAt(i / 60.0).Right).ToList();
        Assert.Equal(DriftVerdict.Centred, StickMath.AnalyzeDrift(rightResting).Verdict);

        var spinning = Enumerable.Range(0, 480).Select(i => DemoGamepadTestService.StateAt(DemoGamepadTestService.RestSeconds + i / 60.0).Left);
        var round = StickMath.AnalyzeCircularity(spinning);
        Assert.NotEqual(CircularityVerdict.KeepSpinning, round.Verdict);
        Assert.InRange(round.AverageErrorPercent, 0.5, 10);
    }

    // ----- Controller: value mapping -----

    [Fact]
    public void XInput_values_map_to_unit_ranges()
    {
        Assert.Equal(-1, StickMath.FromXInputAxis(short.MinValue), 6);
        Assert.Equal(1, StickMath.FromXInputAxis(short.MaxValue), 6);
        Assert.Equal(0, StickMath.FromXInputAxis(0), 6);
        Assert.Equal(1, StickMath.FromXInputTrigger(255), 6);
        Assert.Equal(0, StickMath.FromXInputTrigger(0), 6);
        Assert.Equal(-1, StickMath.FromRawAxis(0), 6);
        Assert.Equal(0, StickMath.FromRawAxis(0.5), 6);
        Assert.Equal(-1, StickMath.FromRawAxis(1, invert: true), 6);
        Assert.Equal(0, StickMath.FromRawAxis(double.NaN), 6);
    }

    [Fact]
    public void XInput_and_wgi_buttons_map_to_the_same_set()
    {
        Assert.Equal(GamepadButtons.A | GamepadButtons.DPadUp | GamepadButtons.Menu, GamepadMapping.FromXInputButtons(0x1000 | 0x0001 | 0x0010));
        Assert.Equal(GamepadButtons.A | GamepadButtons.DPadUp | GamepadButtons.Menu, GamepadMapping.FromWgiButtons(4 | 64 | 1));
        Assert.Equal(GamepadButtons.LeftShoulder | GamepadButtons.Y, GamepadMapping.FromXInputButtons(0x0100 | 0x8000));
        Assert.Equal(GamepadButtons.Paddle4, GamepadMapping.FromWgiButtons(131072));
        Assert.Equal(GamepadButtons.None, GamepadMapping.FromXInputButtons(0));
        Assert.Equal(GamepadButtons.DPadUp | GamepadButtons.DPadLeft, GamepadMapping.FromSwitchPosition(8));
        Assert.Equal(GamepadButtons.None, GamepadMapping.FromSwitchPosition(0));
    }

    [Fact]
    public void Generic_controllers_show_only_the_buttons_they_can_report()
    {
        var raw = GamepadMapping.NamedButtonsFor(GamepadSource.RawController);
        Assert.Equal([GamepadButtons.DPadUp, GamepadButtons.DPadDown, GamepadButtons.DPadLeft, GamepadButtons.DPadRight], raw);
        Assert.Equal(GamepadMapping.StandardButtons, GamepadMapping.NamedButtonsFor(GamepadSource.Gamepad));
        Assert.Contains(GamepadButtons.A, GamepadMapping.NamedButtonsFor(GamepadSource.XInput));
    }

    [Fact]
    public void Battery_reports_become_percentages()
    {
        Assert.Equal(72, GamepadMapping.BatteryPercent(1800, 2500));
        Assert.Null(GamepadMapping.BatteryPercent(null, 2500));
        Assert.Null(GamepadMapping.BatteryPercent(100, 0));
        Assert.Equal(100, GamepadMapping.BatteryPercent(3000, 2500));
        Assert.Equal((null, GamepadBatteryState.Wired), GamepadMapping.FromXInputBattery(1, 0));
        Assert.Equal((25, GamepadBatteryState.Discharging), GamepadMapping.FromXInputBattery(2, 1));
        Assert.Equal(GamepadBatteryState.Charging, GamepadMapping.FromBatteryStatus(3));
    }

    // ----- Keyboard -----

    [Fact]
    public void The_full_size_layout_has_104_unique_keys_inside_its_bounds()
    {
        var keys = KeyboardLayout.FullSize;
        Assert.Equal(104, keys.Count);
        Assert.Equal(keys.Count, keys.Select(k => k.Code).Distinct().Count());
        Assert.All(keys, k =>
        {
            Assert.InRange(k.X + k.Width, 0, KeyboardLayout.TotalWidth);
            Assert.InRange(k.Y + k.Height, 0, KeyboardLayout.TotalHeight);
        });

        // No two keys overlap.
        foreach (var a in keys)
        {
            foreach (var b in keys.Where(b => b != a))
            {
                bool overlap = a.X < b.X + b.Width - 0.01 && b.X < a.X + a.Width - 0.01 && a.Y < b.Y + b.Height - 0.01 && b.Y < a.Y + a.Height - 0.01;
                Assert.False(overlap, $"{a.Label} overlaps {b.Label}");
            }
        }
    }

    [Theory]
    [InlineData(0x0D, 0x1C, false, 0x1C)] // Enter
    [InlineData(0x0D, 0x1C, true, 0xE01C)] // number pad Enter
    [InlineData(0x11, 0x1D, true, 0xE01D)] // right Ctrl
    [InlineData(0x10, 0x36, false, 0x36)] // right Shift
    [InlineData(0x13, 0x45, false, 0x45)] // Pause
    [InlineData(0x90, 0x45, false, 0xE045)] // Num Lock
    [InlineData(0x2C, 0x54, false, 0xE037)] // Print Screen (Alt+PrtSc reports 0x54)
    [InlineData(0x24, 0x47, false, 0x47)] // number pad 7 with Num Lock off
    [InlineData(0xAF, 0x00, false, 0x100AF)] // volume up without a scan code
    public void Key_messages_map_to_physical_keys(int vk, int scan, bool extended, int expected)
    {
        Assert.Equal(expected, KeyCodes.FromMessage(vk, scan, extended));
    }

    [Fact]
    public void Fake_shifts_and_reserved_codes_are_ignored()
    {
        Assert.Null(KeyCodes.FromMessage(0x10, 0x2A, true));
        Assert.Null(KeyCodes.FromMessage(0x10, 0x36, true));
        Assert.Null(KeyCodes.FromMessage(0xFF, 0x10, false));
    }

    [Fact]
    public void LParam_is_split_into_scan_code_extended_and_repeat()
    {
        long lParam = (1L << 30) | (1L << 24) | (0x1DL << 16) | 1;
        Assert.Equal((0x1D, true, true), KeyCodes.SplitLParam(lParam));
        Assert.Equal((0x1E, false, false), KeyCodes.SplitLParam(0x1E0001));
        Assert.Equal("scan code E0 1D", KeyCodes.Describe(0xE01D));
        Assert.Equal("scan code 1E", KeyCodes.Describe(0x1E));
    }

    [Fact]
    public void Rollover_counts_keys_held_together_and_ignores_auto_repeat()
    {
        var tracker = new KeyRolloverTracker();
        Assert.True(tracker.Press(0x1E));
        Assert.True(tracker.Press(0x1F));
        Assert.False(tracker.Press(0x1F));
        Assert.True(tracker.Press(0x20));
        Assert.Equal(3, tracker.HeldCount);
        Assert.Equal(3, tracker.MaxHeld);

        tracker.Release(0x1E);
        tracker.Release(0x1F);
        Assert.Equal(1, tracker.HeldCount);
        Assert.Equal(3, tracker.MaxHeld);
        Assert.Equal(3, tracker.TestedCount);
        Assert.Equal(RolloverVerdict.Limited, tracker.Verdict);

        // A release without a press (Print Screen) still marks the key as tested.
        tracker.Release(0xE037);
        Assert.True(tracker.WasTested(0xE037));
        Assert.Equal(2, tracker.TestedOf([0x1E, 0xE037, 0x30]));

        tracker.ReleaseAll();
        Assert.Equal(0, tracker.HeldCount);
        Assert.Equal(4, tracker.TestedCount);
        tracker.Reset();
        Assert.Equal(0, tracker.TestedCount);
        Assert.Equal(0, tracker.MaxHeld);
    }

    [Theory]
    [InlineData(0, RolloverVerdict.NotTested)]
    [InlineData(1, RolloverVerdict.NotTested)]
    [InlineData(3, RolloverVerdict.Limited)]
    [InlineData(5, RolloverVerdict.Fair)]
    [InlineData(6, RolloverVerdict.Good)]
    [InlineData(10, RolloverVerdict.Excellent)]
    public void Rollover_is_rated_in_plain_classes(int held, RolloverVerdict verdict)
    {
        Assert.Equal(verdict, KeyRolloverTracker.Rate(held));
    }

    // ----- Mouse -----

    [Fact]
    public void Click_timing_finds_double_clicks_and_chatter()
    {
        var analyzer = new ClickTimingAnalyzer();
        var first = analyzer.Register(MouseTestInput.Left, 1000, 500);
        Assert.Null(first.IntervalMs);
        Assert.False(first.IsDoubleClick);

        var second = analyzer.Register(MouseTestInput.Left, 1180, 500);
        Assert.Equal(180, second.IntervalMs);
        Assert.True(second.IsDoubleClick);
        Assert.False(second.IsChatter);

        var bounce = analyzer.Register(MouseTestInput.Left, 1192, 500);
        Assert.True(bounce.IsChatter);
        Assert.Equal(1, analyzer.ChatterCount(MouseTestInput.Left));

        // Buttons are timed separately, and a slow second click is not a double-click.
        Assert.Null(analyzer.Register(MouseTestInput.Right, 1195, 500).IntervalMs);
        Assert.False(analyzer.Register(MouseTestInput.Left, 2000, 500).IsDoubleClick);
        analyzer.Reset();
        Assert.Equal(0, analyzer.ChatterCount(MouseTestInput.Left));
    }

    [Fact]
    public void Polling_smoothness_is_judged_from_live_readings()
    {
        Assert.Equal(SmoothnessVerdict.Unknown, MouseTestMath.Smoothness([333, 1000]));
        Assert.Equal(SmoothnessVerdict.Steady, MouseTestMath.Smoothness([300, 700, 998, 1003, 995, 1000, 1001, 997]));
        Assert.Equal(SmoothnessVerdict.Uneven, MouseTestMath.Smoothness([300, 700, 1000, 400, 950, 300, 1000, 500]));
        Assert.Equal(SmoothnessVerdict.Unknown, MouseTestMath.Smoothness([0, 0, 0, 0, 0, 0, 0]));
    }

    // ----- Monitor -----

    [Fact]
    public void Frame_timing_counts_frames_per_second_and_skipped_frames()
    {
        var analyzer = new FrameTimingAnalyzer(60);
        for (int i = 0; i <= 60; i++)
        {
            analyzer.AddFrame(i * 1000.0 / 60);
        }

        Assert.Equal(0, analyzer.SkippedFrames);
        Assert.InRange(analyzer.FramesPerSecond, 59, 61);

        // A repeated time stamp (two Rendering events in one frame) is ignored.
        Assert.False(analyzer.AddFrame(1000));

        // A gap of three refresh intervals skipped two frames.
        Assert.True(analyzer.AddFrame(1000 + 3 * 1000.0 / 60));
        Assert.Equal(2, analyzer.SkippedFrames);

        analyzer.Reset();
        Assert.Equal(0, analyzer.SkippedFrames);
        Assert.Equal(0, analyzer.FramesPerSecond);
    }

    [Fact]
    public void Frame_timing_does_not_blame_a_fast_screen_for_a_slower_drawing_clock()
    {
        // A 144 Hz screen while Windows composes at 60 Hz (the main screen's rate): every gap is about 16.7 ms.
        var analyzer = new FrameTimingAnalyzer(144);
        double t = 0;
        for (int i = 0; i <= 120; i++)
        {
            analyzer.AddFrame(t);
            t += 1000.0 / 60;
        }

        Assert.True(analyzer.DrawsSlowerThanScreen);
        Assert.Equal(0, analyzer.SkippedFrames);
        Assert.InRange(analyzer.DrawingHz!.Value, 59, 61);

        // A real hitch (one 60 Hz frame missed) is still counted, against the 60 Hz clock.
        t += 1000.0 / 60;
        analyzer.AddFrame(t);
        Assert.Equal(1, analyzer.SkippedFrames);
    }

    [Fact]
    public void Frame_timing_matches_the_screen_when_the_clock_keeps_up()
    {
        var analyzer = new FrameTimingAnalyzer(144);
        for (int i = 0; i <= 200; i++)
        {
            analyzer.AddFrame(i * 1000.0 / 144);
        }

        Assert.False(analyzer.DrawsSlowerThanScreen);
        Assert.Equal(0, analyzer.SkippedFrames);
        Assert.True(analyzer.AddFrame(200 * 1000.0 / 144 + 3 * 1000.0 / 144));
        Assert.Equal(2, analyzer.SkippedFrames);
    }

    [Theory]
    [InlineData(16.7, 16.67, 0)]
    [InlineData(24, 16.67, 0)]
    [InlineData(33.3, 16.67, 1)]
    [InlineData(4.2, 4.1667, 0)]
    [InlineData(12.5, 4.1667, 2)]
    [InlineData(5000, 16.67, 30)]
    public void Skipped_frames_from_one_gap(double interval, double expected, int skipped)
    {
        Assert.Equal(skipped, FrameTimingAnalyzer.SkippedIn(interval, expected));
    }

    // ----- Speakers: tone and WAV generator -----

    [Fact]
    public void Wav_header_describes_the_samples()
    {
        var samples = TestSignalGenerator.Tone(440, 0.5, 48_000, 2, ToneChannel.Both);
        var wav = WavFile.Create(samples, 48_000, 2);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(48_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(48_000 * 2 * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(4, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(24_000 * 2 * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(WavFile.HeaderSize + 24_000 * 2 * 2, wav.Length);
    }

    [Fact]
    public void Float_wav_uses_format_tag_3_and_keeps_the_values()
    {
        var wav = WavFile.Create([0.5f, -0.25f, 2f], 44_100, 1, WavSampleFormat.Float32);
        Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal(32, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(wav.AsSpan(44)));
        Assert.Equal(-0.25f, BinaryPrimitives.ReadSingleLittleEndian(wav.AsSpan(48)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(wav.AsSpan(52)));
        Assert.Equal(short.MaxValue, WavFile.ToPcm16(1f));
        Assert.Equal(-short.MaxValue, WavFile.ToPcm16(-1f));
    }

    [Theory]
    [InlineData(ToneChannel.Left, true, false)]
    [InlineData(ToneChannel.Right, false, true)]
    [InlineData(ToneChannel.Both, true, true)]
    public void Tones_play_only_on_the_chosen_channel(ToneChannel which, bool leftPlays, bool rightPlays)
    {
        var samples = TestSignalGenerator.Tone(440, 0.1, 8_000, 2, which);
        Assert.Equal(800 * 2, samples.Length);
        var left = samples.Where((_, i) => i % 2 == 0).ToArray();
        var right = samples.Where((_, i) => i % 2 == 1).ToArray();
        Assert.Equal(leftPlays, left.Any(s => s != 0));
        Assert.Equal(rightPlays, right.Any(s => s != 0));
        Assert.All(samples, s => Assert.InRange(s, -TestSignalGenerator.DefaultAmplitude - 1e-6, TestSignalGenerator.DefaultAmplitude + 1e-6));
    }

    [Fact]
    public void Surround_outputs_keep_the_extra_channels_silent_and_mono_plays_everything()
    {
        var surround = TestSignalGenerator.Tone(440, 0.05, 8_000, 6, ToneChannel.Both);
        Assert.Equal(400 * 6, surround.Length);
        for (int c = 2; c < 6; c++)
        {
            Assert.All(surround.Where((_, i) => i % 6 == c), s => Assert.Equal(0f, s));
        }

        var mono = TestSignalGenerator.Tone(440, 0.05, 8_000, 1, ToneChannel.Right);
        Assert.Contains(mono, s => s != 0);
    }

    [Fact]
    public void Tones_fade_in_and_out_and_the_sweep_lasts_its_full_length()
    {
        var tone = TestSignalGenerator.Tone(1000, 1, 48_000, 1, ToneChannel.Both);
        Assert.Equal(0f, tone[0]);
        Assert.True(Math.Abs(tone[^1]) < 0.001);
        Assert.Contains(tone.Skip(1000).Take(100), s => Math.Abs(s) > 0.15);

        var sweep = TestSignalGenerator.ForTest(SoundTest.Sweep, 8_000, 2);
        Assert.Equal(TestSignalGenerator.FrameCount(TestSignalGenerator.SweepSeconds, 8_000) * 2, sweep.Length);
        Assert.Equal(TimeSpan.FromSeconds(TestSignalGenerator.SweepSeconds), TestSignalGenerator.Duration(SoundTest.Sweep));
        Assert.Equal(TimeSpan.FromSeconds(TestSignalGenerator.ToneSeconds), TestSignalGenerator.Duration(SoundTest.Left));
    }

    // ----- Microphone -----

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(0.5, -6.0206)]
    [InlineData(0.1, -20)]
    [InlineData(0.001, -60)]
    [InlineData(0, AudioLevelMath.FloorDb)]
    public void Amplitude_converts_to_decibels(double amplitude, double db)
    {
        Assert.Equal(db, AudioLevelMath.ToDb(amplitude), 3);
    }

    [Fact]
    public void Level_measures_peak_rms_and_clipping()
    {
        var sine = TestSignalGenerator.Tone(1000, 0.1, 48_000, 1, ToneChannel.Both, 0.5).Skip(1000).Take(2400).ToArray();
        var level = AudioLevelMath.Measure(sine);
        Assert.Equal(-6.02, level.PeakDb, 1);
        Assert.Equal(-9.03, level.RmsDb, 1);
        Assert.False(level.Clipped);

        var clipped = AudioLevelMath.Measure([0.2f, 1f, -1f, 0.995f]);
        Assert.Equal(3, clipped.ClippedSamples);
        Assert.True(clipped.Clipped);
        Assert.Equal(AudioLevelMath.FloorDb, AudioLevelMath.Measure([]).PeakDb);
        Assert.Equal(50, AudioLevelMath.MeterPercent(-30), 6);
        Assert.Equal(0, AudioLevelMath.MeterPercent(-90), 6);
    }

    [Fact]
    public void Microphone_hint_prefers_clipping_then_silence()
    {
        static MicrophoneLevel L(double peakDb, int clipped = 0) => new(peakDb, peakDb - 9, Math.Pow(10, peakDb / 20), clipped);

        Assert.Equal(MicrophoneHint.Waiting, AudioLevelMath.Hint([]));
        Assert.Equal(MicrophoneHint.NoSignal, AudioLevelMath.Hint([L(-80), L(-75)]));
        Assert.Equal(MicrophoneHint.Quiet, AudioLevelMath.Hint([L(-50), L(-42)]));
        Assert.Equal(MicrophoneHint.Good, AudioLevelMath.Hint([L(-50), L(-18)]));
        Assert.Equal(MicrophoneHint.Good, AudioLevelMath.Hint([L(-1, 1), L(-18)]));
        Assert.Equal(MicrophoneHint.Clipping, AudioLevelMath.Hint([L(0, 4), L(-3), L(0, 2)]));
    }

    [Fact]
    public void Clipping_on_one_channel_is_not_hidden_by_mixing_down()
    {
        // 16-bit stereo: the left channel is at full scale, the right one is silent.
        var pcm16 = new byte[4 * 4];
        for (int frame = 0; frame < 4; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(frame * 4), frame % 2 == 0 ? short.MaxValue : short.MinValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(frame * 4 + 2), 0);
        }

        var format = new PcmFormat(48_000, 2, 16, false);
        var mixed = AudioLevelMath.Measure(AudioLevelMath.ToMono(pcm16, format));
        var perChannel = AudioLevelMath.MeasureCapture(pcm16, format);

        Assert.False(mixed.Clipped);
        Assert.True(perChannel.Clipped);
        Assert.Equal(4, perChannel.ClippedSamples);
        Assert.InRange(perChannel.PeakDb, -0.01, 0);
        Assert.False(AudioLevelMath.MeasureCapture(pcm16, new PcmFormat(48_000, 2, 8, false)).Clipped);
    }

    [Fact]
    public void Captured_buffers_convert_to_mono_in_every_common_format()
    {
        // 16-bit stereo: left full positive, right silent -> mono at half.
        var pcm16 = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(0), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(2), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(4), -32768);
        BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(6), 0);
        Assert.Equal([0.5f, -0.5f], AudioLevelMath.ToMono(pcm16, new PcmFormat(48_000, 2, 16, false)));

        // 32-bit float mono, plus a partial frame that is dropped.
        var f32 = new byte[10];
        BinaryPrimitives.WriteSingleLittleEndian(f32.AsSpan(0), 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(f32.AsSpan(4), -1f);
        Assert.Equal([0.25f, -1f], AudioLevelMath.ToMono(f32, new PcmFormat(48_000, 1, 32, true)));

        // 24-bit: 0x400000 is +0.5 and 0xC00000 is -0.5.
        byte[] pcm24 = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0];
        Assert.Equal([0.5f, -0.5f], AudioLevelMath.ToMono(pcm24, new PcmFormat(48_000, 1, 24, false)));

        Assert.Empty(AudioLevelMath.ToMono(pcm16, new PcmFormat(48_000, 2, 8, false)));
    }

    [Fact]
    public void Resampling_keeps_the_duration()
    {
        var oneSecond = new float[44_100];
        Assert.Equal(48_000, AudioLevelMath.Resample(oneSecond, 44_100, 48_000).Length);
        Assert.Equal(16_000, AudioLevelMath.Resample(new float[48_000], 48_000, 16_000).Length);
        Assert.Equal([0f, 0.5f, 1f, 1f], AudioLevelMath.Resample([0f, 1f], 1, 2));
        Assert.Empty(AudioLevelMath.Resample([], 48_000, 44_100));
        var recording = new RecordedAudio(new float[96_000], 48_000);
        Assert.Equal(TimeSpan.FromSeconds(2), recording.Duration);
    }

    [Fact]
    public async Task Demo_services_return_a_controller_and_a_recording()
    {
        var gamepads = new DemoGamepadTestService();
        var list = await gamepads.GetGamepadsAsync(CancellationToken.None);
        var pad = Assert.Single(list);
        Assert.True(pad.HasTriggerRumble);
        Assert.Equal(72, pad.BatteryPercent);
        Assert.NotNull(gamepads.ReadState(pad.Id));
        Assert.Null(gamepads.ReadState("missing"));
        Assert.True(gamepads.SetRumble(pad.Id, new RumbleRequest(0.5, 0, 0, 0)).Success);

        var sound = new DemoSoundTestService();
        var levels = new List<MicrophoneLevel>();
        var result = await sound.ListenAsync(TimeSpan.FromMilliseconds(200), new SyncProgress<MicrophoneLevel>(levels.Add), CancellationToken.None);
        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(TestSignalGenerator.FrameCount(0.2, DemoSoundTestService.DemoSampleRate), result.Value!.Samples.Length);
        Assert.NotEmpty(levels);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
