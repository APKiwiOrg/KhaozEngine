using System;
using Microsoft.Xna.Framework.Content;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class AudioSystemTests
{
    private static (AudioSystem audio, FakeMusicBackend backend) NewLoaded(params string[] tracks)
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, tracks);
        audio.LoadContent(new ContentManager(new StubServiceProvider()));
        return (audio, backend);
    }

    [Fact]
    public void RegistersTracksFromCtorAndRegisterApis()
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, new[] { "a", "b" });
        audio.RegisterTrack("c");
        audio.RegisterTracks(new[] { "d", "e" });

        audio.LoadContent(new ContentManager(new StubServiceProvider()));

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, backend.LoadedTracks);
        Assert.Equal(5, backend.TrackCount);
    }

    [Fact]
    public void DuplicateRegistrationLoadsTrackOnce()
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, new[] { "a" });
        audio.RegisterTrack("a");                 // duplicate of the ctor seed
        audio.RegisterTracks(new[] { "b", "b" }); // duplicate within the batch

        audio.LoadContent(new ContentManager(new StubServiceProvider()));

        Assert.Equal(new[] { "a", "b" }, backend.LoadedTracks);
    }

    [Fact]
    public void RegisterAfterLoadEagerLoadsNewTrackOnce()
    {
        var (audio, backend) = NewLoaded("a", "b");

        audio.RegisterTrack("c");
        audio.RegisterTrack("c");   // idempotent: no second load

        Assert.Equal(new[] { "a", "b", "c" }, backend.LoadedTracks);
        Assert.Equal(3, backend.TrackCount);
    }

    [Fact]
    public void NullBackendThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new AudioSystem((IMusicBackend)null!));
    }

    [Fact]
    public void DefaultCtorHasExpectedDefaults()
    {
        var audio = new AudioSystem(new[] { "x" });

        Assert.Equal(0.66f, audio.MasterVolume);
        Assert.Equal(0.4f, audio.MusicVolume);
        Assert.True(audio.MusicEnabled);
    }

    [Fact]
    public void PlayRandomTrackNeverRepeatsPreviousIndex()
    {
        var (audio, backend) = NewLoaded("a", "b", "c");
        audio.SetRng(new Random(12345));

        for (int i = 0; i < 200; i++)
        {
            backend.IsPlaying = false;
            audio.PlayRandomTrack();
        }

        Assert.True(backend.PlayedIndices.Count > 100);
        for (int i = 1; i < backend.PlayedIndices.Count; i++)
        {
            Assert.NotEqual(backend.PlayedIndices[i - 1], backend.PlayedIndices[i]);
        }
    }

    [Fact]
    public void SingleTrackAlwaysPlaysIndexZero()
    {
        var (audio, backend) = NewLoaded("only");

        for (int i = 0; i < 5; i++)
        {
            audio.PlayRandomTrack();
        }

        Assert.Equal(5, backend.PlayedIndices.Count);
        Assert.All(backend.PlayedIndices, idx => Assert.Equal(0, idx));
    }

    [Fact]
    public void PlayUsesMasterTimesMusicVolume()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.MasterVolume = 0.5f;
        audio.MusicVolume = 0.4f;
        backend.Volumes.Clear();

        audio.PlayRandomTrack();

        Assert.Contains(backend.Volumes, v => Math.Abs(v - 0.2f) < 1e-4f);
    }

    [Fact]
    public void VolumeIsClampedToUnitRange()
    {
        var (audio, _) = NewLoaded("a");

        audio.MasterVolume = 5f;
        audio.MusicVolume = 5f;
        Assert.Equal(1f, audio.MasterVolume);
        Assert.Equal(1f, audio.MusicVolume);

        audio.MasterVolume = -5f;
        Assert.Equal(0f, audio.MasterVolume);
    }

    [Fact]
    public void DisablingMusicStopsBackend()
    {
        var (audio, backend) = NewLoaded("a", "b");

        audio.MusicEnabled = false;

        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public void EnablingMusicStartsPlayback()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.MusicEnabled = false;
        backend.PlayedIndices.Clear();

        audio.MusicEnabled = true;

        Assert.NotEmpty(backend.PlayedIndices);
    }

    [Fact]
    public void UpdateDefersFirstPlayThenAdvancesWhenStopped()
    {
        var (audio, backend) = NewLoaded("a", "b");
        Assert.Empty(backend.PlayedIndices);

        audio.Update();                       // first Update starts playback
        Assert.Single(backend.PlayedIndices);

        audio.Update();                       // still playing -> no new track
        Assert.Single(backend.PlayedIndices);

        backend.IsPlaying = false;
        audio.Update();                       // current track ended -> next
        Assert.Equal(2, backend.PlayedIndices.Count);
    }

    [Fact]
    public void PlayFailureDisablesFurtherPlayback()
    {
        var (audio, backend) = NewLoaded("a", "b");
        backend.PlaySucceeds = false;

        audio.PlayRandomTrack();              // fails -> _available = false
        backend.PlaySucceeds = true;
        backend.PlayedIndices.Clear();

        audio.PlayRandomTrack();              // _available false -> no-op
        Assert.Empty(backend.PlayedIndices);
    }

    [Fact]
    public void SetRngMakesRotationDeterministic()
    {
        var (a1, b1) = NewLoaded("a", "b", "c");
        a1.SetRng(new Random(7));
        var (a2, b2) = NewLoaded("a", "b", "c");
        a2.SetRng(new Random(7));

        for (int i = 0; i < 20; i++)
        {
            b1.IsPlaying = false; a1.PlayRandomTrack();
            b2.IsPlaying = false; a2.PlayRandomTrack();
        }

        Assert.Equal(b1.PlayedIndices, b2.PlayedIndices);
    }

    [Fact]
    public void DisposeDisposesBackend()
    {
        var (audio, backend) = NewLoaded("a");

        audio.Dispose();

        Assert.True(backend.Disposed);
    }
}
