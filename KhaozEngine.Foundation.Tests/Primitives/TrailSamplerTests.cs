using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class TrailSamplerTests
{
    [Fact]
    public void NewSampler_IsEmpty()
    {
        var s = new TrailSampler(maxAgeSeconds: 0.3f, maxCount: 8);
        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.Samples.Length);
        Assert.Equal(0.3f, s.MaxAgeSeconds, 5);
        Assert.Equal(8, s.MaxCount);
    }

    [Fact]
    public void Add_AppendsNewestLast_OldestFirstOrder()
    {
        var s = new TrailSampler(maxAgeSeconds: 10f, maxCount: 8);
        s.Add(new Vector3(0, 0, 0), 0.0f);
        s.Add(new Vector3(1, 0, 0), 0.1f);
        s.Add(new Vector3(2, 0, 0), 0.2f);

        var pts = s.Samples;
        Assert.Equal(3, pts.Length);
        Assert.Equal(0f, pts[0].Position.X, 5);   // oldest first
        Assert.Equal(2f, pts[2].Position.X, 5);   // newest last
        Assert.Equal(0.2f, pts[2].TimeSeconds, 5);
    }

    [Fact]
    public void Add_EvictsSamplesOlderThanMaxAge()
    {
        var s = new TrailSampler(maxAgeSeconds: 0.3f, maxCount: 100);
        s.Add(new Vector3(0, 0, 0), 0.0f);   // will age out at now=0.5 (age 0.5 > 0.3)
        s.Add(new Vector3(1, 0, 0), 0.1f);   // will age out at now=0.5 (age 0.4 > 0.3)
        s.Add(new Vector3(2, 0, 0), 0.3f);   // age 0.2 <= 0.3 : kept
        s.Add(new Vector3(3, 0, 0), 0.5f);   // newest : kept

        var pts = s.Samples;
        Assert.Equal(2, pts.Length);
        Assert.Equal(2f, pts[0].Position.X, 5);
        Assert.Equal(3f, pts[1].Position.X, 5);
    }

    [Fact]
    public void Add_CapsCountAtMaxCount_EvictsOldest()
    {
        var s = new TrailSampler(maxAgeSeconds: 100f, maxCount: 3);
        for (int i = 0; i < 6; i++)
            s.Add(new Vector3(i, 0, 0), i * 0.01f);

        var pts = s.Samples;
        Assert.Equal(3, pts.Length);
        Assert.Equal(3f, pts[0].Position.X, 5);   // oldest surviving is sample #3
        Assert.Equal(5f, pts[2].Position.X, 5);   // newest is #5
    }

    [Fact]
    public void Prune_AgesOutTailWithoutAdding()
    {
        var s = new TrailSampler(maxAgeSeconds: 0.3f, maxCount: 100);
        s.Add(new Vector3(0, 0, 0), 0.0f);
        s.Add(new Vector3(1, 0, 0), 0.1f);
        s.Add(new Vector3(2, 0, 0), 0.2f);

        // No new emission; the emitter idles to t=0.5, so anything older than 0.2 must decay.
        int remaining = s.Prune(0.5f);
        Assert.Equal(1, remaining);
        Assert.Equal(2f, s.Samples[0].Position.X, 5);   // only the newest (age 0.3) survives
    }

    [Fact]
    public void Prune_EmptiesWhenAllAged()
    {
        var s = new TrailSampler(maxAgeSeconds: 0.3f, maxCount: 100);
        s.Add(new Vector3(0, 0, 0), 0.0f);
        s.Add(new Vector3(1, 0, 0), 0.1f);
        Assert.Equal(0, s.Prune(5f));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Clear_EmptiesAll()
    {
        var s = new TrailSampler(maxAgeSeconds: 10f, maxCount: 8);
        s.Add(new Vector3(0, 0, 0), 0.0f);
        s.Add(new Vector3(1, 0, 0), 0.1f);
        s.Clear();
        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.Samples.Length);
    }
}
