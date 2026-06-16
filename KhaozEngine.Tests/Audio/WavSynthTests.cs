using System;
using System.IO;
using System.Text;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class WavSynthTests : IDisposable
{
    private readonly string _dir;

    public WavSynthTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ke-wavsynth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private readonly record struct WavHeader(
        string Riff, string Wave, string Fmt, short Channels, int SampleRate, string Data, int DataLength);

    private static WavHeader Parse(byte[] bytes)
    {
        string riff = Encoding.ASCII.GetString(bytes, 0, 4);
        string wave = Encoding.ASCII.GetString(bytes, 8, 4);
        string fmt = Encoding.ASCII.GetString(bytes, 12, 4);
        short channels = BitConverter.ToInt16(bytes, 22);
        int sampleRate = BitConverter.ToInt32(bytes, 24);
        string data = Encoding.ASCII.GetString(bytes, 36, 4);
        int dataLength = BitConverter.ToInt32(bytes, 40);
        return new WavHeader(riff, wave, fmt, channels, sampleRate, data, dataLength);
    }

    [Fact]
    public void WriteToneProducesValidMono16RiffWave()
    {
        string path = Path.Combine(_dir, "tone.wav");
        const int rate = 22050;
        const float seconds = 0.25f;
        WavSynth.WriteTone(path, 440f, seconds, sampleRate: rate);

        var h = Parse(File.ReadAllBytes(path));
        Assert.Equal("RIFF", h.Riff);
        Assert.Equal("WAVE", h.Wave);
        Assert.Equal("fmt ", h.Fmt);
        Assert.Equal("data", h.Data);
        Assert.Equal(1, h.Channels);
        Assert.Equal(rate, h.SampleRate);

        int expectedSamples = (int)(seconds * rate);
        Assert.Equal(expectedSamples * 2, h.DataLength);
    }

    [Fact]
    public void WriteNoiseProducesValidMono16RiffWave()
    {
        string path = Path.Combine(_dir, "noise.wav");
        const int rate = 44100;
        const float seconds = 0.1f;
        WavSynth.WriteNoise(path, seconds, sampleRate: rate);

        var h = Parse(File.ReadAllBytes(path));
        Assert.Equal("RIFF", h.Riff);
        Assert.Equal("WAVE", h.Wave);
        Assert.Equal("fmt ", h.Fmt);
        Assert.Equal("data", h.Data);
        Assert.Equal(1, h.Channels);
        Assert.Equal(rate, h.SampleRate);

        int expectedSamples = (int)(seconds * rate);
        Assert.Equal(expectedSamples * 2, h.DataLength);
    }

    [Fact]
    public void WriteNoiseIsReproducibleForFixedSeed()
    {
        string a = Path.Combine(_dir, "a.wav");
        string b = Path.Combine(_dir, "b.wav");
        WavSynth.WriteNoise(a, 0.2f);
        WavSynth.WriteNoise(b, 0.2f);

        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }
}
