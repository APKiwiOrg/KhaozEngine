using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Covers <see cref="AudioSystem.IsSfxLoaded"/> and the first-available <c>PlaySfx</c> /
/// <c>PlaySfx3D</c> candidate-list overloads (game builds the list, engine plays the first loaded).
/// </summary>
public sealed class AudioSystemSfxFirstAvailableTests : IDisposable
{
    private readonly string _dir;

    public AudioSystemSfxFirstAvailableTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ke-sfx-first-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Real WAVs so AudioSystem.LoadSfx's File.Exists gate passes for these two names.
        WavSynth.WriteTone(Path.Combine(_dir, "railgun.wav"), 440f, 0.05f);
        WavSynth.WriteTone(Path.Combine(_dir, "default.wav"), 330f, 0.05f);
        // "broken" has a file but the backend load fails (registered-but-missing buffer).
        WavSynth.WriteTone(Path.Combine(_dir, "broken.wav"), 220f, 0.05f);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (AudioSystem audio, FakeSfxBackend sfx) NewLoaded()
    {
        var sfx = new FakeSfxBackend();
        sfx.FailPaths.Add(Path.Combine(_dir, "broken.wav"));   // registered, file present, backend load -> -1
        var audio = new AudioSystem(new NullMusicBackend(), sfx);
        audio.RegisterSfxes(new[] { "railgun", "default", "broken" });
        audio.LoadContent(_dir);
        return (audio, sfx);
    }

    [Fact]
    public void IsSfxLoadedTrueForLoadedFalseForUnregisteredAndMissing()
    {
        var (audio, _) = NewLoaded();

        Assert.True(audio.IsSfxLoaded("railgun"));   // loaded
        Assert.False(audio.IsSfxLoaded("nope"));     // never registered
        Assert.False(audio.IsSfxLoaded("broken"));   // registered but backend load failed
    }

    [Fact]
    public void PlaySfx3DPlaysFirstLoadedSkippingEarlierUnloaded()
    {
        var (audio, sfx) = NewLoaded();
        var pos = new Vector3(1, 2, 3);

        // "missing" (unregistered) and "broken" (load failed) precede the loaded "railgun".
        bool played = audio.PlaySfx3D(new[] { "missing", "broken", "railgun", "default" }, pos);

        Assert.True(played);
        var call = Assert.Single(sfx.Plays);
        Assert.True(call.Positional);
        Assert.Equal(pos, call.Position);
        // Routed to railgun's handle (load order railgun=0, default=1; "broken" failed so no handle).
        Assert.Equal(0, call.Handle);
    }

    [Fact]
    public void PlaySfxPlaysFirstLoadedFromList()
    {
        var (audio, sfx) = NewLoaded();

        bool played = audio.PlaySfx(new[] { "broken", "default" });

        Assert.True(played);
        Assert.Single(sfx.Plays);
        Assert.False(sfx.Plays[0].Positional);
    }

    [Fact]
    public void AllUnloadedListDoesNotPlayWarnsOnceReturnsFalse()
    {
        var (audio, sfx) = NewLoaded();

        var list = new[] { "missing", "broken" };
        Assert.False(audio.PlaySfx(list));
        Assert.False(audio.PlaySfx(list));   // second call must not re-warn (deduped on joined list) and still no-op

        Assert.Empty(sfx.Plays);
    }

    [Fact]
    public void NullOrEmptyListIsNoOpReturnsFalseNoThrow()
    {
        var (audio, sfx) = NewLoaded();

        Assert.False(audio.PlaySfx((string[])null!));
        Assert.False(audio.PlaySfx(Array.Empty<string>()));
        Assert.False(audio.PlaySfx3D((string[])null!, Vector3.One));
        Assert.False(audio.PlaySfx3D(Array.Empty<string>(), Vector3.One));

        Assert.Empty(sfx.Plays);
    }

    [Fact]
    public void SingleKeyOverloadsUnchanged()
    {
        var (audio, sfx) = NewLoaded();

        // A string literal binds to the single-key overload (no ambiguity with IReadOnlyList<string>).
        audio.PlaySfx("railgun");
        audio.PlaySfx3D("default", new Vector3(0, 1, 0));

        Assert.Equal(2, sfx.Plays.Count);
        Assert.False(sfx.Plays[0].Positional);
        Assert.True(sfx.Plays[1].Positional);
    }
}
