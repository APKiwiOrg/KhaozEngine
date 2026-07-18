using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests;

public class RngTimingTests
{
    // Simulate "kills recorded in iteration order -> deferred loot rolls drawing from one RNG".
    private static List<int> Run(ulong seed)
    {
        var w = new World();
        var rng = new DeterministicRng(seed);
        var loot = new List<int>();
        var ecb = new EntityCommandBuffer();
        for (int kill = 0; kill < 25; kill++)
            ecb.Defer(_ => loot.Add(rng.Next(100)));   // RNG drawn at playback, in record order
        ecb.Playback(w);
        return loot;
    }

    [Fact]
    public void DeferredRngDrawSequenceIsReproducible()
    {
        Assert.Equal(Run(2024), Run(2024));            // identical seed + order -> identical loot
    }

    [Fact]
    public void DifferentSeedDiffers()
    {
        Assert.NotEqual(Run(1), Run(2));
    }
}
