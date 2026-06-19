using System;
using System.IO;

namespace KhaozEngine.Audio;

/// <summary>Oscillator shape for <see cref="WavSynth.WriteTone"/>.</summary>
public enum Waveform
{
    Sine,
    Square,
    Saw,
}

/// <summary>
/// Writes mono 16-bit PCM RIFF/WAVE files for placeholder SFX (no external assets needed). Tones and noise
/// bursts get a short linear attack + release envelope so they start and stop without clicks. Used by samples
/// and games to generate audible placeholders that the SFX backend can then load.
/// </summary>
public static class WavSynth
{
    /// <summary>
    /// Writes a single-cycle <paramref name="waveform"/> tone at <paramref name="frequencyHz"/> for
    /// <paramref name="seconds"/>, with a linear attack/release envelope.
    /// </summary>
    public static void WriteTone(
        string path,
        float frequencyHz,
        float seconds,
        Waveform waveform = Waveform.Sine,
        float amplitude = 0.6f,
        int sampleRate = 44100,
        float attack = 0.005f,
        float release = 0.05f)
    {
        int count = Math.Max(0, (int)(seconds * sampleRate));
        short[] samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / sampleRate;
            float phase = t * frequencyHz;
            float frac = phase - MathF.Floor(phase);   // 0..1 within the cycle
            float wave = waveform switch
            {
                Waveform.Sine => MathF.Sin(frac * MathF.Tau),
                Waveform.Square => frac < 0.5f ? 1f : -1f,
                Waveform.Saw => 2f * frac - 1f,
                _ => 0f,
            };
            float env = Envelope(i, count, sampleRate, attack, release);
            samples[i] = AudioConvert.ToShort(wave * amplitude * env);
        }
        Write(path, samples, sampleRate);
    }

    /// <summary>
    /// Writes a white-noise burst for <paramref name="seconds"/> with a linear attack/release envelope. The
    /// noise is deterministic (fixed xorshift seed) so output is byte-reproducible across runs.
    /// </summary>
    public static void WriteNoise(
        string path,
        float seconds,
        float amplitude = 0.5f,
        int sampleRate = 44100,
        float attack = 0.001f,
        float release = 0.08f)
    {
        int count = Math.Max(0, (int)(seconds * sampleRate));
        short[] samples = new short[count];
        uint state = 0x9E3779B9u;   // fixed seed -> reproducible
        for (int i = 0; i < count; i++)
        {
            // xorshift32, mapped to [-1, 1).
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            float n = (state / (float)uint.MaxValue) * 2f - 1f;
            float env = Envelope(i, count, sampleRate, attack, release);
            samples[i] = AudioConvert.ToShort(n * amplitude * env);
        }
        Write(path, samples, sampleRate);
    }

    // Linear attack/release: ramps up over `attack` seconds, holds, ramps down over `release` seconds.
    static float Envelope(int i, int count, int sampleRate, float attack, float release)
    {
        if (count <= 0) return 0f;
        int attackSamples = Math.Max(1, (int)(attack * sampleRate));
        int releaseSamples = Math.Max(1, (int)(release * sampleRate));
        float a = i < attackSamples ? (float)i / attackSamples : 1f;
        int fromEnd = count - 1 - i;
        float r = fromEnd < releaseSamples ? (float)fromEnd / releaseSamples : 1f;
        return Math.Min(a, r);
    }

    // Standard 44-byte RIFF/WAVE header (mono, 16-bit PCM) + interleaved sample data.
    static void Write(string path, short[] samples, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        int dataBytes = samples.Length * sizeof(short);
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataBytes);              // chunk size
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                          // fmt chunk size (PCM)
        w.Write((short)1);                    // audio format = PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bitsPerSample);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataBytes);
        for (int i = 0; i < samples.Length; i++) w.Write(samples[i]);
    }
}
