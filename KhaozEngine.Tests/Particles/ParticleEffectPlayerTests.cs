using System;
using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Particles;

public class ParticleEffectPlayerTests
{
    private static EmitterConfig Emitter(Vector3 dir, float speed = 5f, float life = 100f) => new()
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
    public void Delay_IsHonoredBeforeTheBurst()
    {
        var effect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY),
            Delay = 0.5f,
            Duration = 0f,
            BurstCount = 10,
        });
        var player = new ParticleEffectPlayer(effect, maxInstances: 4, seed: 1);

        Assert.True(player.Play(Vector3.Zero, Vector3.UnitY));

        player.Update(0.3f); // age 0.3 < delay
        Assert.Equal(0, player.PhaseSystem(0).ActiveCount);

        player.Update(0.3f); // age 0.6 >= delay -> burst
        Assert.Equal(10, player.PhaseSystem(0).ActiveCount);
    }

    [Fact]
    public void Burst_FiresExactlyOnce()
    {
        var effect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY),
            Delay = 0f,
            Duration = 0f,
            BurstCount = 5,
        });
        var player = new ParticleEffectPlayer(effect, maxInstances: 4, seed: 1);
        player.Play(Vector3.Zero, Vector3.UnitY);

        for (int i = 0; i < 8; i++)
        {
            player.Update(0.1f);
        }

        Assert.Equal(5, player.PhaseSystem(0).ActiveCount); // never re-fires
    }

    [Fact]
    public void Rate_AccumulatesOverTheStream()
    {
        var effect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY),
            Delay = 0f,
            Duration = 2f,
            RatePerSecond = 10f,
        });
        var player = new ParticleEffectPlayer(effect, maxInstances: 4, seed: 1);
        player.Play(Vector3.Zero, Vector3.UnitY);

        for (int i = 0; i < 10; i++)
        {
            player.Update(0.1f); // 10 * 0.1s at 10/s = 10 particles
        }

        Assert.Equal(10, player.PhaseSystem(0).ActiveCount);
    }

    [Fact]
    public void Stream_StopsAfterDuration()
    {
        var effect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY),
            Delay = 0f,
            Duration = 0.55f,
            RatePerSecond = 100f,
        });
        var player = new ParticleEffectPlayer(effect, maxInstances: 4, seed: 1);
        player.Play(Vector3.Zero, Vector3.UnitY);

        for (int i = 0; i < 5; i++)
        {
            player.Update(0.1f); // through the window (age ~0.5)
        }
        int duringWindow = player.PhaseSystem(0).ActiveCount;
        Assert.True(duringWindow > 0);

        for (int i = 0; i < 5; i++)
        {
            player.Update(0.1f); // past the window (age ~1.0)
        }
        int afterWindow = player.PhaseSystem(0).ActiveCount;

        Assert.Equal(duringWindow, afterWindow); // no more emission once Duration elapsed
    }

    [Fact]
    public void InstanceCap_ReturnsFalseWhenFull()
    {
        var effect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY),
            Delay = 1f,
            Duration = 1f,
            RatePerSecond = 5f,
        });
        var player = new ParticleEffectPlayer(effect, maxInstances: 2, seed: 1);

        Assert.True(player.Play(Vector3.Zero, Vector3.UnitY));
        Assert.True(player.Play(Vector3.Zero, Vector3.UnitY));
        Assert.False(player.Play(Vector3.Zero, Vector3.UnitY)); // both slots busy
    }

    [Fact]
    public void Clear_StopsEverything()
    {
        var effect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY),
            Delay = 0f,
            Duration = 1f,
            RatePerSecond = 20f,
            BurstCount = 4,
        });
        var player = new ParticleEffectPlayer(effect, maxInstances: 4, seed: 1);
        player.Play(Vector3.Zero, Vector3.UnitY);
        player.Update(0.2f);
        Assert.True(player.AnyAlive);

        player.Clear();
        Assert.False(player.AnyAlive);
        Assert.Equal(0, player.PhaseSystem(0).ActiveCount);
    }

    [Fact]
    public void TwoPlayers_SameSeed_AreDeterministic()
    {
        ParticleEffect Build() => new(
            new ParticleEffectPhase
            {
                Config = Emitter(Vector3.Zero) with { SpreadDegrees = 180f, SpeedMin = 3f, SpeedMax = 6f },
                Delay = 0f,
                Duration = 1f,
                RatePerSecond = 15f,
                BurstCount = 6,
            },
            new ParticleEffectPhase
            {
                Config = Emitter(Vector3.UnitY) with { SpeedMin = 2f, SpeedMax = 4f, SpreadDegrees = 30f },
                Delay = 0.1f,
                Duration = 0.5f,
                RatePerSecond = 8f,
            });

        var a = new ParticleEffectPlayer(Build(), maxInstances: 4, seed: 42);
        var b = new ParticleEffectPlayer(Build(), maxInstances: 4, seed: 42);

        var dir = Vector3.Normalize(new Vector3(1f, 1f, 0f));
        a.Play(new Vector3(1f, 2f, 3f), dir);
        b.Play(new Vector3(1f, 2f, 3f), dir);
        for (int i = 0; i < 15; i++)
        {
            a.Update(0.05f);
            b.Update(0.05f);
        }

        Assert.Equal(a.PhaseCount, b.PhaseCount);
        for (int ph = 0; ph < a.PhaseCount; ph++)
        {
            var pa = a.PhaseSystem(ph);
            var pb = b.PhaseSystem(ph);
            Assert.Equal(pa.ActiveCount, pb.ActiveCount);
            for (int i = 0; i < pa.ActiveCount; i++)
            {
                Assert.Equal(pa.Active[i].Position, pb.Active[i].Position);
                Assert.Equal(pa.Active[i].Velocity, pb.Active[i].Velocity);
            }
        }
    }

    [Fact]
    public void PlayDirection_RotatesTheEmitterAxis()
    {
        // The phase emits along +Y in config space; playing toward +X must rotate the burst onto +X.
        var effect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY, speed: 5f),
            Delay = 0f,
            Duration = 0f,
            BurstCount = 8,
        });
        var player = new ParticleEffectPlayer(effect, maxInstances: 2, seed: 1);
        player.Play(Vector3.Zero, Vector3.UnitX);
        player.Update(0.05f); // fires the burst

        var pool = player.PhaseSystem(0);
        Assert.Equal(8, pool.ActiveCount);
        foreach (var p in pool.Active)
        {
            Vector3 velDir = Vector3.Normalize(p.Velocity);
            Assert.True((velDir - Vector3.UnitX).Length() < 1e-4f, $"expected +X velocity, got {velDir}");
        }
    }

    [Fact]
    public void PlayDirection_Identity_WhenAlignedWithConfig()
    {
        var effect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY, speed: 5f),
            Delay = 0f,
            Duration = 0f,
            BurstCount = 4,
        });
        var player = new ParticleEffectPlayer(effect, maxInstances: 2, seed: 1);
        player.Play(Vector3.Zero, Vector3.UnitY);
        player.Update(0.05f);

        foreach (var p in player.PhaseSystem(0).Active)
        {
            Vector3 velDir = Vector3.Normalize(p.Velocity);
            Assert.True((velDir - Vector3.UnitY).Length() < 1e-4f, $"expected +Y velocity, got {velDir}");
        }
    }

    [Fact]
    public void AnyAlive_TracksPoolDrain_AfterInstanceFrees()
    {
        var cfg = Emitter(Vector3.UnitY, speed: 1f, life: 0.2f);
        var effect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = cfg,
            Delay = 0f,
            Duration = 0f,
            BurstCount = 3,
        });
        var player = new ParticleEffectPlayer(effect, maxInstances: 2, seed: 1);
        player.Play(Vector3.Zero, Vector3.UnitY);

        player.Update(0.05f); // burst fires, particles alive
        Assert.True(player.AnyAlive);

        for (int i = 0; i < 10; i++)
        {
            player.Update(0.05f); // instance frees, then the short-lived particles drain
        }
        Assert.False(player.AnyAlive);
    }
}
