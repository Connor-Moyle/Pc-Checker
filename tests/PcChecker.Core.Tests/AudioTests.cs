using PcChecker.Core.Audio;
using Xunit;

namespace PcChecker.Core.Tests;

public class AudioTextTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 50f)]
    [InlineData(1f, 100f)]
    [InlineData(1.5f, 100f)]
    [InlineData(-0.2f, 0f)]
    [InlineData(0.423f, 42.3f)]
    public void ScalarToPercent_clamps_and_rounds(float scalar, float expected)
    {
        Assert.Equal(expected, AudioText.ScalarToPercent(scalar), 3);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(50f, 0.5f)]
    [InlineData(100f, 1f)]
    [InlineData(250f, 1f)]
    [InlineData(-5f, 0f)]
    public void PercentToScalar_clamps(float percent, float expected)
    {
        Assert.Equal(expected, AudioText.PercentToScalar(percent), 5);
    }

    [Fact]
    public void Volume_conversions_round_trip()
    {
        for (int p = 0; p <= 100; p += 7)
        {
            Assert.Equal(p, AudioText.ScalarToPercent(AudioText.PercentToScalar(p)), 3);
        }

        Assert.Equal(0f, AudioText.ScalarToPercent(float.NaN));
        Assert.Equal(0f, AudioText.PercentToScalar(float.NaN));
    }

    [Fact]
    public void PeakToPercent_handles_null_and_range()
    {
        Assert.Equal(0, AudioText.PeakToPercent(null));
        Assert.Equal(0, AudioText.PeakToPercent(float.NaN));
        Assert.Equal(35, AudioText.PeakToPercent(0.35f), 3);
        Assert.Equal(100, AudioText.PeakToPercent(4f));
    }

    [Theory]
    [InlineData(24, 48000, 2, "24 bit, 48000 Hz, Stereo")]
    [InlineData(16, 44100, 1, "16 bit, 44100 Hz, Mono")]
    [InlineData(32, 96000, 8, "32 bit, 96000 Hz, 7.1 surround")]
    [InlineData(null, 48000, 6, "48000 Hz, 5.1 surround")]
    [InlineData(16, null, null, "16 bit")]
    public void BuildFormatText_joins_known_parts(int? bits, int? rate, int? channels, string expected)
    {
        Assert.Equal(expected, AudioText.BuildFormatText(bits, rate, channels));
    }

    [Fact]
    public void BuildFormatText_returns_null_when_nothing_known()
    {
        Assert.Null(AudioText.BuildFormatText(null, null, null));
        Assert.Null(AudioText.BuildFormatText(0, 0, 0));
    }

    [Theory]
    [InlineData("Speakers (Realtek(R) Audio)", "Speakers", "Realtek(R) Audio")]
    [InlineData("Headset Earphone (SteelSeries Arctis Nova Pro)", "Headset Earphone", "SteelSeries Arctis Nova Pro")]
    [InlineData("ASUS PG279QM (NVIDIA High Definition Audio)", "ASUS PG279QM", "NVIDIA High Definition Audio")]
    [InlineData("Microphone Array", "Microphone Array", null)]
    [InlineData("  Line In (2- USB Audio)  ", "Line In", "2- USB Audio")]
    public void SplitFriendlyName_separates_endpoint_from_adapter(string input, string name, string? iface)
    {
        var (actualName, actualInterface) = AudioText.SplitFriendlyName(input);
        Assert.Equal(name, actualName);
        Assert.Equal(iface, actualInterface);
    }

    [Fact]
    public void SplitFriendlyName_handles_empty_and_odd_brackets()
    {
        Assert.Equal(("Unknown device", null), AudioText.SplitFriendlyName(null));
        Assert.Equal(("Unknown device", null), AudioText.SplitFriendlyName("   "));
        Assert.Equal(("()", null), AudioText.SplitFriendlyName("()"));
        Assert.Equal(("Speakers )", null), AudioText.SplitFriendlyName("Speakers )"));
    }

    [Theory]
    [InlineData("Active", "Active")]
    [InlineData("NotPresent", "Not present")]
    [InlineData("Unplugged", "Unplugged")]
    [InlineData("Disabled", "Disabled")]
    [InlineData(null, "Unknown")]
    [InlineData("Weird", "Weird")]
    public void StateText_is_plain_english(string? state, string expected)
    {
        Assert.Equal(expected, AudioText.StateText(state));
    }

    [Fact]
    public void DefaultBadges_follow_flags()
    {
        var device = new AudioDevice("id", "Speakers", null, AudioFlow.Render, true, true, "Active", 50, false, null, null);
        Assert.Equal(["Default", "Default for calls"], AudioText.DefaultBadges(device));
        Assert.Equal(["Default for calls"], AudioText.DefaultBadges(device with { IsDefault = false }));
        Assert.Empty(AudioText.DefaultBadges(device with { IsDefault = false, IsDefaultCommunications = false }));
    }

    [Theory]
    [InlineData("Discord", "Discord", 10, false, "Discord")]
    [InlineData("@%SystemRoot%\\System32\\AudioSrv.Dll,-202", "chrome", 10, false, "Chrome")]
    [InlineData("", "Spotify.exe", 10, false, "Spotify")]
    [InlineData(null, null, 4321, false, "Process 4321")]
    [InlineData(null, null, 0, false, "Unknown app")]
    [InlineData("whatever", "svchost", 0, true, "System sounds")]
    public void SessionDisplayName_falls_back_in_order(string? display, string? process, int pid, bool system, string expected)
    {
        Assert.Equal(expected, AudioText.SessionDisplayName(display, process, pid, system));
    }

    [Fact]
    public void SortDevices_puts_defaults_first_then_active_then_name()
    {
        var devices = new List<AudioDevice>
        {
            Make("z-unplugged", false, false, "Unplugged"),
            Make("b-active", false, false, "Active"),
            Make("a-active", false, false, "Active"),
            Make("comms", false, true, "Active"),
            Make("default", true, false, "Active"),
        };

        var sorted = AudioText.SortDevices(devices).Select(d => d.Name).ToList();
        Assert.Equal(["default", "comms", "a-active", "b-active", "z-unplugged"], sorted);
    }

    [Fact]
    public void Summarize_includes_interface_and_mute_state()
    {
        var device = Make("Speakers", true, false, "Active") with { InterfaceName = "Realtek(R) Audio", VolumePercent = 42 };
        Assert.Equal("Speakers (Realtek(R) Audio) · 42 %", AudioText.Summarize(device));
        Assert.Equal("Speakers (Realtek(R) Audio) · muted", AudioText.Summarize(device with { Muted = true }));
        Assert.Equal("None", AudioText.Summarize(null));
    }

    private static AudioDevice Make(string name, bool isDefault, bool isComms, string state) =>
        new(name, name, null, AudioFlow.Render, isDefault, isComms, state, 50, false, null, null);
}

