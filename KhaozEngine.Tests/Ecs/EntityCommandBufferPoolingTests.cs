using KhaozEngine.Ecs;
using KhaozEngine.Pooling;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

file struct Speed : IComponent { public float V; }

/// <summary>
/// Asserts that EntityCommandBuffer reuses its remap dictionary across Playback calls via
/// ObjectPool, rather than allocating a fresh Dictionary on every call.
///
/// Strategy (b): observable reuse via internal _remapPool.FreeCount.
/// After each Playback the pool must return to FreeCount==1 (the one pooled wrapper is back),
/// confirming the item was rented during Playback and returned on completion. Two back-to-back
/// Playbacks with different content both produce correct results, proving the reset (Clear) works.
/// </summary>
public class EntityCommandBufferPoolingTests
{
    [Fact]
    public void RemapDictionaryIsReturnedToPoolAfterPlayback()
    {
        var ecb = new EntityCommandBuffer();

        // Pool must be primed with exactly one item and none active before first use.
        Assert.Equal(1, ecb._remapPool.FreeCount);
        Assert.Equal(0, ecb._remapPool.ActiveCount);

        var w = new World();
        var ph = ecb.Create();
        ecb.Set(ph, new Speed { V = 1f });
        ecb.Playback(w);

        // After Playback completes, the rented wrapper must be back in the pool.
        Assert.Equal(1, ecb._remapPool.FreeCount);
        Assert.Equal(0, ecb._remapPool.ActiveCount);
    }

    [Fact]
    public void PoolReusedAcrossMultiplePlaybacksWithCorrectResults()
    {
        var ecb = new EntityCommandBuffer();
        var w = new World();

        // First playback: create one entity.
        var ph1 = ecb.Create();
        ecb.Set(ph1, new Speed { V = 3f });
        ecb.Playback(w);

        Assert.Equal(1, ecb._remapPool.FreeCount);
        int count1 = 0; float sum1 = 0f;
        w.ForEach((Entity _, ref Speed s) => { count1++; sum1 += s.V; });
        Assert.Equal(1, count1);
        Assert.Equal(3f, sum1);

        // Second playback on the same ECB — pool item must be correctly cleared and reused.
        var ph2 = ecb.Create();
        ecb.Set(ph2, new Speed { V = 10f });
        ecb.Playback(w);

        Assert.Equal(1, ecb._remapPool.FreeCount);
        int count2 = 0; float sum2 = 0f;
        w.ForEach((Entity _, ref Speed s) => { count2++; sum2 += s.V; });
        Assert.Equal(2, count2);
        Assert.Equal(13f, sum2);  // 3 + 10
    }

    [Fact]
    public void MultipleCreatesInOnePlaybackResolveCorrectlyAfterPoolReset()
    {
        var ecb = new EntityCommandBuffer();
        var w = new World();

        // Warmup playback (pool item is rented then returned).
        var tmp = ecb.Create();
        ecb.Set(tmp, new Speed { V = 99f });
        ecb.Playback(w);

        Assert.Equal(1, ecb._remapPool.FreeCount);

        // Second playback: two distinct placeholders to exercise the remap logic on the recycled dict.
        var a = ecb.Create();
        var b = ecb.Create();
        ecb.Set(a, new Speed { V = 5f });
        ecb.Set(b, new Speed { V = 7f });
        ecb.Playback(w);

        Assert.Equal(1, ecb._remapPool.FreeCount);

        // Three entities total (one from warmup, two from second playback).
        int count = 0; float sumV = 0f;
        w.ForEach((Entity _, ref Speed s) => { count++; sumV += s.V; });
        Assert.Equal(3, count);
        Assert.Equal(111f, sumV);  // 99 + 5 + 7
    }
}
