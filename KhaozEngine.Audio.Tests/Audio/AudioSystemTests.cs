using System;
using System.Collections.Generic;
using KhaozEngine.Audio;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class AudioSystemTests
{
    private static (AudioSystem audio, FakeMusicBackend backend) NewLoaded(params string[] tracks)
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, tracks);
        audio.LoadContent("tracks");
        return (audio, backend);
    }

    [Fact]
    public void RegistersTracksFromCtorAndRegisterApis()
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, new[] { "a", "b" });
        audio.RegisterTrack("c");
        audio.RegisterTracks(new[] { "d", "e" });

        audio.LoadContent("tracks");

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

        audio.LoadContent("tracks");

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
    public void RegisterAfterLoad_FailedLoad_IsNotPhantomed()
    {
        var (audio, backend) = NewLoaded("a");
        backend.LoadSucceeds = false;

        audio.RegisterTrack("missing");          // post-load, load fails -> must NOT be committed
        Assert.Equal(1, backend.TrackCount);

        // The failed name is not stuck: once loads can succeed, re-registering it works.
        backend.LoadSucceeds = true;
        audio.RegisterTrack("missing");
        Assert.Equal(2, backend.TrackCount);
        Assert.Contains("missing", backend.LoadedTracks);
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
        audio.SetRng(new DeterministicRng(12345));

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
    public void TogglingMusicBeforeFirstUpdateDoesNotDoubleStart()
    {
        var (audio, backend) = NewLoaded("a", "b");

        audio.MusicEnabled = false;           // before any Update
        audio.MusicEnabled = true;            // initiates one track
        Assert.Single(backend.PlayedIndices);

        audio.Update();                       // must NOT start a second track
        Assert.Single(backend.PlayedIndices);
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
        a1.SetRng(new DeterministicRng(7));
        var (a2, b2) = NewLoaded("a", "b", "c");
        a2.SetRng(new DeterministicRng(7));

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

    [Fact]
    public void PlayTrackByName_PlaysItAndSetsCurrentTrack()
    {
        var (audio, backend) = NewLoaded("a", "b", "c");
        string? changed = "unset";
        audio.TrackChanged += name => changed = name;

        audio.PlayTrack("b");

        Assert.Equal(1, backend.PlayedIndices[^1]);   // "b" is index 1
        Assert.Equal("b", audio.CurrentTrack);
        Assert.Equal("b", changed);
    }

    [Fact]
    public void PlayTrackByIndex_PlaysIt()
    {
        var (audio, backend) = NewLoaded("a", "b", "c");
        audio.PlayTrack(2);
        Assert.Equal(2, backend.PlayedIndices[^1]);
        Assert.Equal("c", audio.CurrentTrack);
    }

    [Fact]
    public void PlayTrackByName_Unknown_IsLoggedNoOp()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.PlayTrack("nope");
        Assert.Empty(backend.PlayedIndices);
        Assert.Null(audio.CurrentTrack);
    }

    [Fact]
    public void PlayTrackByIndex_OutOfRange_IsNoOp()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.PlayTrack(5);
        audio.PlayTrack(-1);
        Assert.Empty(backend.PlayedIndices);
        Assert.Null(audio.CurrentTrack);
    }

    [Fact]
    public void CurrentTrackIsNullBeforeAnyPlay()
    {
        var (audio, _) = NewLoaded("a", "b");
        Assert.Null(audio.CurrentTrack);
    }

    [Fact]
    public void DisablingMusicClearsCurrentTrack()
    {
        var (audio, _) = NewLoaded("a", "b");
        string? last = "unset";
        audio.PlayTrack("a");
        audio.TrackChanged += n => last = n;
        audio.MusicEnabled = false;
        Assert.Null(audio.CurrentTrack);
        Assert.Null(last);                 // TrackChanged(null) fired on stop
    }

    [Fact]
    public void DefaultPlayModeIsRandomRotation()
    {
        var (audio, _) = NewLoaded("a");
        Assert.Equal(PlayMode.RandomRotation, audio.PlayMode);
    }

    [Fact]
    public void RepeatOne_ReplaysSameTrackOnAutoAdvance()
    {
        var (audio, backend) = NewLoaded("a", "b", "c");
        audio.PlayTrack("b");                 // index 1; sets _lastTrackIndex = 1, _started = true
        audio.PlayMode = PlayMode.RepeatOne;

        backend.IsPlaying = false;
        audio.Update();                       // auto-advance under RepeatOne

        // Random rotation would AVOID index 1 (last played); RepeatOne replays exactly index 1.
        Assert.Equal(1, backend.PlayedIndices[^1]);
        Assert.Equal("b", audio.CurrentTrack);
    }

    [Fact]
    public void TrackChanged_FiresOnChange_NotOnSameNameRepeat()
    {
        var (audio, backend) = NewLoaded("a", "b", "c");
        var names = new List<string?>();
        audio.TrackChanged += n => names.Add(n);

        audio.PlayTrack("a");                 // change -> "a"
        audio.PlayTrack("b");                 // change -> "b"
        audio.PlayMode = PlayMode.RepeatOne;
        backend.IsPlaying = false;
        audio.Update();                       // RepeatOne replay of "b" -> NO new event

        Assert.Equal(new string?[] { "a", "b" }, names);
    }

    [Fact]
    public void PartialLoadFailure_KeepsNamesAlignedSoCurrentTrackIsCorrect()
    {
        // "b" fails to load: the backend's track list compacts to [a, c]. AudioSystem must compact its
        // own name list to match, or name<->index lookups drift and CurrentTrack reports the wrong song.
        var backend = new FakeMusicBackend();
        backend.FailTracks.Add("b");
        var audio = new AudioSystem(backend, new[] { "a", "b", "c" });
        audio.LoadContent("tracks");

        Assert.Equal(new[] { "a", "c" }, backend.LoadedTracks);   // "b" skipped
        Assert.Equal(2, backend.TrackCount);

        audio.PlayTrack("c");
        Assert.Equal(1, backend.PlayedIndices[^1]);   // "c" is now compact index 1, not 2
        Assert.Equal("c", audio.CurrentTrack);        // and the now-playing name resolves correctly
    }

    [Fact]
    public void Update_TransientIsPlayingError_SkipsFrameAndStaysAvailable()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.Update();                              // deferred first play
        int playedBefore = backend.PlayedIndices.Count;

        backend.ThrowOnNextIsPlayingReads = 1;
        audio.Update();                              // IsPlaying read throws -> skip frame, do NOT latch
        Assert.Equal(playedBefore, backend.PlayedIndices.Count);   // no advance this frame

        backend.IsPlaying = false;
        audio.Update();                              // recovered -> audio still alive, advances
        Assert.Equal(playedBefore + 1, backend.PlayedIndices.Count);
    }

    [Fact]
    public void Update_StreamingRefillThrows_StopsTrackAndStaysUsable()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.Update();                              // deferred first play -> a track is playing
        int playedBefore = backend.PlayedIndices.Count;
        int stoppedBefore = backend.StopCount;

        // A corrupt or truncated music file makes the streaming refill throw mid-playback (the real OpenAL
        // backend surfaces EndOfStreamException from the decoder). The frame loop must contain it, not crash
        // the whole game.
        backend.ThrowOnNextUpdateCalls = 1;
        audio.Update();                              // refill throws -> must NOT propagate out of Update

        // The failing track was stopped cleanly (logged + stopped), not left half-playing.
        Assert.True(backend.StopCount > stoppedBefore);

        // The audio system stays usable afterwards: the stopped track auto-advances on the next frame.
        audio.Update();
        Assert.True(backend.PlayedIndices.Count > playedBefore);
    }
}
