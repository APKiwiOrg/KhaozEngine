using System;
using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Particles;

public class ParticleTrailTests
{
    private static EmitterConfig StraightCfg(Vector3 dir, float speed, float life) => new()
    {
        LifetimeMin = life,
        LifetimeMax = life,
        SpeedMin = speed,
        SpeedMax = speed,
        Direction = dir,
        SpreadDegrees = 0f,
        Gravity = Vector3.Zero,
        Drag = 0f,
        StartSize = 1f,
        EndSize = 1f,
        StartColor = Color.White,
        EndColor = Color.White,
    };

    [Fact]
    public void Disabled_ByDefault_GetTrailReturnsZero()
    {
        var sys = new ParticleSystem(16, seed: 1);
        Assert.Equal(0, sys.TrailCapacity);
        sys.Emit(StraightCfg(Vector3.UnitX, 5f, 10f), Vector3.Zero, 1);

        Span<ParticleTrailPoint> buf = stackalloc ParticleTrailPoint[8];
        Assert.Equal(0, sys.GetTrail(0, buf));
    }

    [Fact]
    public void SeedsWithSpawnPoint_AtEmit()
    {
        var origin = new Vector3(2f, 3f, 4f);
        var sys = new ParticleSystem(16, seed: 1, trailSamples: 8);
        sys.Emit(StraightCfg(Vector3.UnitX, 5f, 10f), origin, 1);

        Span<ParticleTrailPoint> buf = stackalloc ParticleTrailPoint[8];
        int count = sys.GetTrail(0, buf);
        Assert.Equal(1, count);
        Assert.Equal(origin, buf[0].Position);
        Assert.Equal(0f, buf[0].Age);
    }

    [Fact]
    public void CaptureCadence_GrowsOnePerInterval()
    {
        var sys = new ParticleSystem(16, seed: 1, trailSamples: 8) { TrailSampleInterval = 0.1f };
        sys.Emit(StraightCfg(Vector3.UnitX, 5f, 100f), Vector3.Zero, 1);

        Span<ParticleTrailPoint> buf = stackalloc ParticleTrailPoint[16];
        Assert.Equal(1, sys.GetTrail(0, buf)); // seed only

        for (int k = 1; k <= 6; k++)
        {
            sys.Update(0.1f);
            int expected = Math.Min(1 + k, 8); // seed + k captures, capped at capacity
            Assert.Equal(expected, sys.GetTrail(0, buf));
        }
    }

    [Fact]
    public void RingWrap_CapsAtCapacity_KeepsNewestInOrder()
    {
        var sys = new ParticleSystem(16, seed: 1, trailSamples: 4) { TrailSampleInterval = 0.05f };
        sys.Emit(StraightCfg(Vector3.UnitX, 10f, 100f), Vector3.Zero, 1);

        for (int i = 0; i < 12; i++)
        {
            sys.Update(0.05f); // 12 captures into a 4-slot ring
        }

        Span<ParticleTrailPoint> buf = stackalloc ParticleTrailPoint[4];
        int count = sys.GetTrail(0, buf);
        Assert.Equal(4, count);

        // Oldest-to-newest: ages strictly increase, positions advance along +X (motion direction).
        for (int j = 1; j < count; j++)
        {
            Assert.True(buf[j].Age > buf[j - 1].Age, "ages should increase oldest-to-newest");
            Assert.True(buf[j].Position.X > buf[j - 1].Position.X, "positions should advance along motion");
        }
    }

    [Fact]
    public void GetTrail_OrdersOldestToNewest()
    {
        var sys = new ParticleSystem(16, seed: 1, trailSamples: 16) { TrailSampleInterval = 0.05f };
        sys.Emit(StraightCfg(Vector3.UnitY, 8f, 100f), Vector3.Zero, 1);

        for (int i = 0; i < 5; i++)
        {
            sys.Update(0.05f);
        }

        Span<ParticleTrailPoint> buf = stackalloc ParticleTrailPoint[16];
        int count = sys.GetTrail(0, buf);
        Assert.Equal(6, count); // seed + 5

        Assert.Equal(0f, buf[0].Age); // oldest is the spawn seed
        for (int j = 1; j < count; j++)
        {
            Assert.True(buf[j].Age >= buf[j - 1].Age);
            Assert.True(buf[j].Position.Y >= buf[j - 1].Position.Y);
        }
    }

    [Fact]
    public void SwapRemove_KeepsSurvivorTrailIntact()
    {
        // A short particle (index 0, moving +X) and a long one (index 1, moving +Y). When the short one dies
        // the long one swaps into slot 0; its trail block must travel with it undamaged.
        var sys = new ParticleSystem(16, seed: 1, trailSamples: 16) { TrailSampleInterval = 0.05f };
        sys.Emit(StraightCfg(Vector3.UnitX, 10f, 0.2f), Vector3.Zero, 1);  // short, +X
        sys.Emit(StraightCfg(Vector3.UnitY, 10f, 100f), Vector3.Zero, 1);  // long, +Y

        Assert.Equal(2, sys.ActiveCount);

        for (int i = 0; i < 6; i++)
        {
            sys.Update(0.05f); // ~0.30s: the +X particle (life 0.2) dies and is swap-removed
        }

        Assert.Equal(1, sys.ActiveCount);
        var survivor = sys.Active[0];
        Assert.True(survivor.Velocity.Y > 0f && MathF.Abs(survivor.Velocity.X) < 1e-4f, "survivor should be the +Y particle");

        Span<ParticleTrailPoint> buf = stackalloc ParticleTrailPoint[16];
        int count = sys.GetTrail(0, buf);
        Assert.True(count >= 2);
        for (int j = 0; j < count; j++)
        {
            // The +Y particle's whole history stays on the Y axis: no X/Z leakage from the removed neighbour.
            Assert.True(MathF.Abs(buf[j].Position.X) < 1e-4f, $"survivor trail leaked X at {j}: {buf[j].Position}");
            Assert.True(MathF.Abs(buf[j].Position.Z) < 1e-4f);
        }
        for (int j = 1; j < count; j++)
        {
            Assert.True(buf[j].Position.Y >= buf[j - 1].Position.Y);
        }
    }

    [Fact]
    public void Trails_Deterministic_SameSeed()
    {
        var a = new ParticleSystem(16, seed: 5, trailSamples: 8) { TrailSampleInterval = 0.05f };
        var b = new ParticleSystem(16, seed: 5, trailSamples: 8) { TrailSampleInterval = 0.05f };
        var cfg = StraightCfg(Vector3.Zero, 5f, 100f);
        cfg.SpreadDegrees = 180f;

        a.Emit(cfg, Vector3.Zero, 4);
        b.Emit(cfg, Vector3.Zero, 4);
        for (int i = 0; i < 8; i++)
        {
            a.Update(0.05f);
            b.Update(0.05f);
        }

        Span<ParticleTrailPoint> ba = stackalloc ParticleTrailPoint[8];
        Span<ParticleTrailPoint> bb = stackalloc ParticleTrailPoint[8];
        for (int i = 0; i < a.ActiveCount; i++)
        {
            int ca = a.GetTrail(i, ba);
            int cb = b.GetTrail(i, bb);
            Assert.Equal(ca, cb);
            for (int j = 0; j < ca; j++)
            {
                Assert.Equal(ba[j], bb[j]);
            }
        }
    }
}
