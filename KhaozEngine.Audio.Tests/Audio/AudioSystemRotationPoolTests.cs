using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Audio;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class AudioSystemRotationPoolTests
{
    private static (AudioSystem audio, FakeMusicBackend backend) NewLoaded(params string[] tracks)
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, tracks);
        audio.LoadContent("tracks");
        return (audio, backend);
    }

    // Plays the random rotation `count` times, resetting IsPlaying between plays.
    private static void Spin(AudioSystem audio, FakeMusicBackend backend, int count)
    {
        for (int i = 0; i < count; i++)
        {
            backend.IsPlaying = false;
            audio.PlayRandomTrack();
        }
    }

    [Fact]
    public void SingletonPool_AlwaysSelectsThatTrack()
    {
        var (audio, backend) = NewLoaded("a", "b", "c", "d");
        audio.SetRng(new DeterministicRng(1));
        audio.SetRotationPool(new[] { "a" });   // index 0

        Spin(audio, backend, 50);

        Assert.Equal(50, backend.PlayedIndices.Count);
        Assert.All(backend.PlayedIndices, idx => Assert.Equal(0, idx));
    }

    [Fact]
    public void TwoTrackPool_NeverSelectsOutsideThePool_AcrossManySeeds()
    {
        for (int seed = 0; seed < 25; seed++)
        {
            var (audio, backend) = NewLoaded("a", "b", "c", "d");
            audio.SetRng(new DeterministicRng((ulong)seed));
            audio.SetRotationPool(new[] { "a", "b" });   // indices 0, 1

            Spin(audio, backend, 60);

            Assert.All(backend.PlayedIndices, idx => Assert.True(idx == 0 || idx == 1,
                $"seed {seed} selected out-of-pool index {idx}"));
            // both pool members should turn up over 60 picks
            Assert.Contains(0, backend.PlayedIndices);
            Assert.Contains(1, backend.PlayedIndices);
        }
    }

    [Fact]
    public void NullPool_AllTracksEligible()
    {
        var (audio, backend) = NewLoaded("a", "b", "c", "d");
        audio.SetRng(new DeterministicRng(99));
        audio.SetRotationPool(null);   // explicit default

        Spin(audio, backend, 200);

        Assert.Equal(new[] { 0, 1, 2, 3 }, backend.PlayedIndices.Distinct().OrderBy(i => i).ToArray());
    }

    [Fact]
    public void ResettingPoolToNull_RestoresAllTracks()
    {
        var (audio, backend) = NewLoaded("a", "b", "c", "d");
        audio.SetRng(new DeterministicRng(5));
        audio.SetRotationPool(new[] { "a" });
        Spin(audio, backend, 20);
        Assert.All(backend.PlayedIndices, idx => Assert.Equal(0, idx));

        backend.PlayedIndices.Clear();
        audio.SetRotationPool(null);
        Spin(audio, backend, 200);

        Assert.Equal(new[] { 0, 1, 2, 3 }, backend.PlayedIndices.Distinct().OrderBy(i => i).ToArray());
    }

    [Fact]
    public void PlayTrackByName_StillPlaysOutsideThePool()
    {
        var (audio, backend) = NewLoaded("a", "b", "c", "d");
        audio.SetRotationPool(new[] { "a" });

        audio.PlayTrack("c");   // index 2, outside the pool, must still play on demand

        Assert.Equal(2, backend.PlayedIndices[^1]);
        Assert.Equal("c", audio.CurrentTrack);
    }

    [Fact]
    public void PlayTrackByIndex_StillPlaysOutsideThePool()
    {
        var (audio, backend) = NewLoaded("a", "b", "c", "d");
        audio.SetRotationPool(new[] { "a" });

        audio.PlayTrack(3);   // "d", outside the pool

        Assert.Equal(3, backend.PlayedIndices[^1]);
        Assert.Equal("d", audio.CurrentTrack);
    }

    [Fact]
    public void UnknownPoolNames_AreIgnored_KnownOnesStillSelected()
    {
        var (audio, backend) = NewLoaded("a", "b", "c", "d");
        audio.SetRng(new DeterministicRng(3));
        audio.SetRotationPool(new[] { "a", "ghost", "b" });   // "ghost" is not registered

        Spin(audio, backend, 60);

        Assert.All(backend.PlayedIndices, idx => Assert.True(idx == 0 || idx == 1));
        Assert.Contains(0, backend.PlayedIndices);
        Assert.Contains(1, backend.PlayedIndices);
    }

    [Fact]
    public void EmptyResolvedPool_FallsBackToAllTracks_NeverSilent()
    {
        var (audio, backend) = NewLoaded("a", "b", "c", "d");
        audio.SetRng(new DeterministicRng(11));
        audio.SetRotationPool(new[] { "nope", "alsonope" });   // none registered

        Spin(audio, backend, 200);

        // Must not be silent: every spin played something, drawing from all four tracks.
        Assert.Equal(200, backend.PlayedIndices.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, backend.PlayedIndices.Distinct().OrderBy(i => i).ToArray());
    }

    [Fact]
    public void EmptyPoolList_FallsBackToAllTracks()
    {
        var (audio, backend) = NewLoaded("a", "b", "c", "d");
        audio.SetRng(new DeterministicRng(8));
        audio.SetRotationPool(Array.Empty<string>());

        Spin(audio, backend, 200);

        Assert.Equal(200, backend.PlayedIndices.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, backend.PlayedIndices.Distinct().OrderBy(i => i).ToArray());
    }

    [Fact]
    public void PoolResolvesLazily_NamesRegisteredAfterSetPool()
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, new[] { "a", "b" });
        audio.SetRotationPool(new[] { "c" });   // "c" not registered yet
        audio.RegisterTrack("c");
        audio.RegisterTrack("d");
        audio.LoadContent("tracks");            // now a,b,c,d are loaded; "c" is index 2
        audio.SetRng(new DeterministicRng(2));

        Spin(audio, backend, 40);

        Assert.All(backend.PlayedIndices, idx => Assert.Equal(2, idx));   // pool ["c"] resolves to index 2
    }

    [Fact]
    public void PoolMembersAvoidImmediateRepeat()
    {
        var (audio, backend) = NewLoaded("a", "b", "c", "d");
        audio.SetRng(new DeterministicRng(17));
        audio.SetRotationPool(new[] { "a", "b", "c" });   // 3-member pool

        Spin(audio, backend, 200);

        for (int i = 1; i < backend.PlayedIndices.Count; i++)
        {
            Assert.NotEqual(backend.PlayedIndices[i - 1], backend.PlayedIndices[i]);
        }
    }

    [Fact]
    public void NullPool_RngStreamUnchanged_BackCompat()
    {
        // The null-pool path must produce the identical index stream as before SetRotationPool existed:
        // one AudioSystem never touches the pool, the other sets it to null explicitly. Same seed -> same picks.
        var (a1, b1) = NewLoaded("a", "b", "c");
        a1.SetRng(new DeterministicRng(42));
        var (a2, b2) = NewLoaded("a", "b", "c");
        a2.SetRotationPool(null);
        a2.SetRng(new DeterministicRng(42));

        Spin(a1, b1, 50);
        Spin(a2, b2, 50);

        Assert.Equal(b1.PlayedIndices, b2.PlayedIndices);
    }
}