public class WaveFormatParserTests
{
    [Fact]
    public void Parses_plain_pcm_waveformatex()
    {
        var blob = BuildWaveFormat(formatTag: 1, channels: 2, sampleRate: 44100, bitsPerSample: 16);
        Assert.True(WaveFormatParser.TryParse(blob, out var summary));
        Assert.Equal(2, summary.Channels);
        Assert.Equal(44100, summary.SampleRate);
        Assert.Equal(16, summary.BitsPerSample);
        Assert.False(summary.IsFloat);
        Assert.Equal("16 bit, 44100 Hz, Stereo", summary.ToDisplayText());
    }

    [Fact]
    public void Extensible_uses_valid_bits_and_detects_float()
    {
        var pcm24 = BuildWaveFormat(formatTag: 0xFFFE, channels: 2, sampleRate: 48000, bitsPerSample: 32, validBits: 24, floatSubtype: false);
        Assert.True(WaveFormatParser.TryParse(pcm24, out var summary));
        Assert.Equal(24, summary.BitsPerSample);
        Assert.False(summary.IsFloat);
        Assert.Equal("24 bit, 48000 Hz, Stereo", summary.ToDisplayText());

        var float32 = BuildWaveFormat(formatTag: 0xFFFE, channels: 2, sampleRate: 48000, bitsPerSample: 32, validBits: 32, floatSubtype: true);
        Assert.True(WaveFormatParser.TryParse(float32, out summary));
        Assert.True(summary.IsFloat);
        Assert.Equal("32 bit float, 48000 Hz, Stereo", summary.ToDisplayText());
    }

    [Fact]
    public void Rejects_short_or_nonsense_blobs()
    {
        Assert.False(WaveFormatParser.TryParse(new byte[4], out _));
        Assert.False(WaveFormatParser.TryParse(BuildWaveFormat(1, 0, 48000, 16), out _));
        Assert.False(WaveFormatParser.TryParse(BuildWaveFormat(1, 2, 0, 16), out _));
        Assert.False(WaveFormatParser.TryParse(BuildWaveFormat(1, 200, 48000, 16), out _));
    }

