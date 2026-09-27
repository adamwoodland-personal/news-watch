using System.IO;
using System.Media;

namespace NewsWatch.Services;

/// <summary>
/// Synthesizes a short, quiet rising three-note chime as an in-memory WAV at
/// startup, so no audio assets are shipped. Deliberately different from
/// PULSE//WATCH's two-note pings so the two apps can be told apart by ear.
/// </summary>
public static class SoundService
{
    private const int SampleRate = 44100;
    private const double Amplitude = 0.15; // subtle — well below full scale
    private const double NoteSeconds = 0.09;

    private static readonly byte[] NewsWav = BuildWav(659.25, 880.00, 1046.50); // E5 -> A5 -> C6

    public static void PlayNews() => Play(NewsWav);

    private static void Play(byte[] wav)
    {
        try
        {
            // SoundPlayer.Play is async (worker thread); a new instance per call
            // lets overlapping chimes each play without cutting the previous off badly.
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Play();
        }
        catch
        {
            // No audio device / audio service down — panels still show visually.
        }
    }

    private static byte[] BuildWav(params double[] notes)
    {
        var samples = notes.SelectMany(GenerateNote).ToArray();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataLen);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);            // PCM
        w.Write((short)1);            // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);      // byte rate
        w.Write((short)2);            // block align
        w.Write((short)16);           // bits per sample
        w.Write("data"u8);
        w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }

    private static short[] GenerateNote(double freq)
    {
        int count = (int)(SampleRate * NoteSeconds);
        var samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / SampleRate;
            // Quick attack, exponential decay — reads as a soft "blip", not a buzzer.
            double envelope = Math.Min(1.0, i / (SampleRate * 0.004)) * Math.Exp(-5.0 * t / NoteSeconds);
            samples[i] = (short)(Math.Sin(2 * Math.PI * freq * t) * envelope * Amplitude * short.MaxValue);
        }
        return samples;
    }
}
