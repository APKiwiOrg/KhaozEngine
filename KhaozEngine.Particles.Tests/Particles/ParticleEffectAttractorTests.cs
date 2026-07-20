using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Particles;

public class ParticleEffectAttractorTests
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

    private static ParticleEffect TwoPhaseEffect(float rate = 20f, float duration = 1f)
    {
        return new ParticleEffect(
            new ParticleEffectPhase
            {
                Config = Emitter(Vector3.UnitY, speed: 1f),
                Delay = 0f,
                Duration = duration,
                RatePerSecond = rate,
            },
            new ParticleEffectPhase
            {
                Config = Emitter(Vector3.UnitX, speed: 1f),
                Delay = 0f,
                Duration = duration,
                RatePerSecond = rate,
            });
    }

    [Fact]
    public void Player_Attractor_ThreadsToEveryPhasePool()
    {
        var effect = TwoPhaseEffect();
        var player = new ParticleEffectPlayer(effect, maxInstances: 2, seed: 1);

        var attractor = new ParticleAttractor
        {
            Target = new Vector3(3f, 0f, 0f),
            Strength = 10f,
            StrengthCurve = ParticleCurve.One,
        };

        player.Attractor = attractor;
        Assert.NotNull(player.PhaseSystem(0).Attractor);
        Assert.NotNull(player.PhaseSystem(1).Attractor);
        Assert.Equal(attractor.Target, player.PhaseSystem(0).Attractor!.Value.Target);
        Assert.Equal(attractor.Target, player.PhaseSystem(1).Attractor!.Value.Target);

        player.Attractor = null;
        Assert.Null(player.PhaseSystem(0).Attractor);
        Assert.Null(player.PhaseSystem(1).Attractor);
    }

    [Fact]
    public void Player_AbsorbCounts_AggregateAcrossPhases()
    {
        var effect = TwoPhaseEffect(rate: 30f, duration: 0.5f);
        var player = new ParticleEffectPlayer(effect, maxInstances: 2, seed: 1);
        player.Play(new Vector3(2f, 0f, 0f), Vector3.UnitX);

        player.Attractor = new ParticleAttractor
        {
            Target = Vector3.Zero,
            Strength = 80f,
            StrengthCurve = ParticleCurve.One,
            KillRadius = 5f, // large enough to absorb on arrival for both phases
        };

        for (int i = 0; i < 120; i++)
        {
            player.Update(1f / 60f);
        }

        Assert.True(player.PhaseSystem(0).AbsorbedTotal > 0);
        Assert.True(player.PhaseSystem(1).AbsorbedTotal > 0);
        Assert.Equal(player.PhaseSystem(0).AbsorbedTotal + player.PhaseSystem(1).AbsorbedTotal, player.AbsorbedTotal);
        Assert.Equal(player.PhaseSystem(0).AbsorbedLastUpdate + player.PhaseSystem(1).AbsorbedLastUpdate, player.AbsorbedLastUpdate);
    }

    [Fact]
    public void Player_OnAbsorbed_ForwardsToPools()
    {
        var effect = TwoPhaseEffect(rate: 30f, duration: 0.5f);
        var player = new ParticleEffectPlayer(effect, maxInstances: 2, seed: 1);
        player.Play(new Vector3(2f, 0f, 0f), Vector3.UnitX);

        int absorbedCount = 0;
        player.OnAbsorbed = _ => absorbedCount++;

        player.Attractor = new ParticleAttractor
        {
            Target = Vector3.Zero,
            Strength = 80f,
            StrengthCurve = ParticleCurve.One,
            KillRadius = 5f,
        };

        for (int i = 0; i < 120; i++)
        {
            player.Update(1f / 60f);
        }

        Assert.True(absorbedCount > 0);
        Assert.Equal(player.AbsorbedTotal, absorbedCount);
    }

    [Fact]
    public void RateScale_ScalesStreamEmission()
    {
        var buildEffect = () => new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY, speed: 0f),
            Delay = 0f,
            Duration = 1f,
            RatePerSecond = 40f,
        });

        var full = new ParticleEffectPlayer(buildEffect(), maxInstances: 2, seed: 1);
        var quarter = new ParticleEffectPlayer(buildEffect(), maxInstances: 2, seed: 1);
        quarter.RateScale = 0.25f;

        full.Play(Vector3.Zero, Vector3.UnitY);
        quarter.Play(Vector3.Zero, Vector3.UnitY);

        for (int i = 0; i < 70; i++) // past the 1s window
        {
            full.Update(1f / 60f);
            quarter.Update(1f / 60f);
        }

        int fullCount = full.PhaseSystem(0).ActiveCount;
        int quarterCount = quarter.PhaseSystem(0).ActiveCount;
        Assert.True(fullCount > 0);

        float ratio = (float)quarterCount / fullCount;
        Assert.True(System.Math.Abs(ratio - 0.25f) <= 0.15f, $"expected ~0.25 ratio, got {ratio} ({quarterCount}/{fullCount})");

        var zero = new ParticleEffectPlayer(buildEffect(), maxInstances: 2, seed: 1) { RateScale = 0f };
        zero.Play(Vector3.Zero, Vector3.UnitY);
        for (int i = 0; i < 70; i++)
        {
            zero.Update(1f / 60f);
        }
        Assert.Equal(0, zero.PhaseSystem(0).ActiveCount);

        // Bursts still fire at any scale, including 0.
        var burstEffect = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY, speed: 0f),
            Delay = 0f,
            Duration = 0f,
            BurstCount = 6,
        });
        var burstPlayer = new ParticleEffectPlayer(burstEffect, maxInstances: 2, seed: 1) { RateScale = 0f };
        burstPlayer.Play(Vector3.Zero, Vector3.UnitY);
        burstPlayer.Update(0.05f);
        Assert.Equal(6, burstPlayer.PhaseSystem(0).ActiveCount);
    }

    [Fact]
    public void RateCurve_EaseIn_BackloadsEmission()
    {
        var curved = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY, speed: 0f, life: 1000f),
            Delay = 0f,
            Duration = 1f,
            RatePerSecond = 40f,
            RateCurve = ParticleCurve.EaseIn,
        });
        var flat = new ParticleEffect(new ParticleEffectPhase
        {
            Config = Emitter(Vector3.UnitY, speed: 0f, life: 1000f),
            Delay = 0f,
            Duration = 1f,
            RatePerSecond = 40f,
        });

        var curvedPlayer = new ParticleEffectPlayer(curved, maxInstances: 2, seed: 1);
        var flatPlayer = new ParticleEffectPlayer(flat, maxInstances: 2, seed: 1);
        curvedPlayer.Play(Vector3.Zero, Vector3.UnitY);
        flatPlayer.Play(Vector3.Zero, Vector3.UnitY);

        const float dt = 1f / 60f;
        const int halfSteps = 30; // 0.5s

        int curvedBefore = curvedPlayer.PhaseSystem(0).ActiveCount;
        int flatBefore = flatPlayer.PhaseSystem(0).ActiveCount;

        for (int i = 0; i < halfSteps; i++)
        {
            curvedPlayer.Update(dt);
            flatPlayer.Update(dt);
        }
        int curvedFirstHalf = curvedPlayer.PhaseSystem(0).ActiveCount - curvedBefore;
        int flatFirstHalf = flatPlayer.PhaseSystem(0).ActiveCount - flatBefore;

        for (int i = 0; i < halfSteps; i++)
        {
            curvedPlayer.Update(dt);
            flatPlayer.Update(dt);
        }
        int curvedSecondHalf = curvedPlayer.PhaseSystem(0).ActiveCount - (curvedBefore + curvedFirstHalf);
        int flatSecondHalf = flatPlayer.PhaseSystem(0).ActiveCount - (flatBefore + flatFirstHalf);

        Assert.True(curvedSecondHalf > curvedFirstHalf,
            $"EaseIn should backload emission: first={curvedFirstHalf} second={curvedSecondHalf}");
        Assert.True(System.Math.Abs(flatSecondHalf - flatFirstHalf) <= 1,
            $"null curve should stay flat: first={flatFirstHalf} second={flatSecondHalf}");
    }

    [Fact]
    public void RateDefaults_AreBitIdenticalToLegacy()
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

        var legacy = new ParticleEffectPlayer(Build(), maxInstances: 4, seed: 42);
        var explicitDefault = new ParticleEffectPlayer(Build(), maxInstances: 4, seed: 42) { RateScale = 1f };

        var dir = Vector3.Normalize(new Vector3(1f, 1f, 0f));
        legacy.Play(new Vector3(1f, 2f, 3f), dir);
        explicitDefault.Play(new Vector3(1f, 2f, 3f), dir);

        for (int i = 0; i < 15; i++)
        {
            legacy.Update(0.05f);
            explicitDefault.Update(0.05f);
        }

        Assert.Equal(legacy.PhaseCount, explicitDefault.PhaseCount);
        for (int ph = 0; ph < legacy.PhaseCount; ph++)
        {
            var pa = legacy.PhaseSystem(ph);
            var pb = explicitDefault.PhaseSystem(ph);
            Assert.Equal(pa.ActiveCount, pb.ActiveCount);
            for (int i = 0; i < pa.ActiveCount; i++)
            {
                Assert.Equal(pa.Active[i].Position, pb.Active[i].Position);
                Assert.Equal(pa.Active[i].Velocity, pb.Active[i].Velocity);
            }
        }
    }
}
