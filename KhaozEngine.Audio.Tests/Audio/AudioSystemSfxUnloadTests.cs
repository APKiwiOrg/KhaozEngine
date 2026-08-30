using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Releasing SFX buffers (#116). ISfxBackend had Load and no counterpart, so a loaded sound lived for the rest
/// of the process and a zone-scoped or level-scoped sound set only ever grew. These pin the unregister path and
/// the default-interface-member shape that keeps an existing backend compiling untouched.
/// </summary>
public sealed class AudioSystemSfxUnloadTests : IDisposable
{
    private readonly string _dir;

    public AudioSystemSfxUnloadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ke-sfx-unload-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        WavSynth.WriteTone(Path.Combine(_dir, "blip.wav"), 440f, 0.05f);
        WavSynth.WriteTone(Path.Combine(_dir, "thud.wav"), 220f, 0.05f);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (AudioSystem audio, FakeSfxBackend sfx) NewLoaded(params string[] names)
    {
        var sfx = new FakeSfxBackend();
        var audio = new AudioSystem(new NullMusicBackend(), sfx);
        audio.RegisterSfxes(names);
        audio.LoadContent(_dir);
        return (audio, sfx);
    }

    [Fact]
    public void UnregisterSfx_ReleasesTheBackendBufferAndForgetsTheName()
    {
        var (audio, sfx) = NewLoaded("blip");
        Assert.True(audio.IsSfxLoaded("blip"));

        Assert.True(audio.UnregisterSfx("blip"));

        Assert.Equal(new[] { 0 }, sfx.Unloaded);          // the handle the fake Load handed out
        Assert.False(audio.IsSfxLoaded("blip"));
        audio.PlaySfx("blip");
        Assert.Empty(sfx.Plays);                          // the name no longer resolves to a buffer
    }

    [Fact]
    public void UnregisterSfx_UnknownName_ReleasesNothing()
    {
        var (audio, sfx) = NewLoaded("blip");

        Assert.False(audio.UnregisterSfx("nope"));

        Assert.Empty(sfx.Unloaded);
        Assert.True(audio.IsSfxLoaded("blip"));
    }

    [Fact]
    public void UnregisterSfxes_ReleasesTheWholeSet()
    {
        // The zone-unload shape the issue is about: a set loaded together, released together.
        var (audio, sfx) = NewLoaded("blip", "thud");

        Assert.Equal(2, audio.UnregisterSfxes(new[] { "blip", "thud" }));

        Assert.Equal(2, sfx.Unloaded.Count);
        Assert.False(audio.IsSfxLoaded("blip"));
        Assert.False(audio.IsSfxLoaded("thud"));
    }

    [Fact]
    public void RegisteringAgainAfterUnregister_LoadsTheSoundBack()
    {
        var (audio, sfx) = NewLoaded("blip");
        audio.UnregisterSfx("blip");

        audio.RegisterSfx("blip");

        Assert.True(audio.IsSfxLoaded("blip"));
        Assert.Equal(2, sfx.LoadedPaths.Count);           // loaded once at LoadContent, once on re-register
    }

    [Fact]
    public void ABackendWithoutAnUnloadOverride_StillCompilesAndDoesNothing()
    {
        // The whole point of the default interface member: a game's own backend written before Unload existed
        // keeps compiling, and AudioSystem's unregister path is safe to call against it.
        var backend = new UnloadlessSfxBackend();
        var audio = new AudioSystem(new NullMusicBackend(), backend);
        audio.RegisterSfx("blip");
        audio.LoadContent(_dir);

        Assert.True(audio.UnregisterSfx("blip"));         // reached the no-op default without throwing
        Assert.False(audio.IsSfxLoaded("blip"));
    }

    /// <summary>An ISfxBackend implementing only the members that existed before #116.</summary>
    private sealed class UnloadlessSfxBackend : ISfxBackend
    {
        private int _next;
        public string Name => "Unloadless";
        public int Load(string path) => _next++;
        public void Play(int handle, float gain, float pitch, bool positional, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward, Vector3 up) { }
        public void StopAll() { }
        public void Dispose() { }
    }
}
