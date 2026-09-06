using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Issue #114: SFX playback had no priority anywhere, so once all voices were busy the OpenAL backend stole in
/// pure round-robin and a barrage of footsteps could cut a boss cue mid-play. These cover the call surface
/// (AudioSystem states a priority and it reaches the backend unchanged) and the compatibility contract (a
/// backend that never heard of priorities still gets its play). The stealing rule itself is
/// <see cref="SfxVoicePoolTests"/>, where it is pure and needs no audio device.
/// </summary>
public sealed class AudioSystemSfxPriorityTests : IDisposable
{
    private readonly string _dir;

    public AudioSystemSfxPriorityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ke-sfx-priority-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
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
    public void EveryPlayOverloadCarriesItsPriorityToTheBackend()
    {
        var (audio, sfx) = NewLoaded();
        var keys = new List<string> { "missing", "blip" };

        audio.PlaySfx("blip", SfxPriority.High);
        audio.PlaySfx3D("blip", new Vector3(1f, 2f, 3f), SfxPriority.Low);
        Assert.True(audio.PlaySfx(keys, SfxPriority.High));
        Assert.True(audio.PlaySfx3D(keys, new Vector3(4f, 5f, 6f), SfxPriority.Low));

        Assert.Equal(new[] { SfxPriority.High, SfxPriority.Low, SfxPriority.High, SfxPriority.Low }, sfx.PlayPriorities);
        // The rest of the play is untouched: the 3D calls still carry their positions and everything landed.
        Assert.Equal(4, sfx.Plays.Count);
        Assert.Equal(new Vector3(1f, 2f, 3f), sfx.Plays[1].Position);
        Assert.Equal(new Vector3(4f, 5f, 6f), sfx.Plays[3].Position);
    }

    [Fact]
    public void APlayThatStatesNoPriorityIsNormal()
    {
        var (audio, sfx) = NewLoaded();

        audio.PlaySfx("blip");
        audio.PlaySfx3D("blip", Vector3.One);
        audio.PlaySfx(new List<string> { "blip" });

        Assert.Equal(new[] { SfxPriority.Normal, SfxPriority.Normal, SfxPriority.Normal }, sfx.PlayPriorities);
    }

    [Fact]
    public void PriorityDoesNotDisturbTheGainMath()
    {
        var (audio, sfx) = NewLoaded();
        audio.MasterVolume = 0.5f;
        audio.SfxVolume = 0.5f;

        audio.PlaySfx("blip", SfxPriority.High, volume: 0.5f);

        Assert.Equal(0.125f, sfx.Plays[0].Gain, 5);
    }

    [Fact]
    public void ABackendWithoutThePriorityOverloadStillGetsThePlay()
    {
        // The default interface member forwards to the priority-free Play, so a game's own backend (or one built
        // before #114) keeps working with no recompile of its own.
        var backend = new PriorityBlindSfxBackend();
        var audio = new AudioSystem(new NullMusicBackend(), backend);
        audio.RegisterSfx("blip");
        audio.LoadContent(_dir);

        audio.PlaySfx("blip", SfxPriority.High);

        Assert.Equal(1, backend.Plays);
    }

    [Fact]
    public void ABackendWithoutTheAttenuationOverloadStillGetsThePositionalPlay()
    {
        var backend = new PriorityBlindSfxBackend();
        var audio = new AudioSystem(new NullMusicBackend(), backend);
        audio.RegisterSfx("blip");
        audio.LoadContent(_dir);
        audio.DefineBus("world", new SfxAttenuation(8f, 0.4f, 120f));

        audio.PlaySfx3D("blip", Vector3.One, bus: "world");

        Assert.Equal(1, backend.Plays);
    }

    /// <summary>An ISfxBackend implementing only the members that existed before #114.</summary>
    private sealed class PriorityBlindSfxBackend : ISfxBackend
    {
        private int _next;
        public int Plays;
        public string Name => "PriorityBlind";
        public int Load(string path) => _next++;
        public void Play(int handle, float gain, float pitch, bool positional, Vector3 position) => Plays++;
        public void SetListener(Vector3 position, Vector3 forward, Vector3 up) { }
        public void StopAll() { }
        public void Dispose() { }
    }
}
