using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class AudioSystemSfxTests : IDisposable
{
    private readonly string _dir;

    public AudioSystemSfxTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ke-sfx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // A real WAV file so AudioSystem.LoadSfx finds it (File.Exists gate) and the fake backend maps it.
        WavSynth.WriteTone(Path.Combine(_dir, "blip.wav"), 440f, 0.05f);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (AudioSystem audio, FakeSfxBackend sfx) NewLoaded()
    {
        var sfx = new FakeSfxBackend();
        var audio = new AudioSystem(new NullMusicBackend(), sfx);
        audio.RegisterSfx("blip");
        audio.LoadContent(_dir);
        return (audio, sfx);
    }

    [Fact]
    public void RegisterSfxAndLoadContentMapsTheName()
    {
        var (audio, sfx) = NewLoaded();

        // The fake Load returned a valid handle, so PlaySfx must route to it (not warn-once skip).
        audio.PlaySfx("blip");

        Assert.Single(sfx.LoadedPaths);
        Assert.Single(sfx.Plays);
    }

    [Fact]
    public void PlaySfxAppliesMasterTimesSfxTimesVolumeGain()
    {
        var (audio, sfx) = NewLoaded();
        // Defaults: Master 0.66, Sfx 0.7.
        audio.PlaySfx("blip", 0.5f);

        var call = Assert.Single(sfx.Plays);
        Assert.Equal(0.66f * 0.7f * 0.5f, call.Gain, 4);
        Assert.False(call.Positional);
    }

    [Fact]
    public void PlaySfx3DSetsPositionalAndPosition()
    {
        var (audio, sfx) = NewLoaded();
        var pos = new Vector3(3, -2, 5);
        audio.PlaySfx3D("blip", pos);

        var call = Assert.Single(sfx.Plays);
        Assert.True(call.Positional);
        Assert.Equal(pos, call.Position);
    }

    [Fact]
    public void SetListenerForwardsVerbatim()
    {
        var (audio, sfx) = NewLoaded();
        var p = new Vector3(1, 2, 3);
        var f = new Vector3(0, 0, -1);
        var u = new Vector3(0, 1, 0);
        audio.SetListener(p, f, u);

        var call = Assert.Single(sfx.Listeners);
        Assert.Equal(p, call.Position);
        Assert.Equal(f, call.Forward);
        Assert.Equal(u, call.Up);
    }

    [Fact]
    public void UnknownSfxNameIsNoOp()
    {
        var (audio, sfx) = NewLoaded();
        audio.PlaySfx("nope");
        audio.PlaySfx3D("nope", Vector3.One);

        Assert.Empty(sfx.Plays);
    }

    [Fact]
    public void SfxVolumeClampsToUnitRange()
    {
        var audio = new AudioSystem(new NullMusicBackend(), new FakeSfxBackend());

        audio.SfxVolume = 2.5f;
        Assert.Equal(1f, audio.SfxVolume);

        audio.SfxVolume = -1f;
        Assert.Equal(0f, audio.SfxVolume);

        audio.SfxVolume = 0.3f;
        Assert.Equal(0.3f, audio.SfxVolume);
    }
}