    private static byte[] BuildWaveFormat(int formatTag, int channels, int sampleRate, int bitsPerSample, int? validBits = null, bool floatSubtype = false)
    {
        bool extensible = formatTag == 0xFFFE;
        var bytes = new byte[extensible ? 40 : 18];
        int blockAlign = channels * bitsPerSample / 8;
        BitConverter.GetBytes((ushort)formatTag).CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)channels).CopyTo(bytes, 2);
        BitConverter.GetBytes((uint)sampleRate).CopyTo(bytes, 4);
        BitConverter.GetBytes((uint)(sampleRate * blockAlign)).CopyTo(bytes, 8);
        BitConverter.GetBytes((ushort)blockAlign).CopyTo(bytes, 12);
        BitConverter.GetBytes((ushort)bitsPerSample).CopyTo(bytes, 14);
        BitConverter.GetBytes((ushort)(extensible ? 22 : 0)).CopyTo(bytes, 16);
        if (extensible)
        {
            BitConverter.GetBytes((ushort)(validBits ?? bitsPerSample)).CopyTo(bytes, 18);
            BitConverter.GetBytes(3u).CopyTo(bytes, 20); // channel mask: front left + right
            var subtype = floatSubtype
                ? new Guid("00000003-0000-0010-8000-00aa00389b71")
                : new Guid("00000001-0000-0010-8000-00aa00389b71");
            subtype.ToByteArray().CopyTo(bytes, 24);
        }

        return bytes;
    }
}

public class DemoAudioServiceTests
{
    [Fact]
    public async Task Demo_devices_are_sorted_with_defaults_first_and_have_formats()
    {
        var audio = new DemoAudioService();
        var devices = await audio.GetDevicesAsync(CancellationToken.None);

        Assert.True(devices.Count >= 4);
        Assert.True(devices.First(d => d.Flow == AudioFlow.Render).IsDefault);
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.FormatText)));
        Assert.Contains(devices, d => d.Flow == AudioFlow.Capture && d.IsDefault);
    }

    [Fact]
    public async Task Demo_set_default_moves_the_badge()
    {
        var audio = new DemoAudioService();
        int changed = 0;
        audio.DevicesChanged += (_, _) => changed++;

        var devices = await audio.GetDevicesAsync(CancellationToken.None);
        var other = devices.First(d => d.Flow == AudioFlow.Render && !d.IsDefault);

        var result = await audio.SetDefaultDeviceAsync(other.Id, AudioFlow.Render, false, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(1, changed);

        devices = await audio.GetDevicesAsync(CancellationToken.None);
        Assert.Single(devices, d => d.Flow == AudioFlow.Render && d.IsDefault);
        Assert.True(devices.Single(d => d.Id == other.Id).IsDefault);
    }

    [Fact]
    public async Task Demo_volume_and_mute_round_trip()
    {
        var audio = new DemoAudioService();
        var device = (await audio.GetDevicesAsync(CancellationToken.None)).First();

        Assert.True((await audio.SetVolumeAsync(device.Id, 137, CancellationToken.None)).Success);
        Assert.True((await audio.SetMuteAsync(device.Id, true, CancellationToken.None)).Success);

        var after = (await audio.GetDevicesAsync(CancellationToken.None)).Single(d => d.Id == device.Id);
        Assert.Equal(100, after.VolumePercent);
        Assert.True(after.Muted);
        Assert.Equal(0, after.PeakLevel);

        Assert.False((await audio.SetVolumeAsync("missing", 10, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Demo_sessions_can_be_adjusted_and_levels_are_reported()
    {
        var audio = new DemoAudioService();
        var sessions = await audio.GetSessionsAsync(CancellationToken.None);
        Assert.NotEmpty(sessions);
        Assert.Contains(sessions, s => s.DisplayName == "System sounds");

        var app = sessions.First(s => s.ProcessId > 0);
        Assert.True((await audio.SetSessionVolumeAsync(app.Id, 12, CancellationToken.None)).Success);
        Assert.True((await audio.SetSessionMuteAsync(app.Id, true, CancellationToken.None)).Success);
        var updated = (await audio.GetSessionsAsync(CancellationToken.None)).Single(s => s.Id == app.Id);
        Assert.Equal(12, updated.VolumePercent);
        Assert.True(updated.Muted);

        var levels = await ((IAudioLevelMeter)audio).ReadPeakLevelsAsync(CancellationToken.None);
        var devices = await audio.GetDevicesAsync(CancellationToken.None);
        Assert.All(devices, d => Assert.True(levels.ContainsKey(d.Id)));
        Assert.All(levels.Values, v => Assert.InRange(v, 0f, 1f));
    }
}
