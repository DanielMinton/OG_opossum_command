using System.Buffers.Binary;

namespace OG_opossum_command.Core.Feedback;

/// <summary>
/// Builds the app's sounds in memory as 16-bit mono PCM WAV files (ADR-057), so no audio files ship and nothing is
/// downloaded. Each note is a sine with two soft overtones, a short attack and an exponential decay that ends at
/// exactly zero, so no cue clicks. Every cue is quiet: its loudest sample is at or below <see cref="MaxPeak"/>
/// (about -15 dBFS).
/// </summary>
public static class ToneSynth
{
    /// <summary>Samples per second.</summary>
    public const int SampleRate = 44_100;

    /// <summary>The loudest any cue gets, as a fraction of full scale.</summary>
    public const double MaxPeak = 0.18;

    private const int HeaderBytes = 44;

    private sealed record Note(double Hz, double StartMs, double DurationMs);

    private sealed record Voice(Note[] Notes, double Peak);

    private static Voice VoiceFor(SoundCue cue) => cue switch
    {
        SoundCue.Tap => new([new Note(1175, 0, 45)], 0.10),
        SoundCue.Navigate => new([new Note(1568, 0, 30)], 0.06),
        SoundCue.Start => new([new Note(659.26, 0, 110), new Note(987.77, 60, 120)], 0.15),
        SoundCue.Success => new([new Note(1046.5, 0, 220), new Note(1318.51, 55, 220), new Note(1567.98, 110, 240)], 0.15),
        SoundCue.Failure => new([new Note(440, 0, 170), new Note(349.23, 95, 200)], MaxPeak),
        _ => throw new ArgumentOutOfRangeException(nameof(cue), cue, "Unknown sound cue."),
    };

    /// <summary>The cue as a complete WAV file.</summary>
    public static byte[] RenderWav(SoundCue cue)
    {
        var samples = RenderSamples(cue);
        var dataBytes = samples.Length * 2;
        var wav = new byte[HeaderBytes + dataBytes];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16); // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2); // block align
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16); // bits per sample
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(HeaderBytes + (i * 2))..], samples[i]);
        }

        return wav;
    }

    /// <summary>The cue as 16-bit samples (exposed for tests).</summary>
    public static short[] RenderSamples(SoundCue cue)
    {
        var voice = VoiceFor(cue);
        var totalMs = voice.Notes.Max(n => n.StartMs + n.DurationMs);
        var length = (int)Math.Ceiling(totalMs * SampleRate / 1000.0) + 1;
        var mix = new double[length];

        foreach (var note in voice.Notes)
        {
            var start = (int)Math.Round(note.StartMs * SampleRate / 1000.0);
            var count = (int)Math.Round(note.DurationMs * SampleRate / 1000.0);
            for (var i = 0; i < count && start + i < length; i++)
            {
                var t = (double)i / SampleRate;
                var phase = 2 * Math.PI * note.Hz * t;
                var tone = Math.Sin(phase) + (0.22 * Math.Sin(2 * phase)) + (0.07 * Math.Sin(3 * phase));
                mix[start + i] += tone * Envelope(i, count);
            }
        }

        var loudest = mix.Max(Math.Abs);
        var gain = loudest > 0 ? voice.Peak / loudest : 0;
        var samples = new short[length];
        for (var i = 0; i < length; i++)
        {
            samples[i] = (short)Math.Round(Math.Clamp(mix[i] * gain, -1.0, 1.0) * short.MaxValue);
        }

        return samples;
    }

    /// <summary>5 ms linear attack, exponential decay, and a linear fade over the last 6 ms that reaches zero.</summary>
    private static double Envelope(int index, int count)
    {
        var attack = (int)(0.005 * SampleRate);
        var release = (int)(0.006 * SampleRate);
        var level = index < attack ? (double)index / attack : Math.Exp(-4.0 * (index - attack) / Math.Max(1, count - attack));
        var remaining = count - 1 - index;
        if (remaining < release)
        {
            level *= Math.Max(0, (double)remaining / release);
        }

        return level;
    }
}
