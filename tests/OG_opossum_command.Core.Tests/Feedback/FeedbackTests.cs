using System.Buffers.Binary;
using OG_opossum_command.Core.Feedback;

namespace OG_opossum_command.Core.Tests.Feedback;

/// <summary>ADR-057: the synthesized cues are valid, quiet, short and click-free; motion and sound follow the rules.</summary>
public sealed class FeedbackTests
{
    public static TheoryData<SoundCue> Cues() => [.. Enum.GetValues<SoundCue>()];

    [Theory]
    [MemberData(nameof(Cues))]
    public void Wav_header_describes_16_bit_mono_pcm(SoundCue cue)
    {
        var wav = ToneSynth.RenderWav(cue);

        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(ToneSynth.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    [Theory]
    [MemberData(nameof(Cues))]
    public void Cue_is_quiet_short_and_starts_and_ends_silent(SoundCue cue)
    {
        var samples = ToneSynth.RenderSamples(cue);

        var peak = samples.Max(s => Math.Abs((int)s)) / (double)short.MaxValue;
        Assert.InRange(peak, 0.02, ToneSynth.MaxPeak + 0.001);
        Assert.InRange(samples.Length * 1000.0 / ToneSynth.SampleRate, 20, 400);
        Assert.InRange(Math.Abs((int)samples[0]), 0, 50);
        Assert.InRange(Math.Abs((int)samples[^1]), 0, 50);
    }

    [Fact]
    public void Navigate_is_quieter_than_tap_and_tap_quieter_than_results()
    {
        static double Peak(SoundCue c) => ToneSynth.RenderSamples(c).Max(s => Math.Abs((int)s));

        Assert.True(Peak(SoundCue.Navigate) < Peak(SoundCue.Tap));
        Assert.True(Peak(SoundCue.Tap) < Peak(SoundCue.Success));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData(" False ", false)]
    [InlineData("garbage", true)]
    public void Settings_are_on_unless_false(string? value, bool expected) => Assert.Equal(expected, FeedbackPolicy.IsOn(value));

    [Theory]
    [InlineData(true, false, null, true)]
    [InlineData(false, false, null, false)]
    [InlineData(true, true, null, false)]
    [InlineData(true, false, "false", false)]
    [InlineData(false, false, "true", false)]
    public void Windows_settings_win_over_the_motion_setting(bool effects, bool highContrast, string? setting, bool expected) =>
        Assert.Equal(expected, FeedbackPolicy.MotionEnabled(effects, highContrast, setting));

    [Theory]
    [InlineData(SoundCue.Tap, null, false, true, true)]
    [InlineData(SoundCue.Tap, "false", false, true, false)]
    [InlineData(SoundCue.Tap, null, false, false, false)]
    [InlineData(SoundCue.Success, null, true, true, false)]
    [InlineData(SoundCue.Tap, null, true, true, false)]
    [InlineData(SoundCue.Failure, null, true, true, true)]
    [InlineData(SoundCue.Failure, "false", true, true, false)]
    public void Quiet_mode_lets_only_failures_through(SoundCue cue, string? setting, bool quiet, bool visible, bool expected) =>
        Assert.Equal(expected, FeedbackPolicy.ShouldPlay(cue, setting, quiet, visible));
}
