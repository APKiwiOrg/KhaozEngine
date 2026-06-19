using System.Collections.Generic;
using KhaozEngine.Audio;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Verifies that AudioSystem reproduces the same track sequence when given the same DeterministicRng seed,
/// proving that the System.Random -> DeterministicRng migration achieves reproducibility.
/// </summary>
public sealed class AudioRandomTrackDeterminismTests
{
    private static (AudioSystem audio, FakeMusicBackend backend) NewLoaded(params string[] tracks)
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, tracks);
        audio.LoadContent("tracks");
        return (audio, backend);
    }

    [Fact]
    public void SameSeed_ProducesSameTrackSequence()
    {
        const ulong seed = 0xABCDEF01u;
        const int picks = 50;

        var (a1, b1) = NewLoaded("alpha", "beta", "gamma", "delta");
        a1.SetRng(new DeterministicRng(seed));

        var (a2, b2) = NewLoaded("alpha", "beta", "gamma", "delta");
        a2.SetRng(new DeterministicRng(seed));

        for (int i = 0; i < picks; i++)
        {
            b1.IsPlaying = false;
            a1.PlayRandomTrack();
            b2.IsPlaying = false;
            a2.PlayRandomTrack();
        }

        Assert.Equal(picks, b1.PlayedIndices.Count);
        Assert.Equal(picks, b2.PlayedIndices.Count);
        Assert.Equal(b1.PlayedIndices, b2.PlayedIndices);
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        const int picks = 30;

        var (a1, b1) = NewLoaded("alpha", "beta", "gamma");
        a1.SetRng(new DeterministicRng(1));

        var (a2, b2) = NewLoaded("alpha", "beta", "gamma");
        a2.SetRng(new DeterministicRng(999));

        for (int i = 0; i < picks; i++)
        {
            b1.IsPlaying = false; a1.PlayRandomTrack();
            b2.IsPlaying = false; a2.PlayRandomTrack();
        }

        // Different seeds -> different streams (statistically near-certain with 30 picks over 3 tracks)
        Assert.NotEqual(b1.PlayedIndices, b2.PlayedIndices);
    }

    [Fact]
    public void SameSeed_WithRotationPool_ProducesSameTrackSequence()
    {
        const ulong seed = 0x1234567890ABCDEFu;
        const int picks = 40;

        var (a1, b1) = NewLoaded("a", "b", "c", "d", "e");
        a1.SetRotationPool(new[] { "b", "c", "d" });
        a1.SetRng(new DeterministicRng(seed));

        var (a2, b2) = NewLoaded("a", "b", "c", "d", "e");
        a2.SetRotationPool(new[] { "b", "c", "d" });
        a2.SetRng(new DeterministicRng(seed));

        for (int i = 0; i < picks; i++)
        {
            b1.IsPlaying = false; a1.PlayRandomTrack();
            b2.IsPlaying = false; a2.PlayRandomTrack();
        }

        Assert.Equal(b1.PlayedIndices, b2.PlayedIndices);
    }
}
