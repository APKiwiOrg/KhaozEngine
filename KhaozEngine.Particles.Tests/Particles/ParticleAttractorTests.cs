using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Particles;

public class ParticleAttractorTests
{
    private static EmitterConfig BaseCfg() => new()
    {
        LifetimeMin = 1f,
        LifetimeMax = 1f,
        SpeedMin = 0f,
        SpeedMax = 0f,
        Direction = Vector3.UnitY,
        SpreadDegrees = 0f,
        Gravity = Vector3.Zero,
        Drag = 0f,
        StartSize = 1f,
        EndSize = 1f,
        StartColor = Color.White,
        EndColor = Color.White,
    };

    private static float MeanDistance(ReadOnlySpan<Particle> particles, Vector3 point)
    {
        if (particles.Length == 0)
        {
            return 0f;
        }

        float sum = 0f;
        for (int i = 0; i < particles.Length; i++)
        {
            sum += Vector3.Distance(particles[i].Position, point);
        }
        return sum / particles.Length;
    }

    private static float MeanSpeed(ReadOnlySpan<Particle> particles)
    {
        if (particles.Length == 0)
        {
            return 0f;
        }

        float sum = 0f;
        for (int i = 0; i < particles.Length; i++)
        {
            sum += particles[i].Velocity.Length();
        }
        return sum / particles.Length;
    }

    [Fact]
    public void Attractor_PullsParticlesTowardTarget()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 100f;
        cfg.LifetimeMax = 100f;
        cfg.SpeedMin = 1f;
        cfg.SpeedMax = 3f;
        cfg.Direction = Vector3.Zero; // omni
        cfg.SpreadDegrees = 180f;

        var sys = new ParticleSystem(64, seed: 5);
        sys.Emit(cfg, Vector3.Zero, 32);

        var target = new Vector3(15f, 0f, 0f);
        sys.Attractor = new ParticleAttractor
        {
            Target = target,
            Strength = 30f,
            StrengthCurve = ParticleCurve.One,
        };

        float initialMean = MeanDistance(sys.Active, target);

        for (int i = 0; i < 30; i++)
        {
            sys.Update(1f / 60f);
        }

        float finalMean = MeanDistance(sys.Active, target);
        Assert.True(finalMean < initialMean, $"expected mean distance to decrease: {initialMean} -> {finalMean}");
    }

    [Fact]
    public void Attractor_Null_IsBitIdentical()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 5f;
        cfg.LifetimeMax = 5f;
        cfg.Gravity = new Vector3(0f, -1f, 0f);
        cfg.Drag = 0.2f;
        cfg.SpeedMin = 1f;
        cfg.SpeedMax = 2f;
        cfg.Direction = Vector3.Zero;
        cfg.SpreadDegrees = 180f;

        var a = new ParticleSystem(32, seed: 9);
        var b = new ParticleSystem(32, seed: 9);
        a.Emit(cfg, Vector3.Zero, 16);
        b.Emit(cfg, Vector3.Zero, 16);

        // a never touches Attractor. b sets it to null explicitly. Both must behave identically.
        b.Attractor = null;

        for (int i = 0; i < 20; i++)
        {
            a.Update(1f / 60f);
            b.Update(1f / 60f);
        }

        Assert.Equal(a.ActiveCount, b.ActiveCount);
        for (int i = 0; i < a.ActiveCount; i++)
        {
            Particle pa = a.Active[i];
            Particle pb = b.Active[i];
            Assert.Equal(pa.Position, pb.Position);
            Assert.Equal(pa.Velocity, pb.Velocity);
            Assert.Equal(pa.Age, pb.Age);
            Assert.Equal(pa.Life, pb.Life);
            Assert.Equal(pa.Size, pb.Size);
            Assert.Equal(pa.Color, pb.Color);
            Assert.Equal(pa.Rotation, pb.Rotation);
            Assert.Equal(pa.Seed, pb.Seed);
        }
    }

    [Fact]
    public void Attractor_MovingTarget_IsTrackedPerFrame()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 100f;
        cfg.LifetimeMax = 100f;

        var sys = new ParticleSystem(32, seed: 3);
        sys.Emit(cfg, Vector3.Zero, 16);

        const float dt = 1f / 60f;
        const int steps = 60;
        Vector3 finalTarget = default;

        for (int step = 0; step < steps; step++)
        {
            Vector3 target = new(5f + 0.05f * step, 3f, 0f);
            sys.Attractor = new ParticleAttractor
            {
                Target = target,
                Strength = 15f,
                StrengthCurve = ParticleCurve.One,
            };
            finalTarget = target;
            sys.Update(dt);
        }

        foreach (Particle p in sys.Active)
        {
            Vector3 toFinal = finalTarget - p.Position;
            if (toFinal.LengthSquared() > 1e-6f && p.Velocity.LengthSquared() > 1e-6f)
            {
                float cos = Vector3.Dot(Vector3.Normalize(p.Velocity), Vector3.Normalize(toFinal));
                Assert.True(cos > 0f, $"velocity should point within 90 degrees of the current target direction, cos={cos}");
            }
        }

        float finalMean = MeanDistance(sys.Active, finalTarget);
        float initialDistance = Vector3.Distance(Vector3.Zero, finalTarget);
        Assert.True(finalMean < initialDistance, $"expected mean distance to the final target to decrease: {initialDistance} -> {finalMean}");
    }

    [Fact]
    public void Attractor_Cleared_StopsPull()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 100f;
        cfg.LifetimeMax = 100f;
        cfg.SpeedMin = 1f;
        cfg.SpeedMax = 2f;
        cfg.Direction = Vector3.Zero;
        cfg.SpreadDegrees = 180f;

        var sys = new ParticleSystem(32, seed: 11);
        sys.Emit(cfg, Vector3.Zero, 16);

        sys.Attractor = new ParticleAttractor
        {
            Target = new Vector3(10f, 0f, 0f),
            Strength = 25f,
            StrengthCurve = ParticleCurve.One,
        };

        for (int i = 0; i < 20; i++)
        {
            sys.Update(1f / 60f);
        }

        sys.Attractor = null;

        var before = new Vector3[sys.ActiveCount];
        for (int i = 0; i < sys.ActiveCount; i++)
        {
            before[i] = sys.Active[i].Velocity;
        }

        sys.Update(1f / 60f); // zero gravity/drag/turbulence in cfg: nothing should change velocity now

        for (int i = 0; i < sys.ActiveCount; i++)
        {
            Assert.Equal(before[i], sys.Active[i].Velocity);
        }
    }

    [Fact]
    public void StrengthCurve_EaseIn_DriftsBeforePulling()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 2f;
        cfg.LifetimeMax = 2f;

        var target = new Vector3(10f, 0f, 0f);
        const float dt = 1f / 60f;

        var one = new ParticleSystem(32, seed: 21);
        var easeIn = new ParticleSystem(32, seed: 21);
        one.Emit(cfg, Vector3.Zero, 16);
        easeIn.Emit(cfg, Vector3.Zero, 16);

        one.Attractor = new ParticleAttractor { Target = target, Strength = 20f, StrengthCurve = ParticleCurve.One };
        easeIn.Attractor = new ParticleAttractor { Target = target, Strength = 20f, StrengthCurve = ParticleCurve.EaseIn };

        for (int i = 0; i < 5; i++)
        {
            one.Update(dt);
            easeIn.Update(dt);
        }

        float oneSpeed = MeanSpeed(one.Active);
        float easeInSpeed = MeanSpeed(easeIn.Active);
        Assert.True(easeInSpeed < oneSpeed, $"EaseIn should still be drifting slower after 5 early steps: one={oneSpeed} easeIn={easeInSpeed}");

        // Run out the rest of the lifetime (stop just short of death so particles stay live to inspect).
        const int totalSteps = 117; // 117/60 s ~= 1.95 s of a 2 s life, norm ~0.975
        for (int i = 5; i < totalSteps; i++)
        {
            one.Update(dt);
            easeIn.Update(dt);
        }

        float easeInFinalMean = MeanDistance(easeIn.Active, target);
        float initialDistance = Vector3.Distance(Vector3.Zero, target);
        Assert.True(easeInFinalMean < initialDistance * 0.5f,
            $"EaseIn should have closed most of the distance by lifetime end: {easeInFinalMean} vs initial {initialDistance}");
    }

    [Fact]
    public void KillRadius_AbsorbsAndSignals()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 1000f;
        cfg.LifetimeMax = 1000f;
        cfg.SpeedMin = 0.5f;
        cfg.SpeedMax = 1.5f;
        cfg.Direction = Vector3.Zero;
        cfg.SpreadDegrees = 180f;

        const int emitCount = 20;
        var sys = new ParticleSystem(32, seed: 33);
        sys.Emit(cfg, new Vector3(3f, 0f, 0f), emitCount);

        var target = Vector3.Zero;
        const float killRadius = 0.5f;
        sys.Attractor = new ParticleAttractor
        {
            Target = target,
            Strength = 50f,
            StrengthCurve = ParticleCurve.One,
            KillRadius = killRadius,
        };

        var absorbedPositions = new List<Vector3>();
        sys.OnAbsorbed = p => absorbedPositions.Add(p.Position);

        int absorbedSum = 0;
        int step = 0;
        const int maxSteps = 2000; // well short of the 1000 s lifetime
        while (sys.ActiveCount > 0 && step < maxSteps)
        {
            sys.Update(1f / 60f);
            absorbedSum += sys.AbsorbedLastUpdate;
            step++;
        }

        Assert.Equal(0, sys.ActiveCount);
        Assert.True(step < maxSteps, "should absorb well before the bounded loop runs out (and well before lifetime expiry)");
        Assert.Equal(emitCount, sys.AbsorbedTotal);
        Assert.Equal(emitCount, absorbedSum);
        Assert.Equal(emitCount, absorbedPositions.Count);
        foreach (Vector3 pos in absorbedPositions)
        {
            Assert.True(Vector3.Distance(pos, target) <= killRadius + 1e-3f, $"absorbed position {pos} too far from target");
        }
    }

    [Fact]
    public void KillRadius_Zero_NeverAbsorbs()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 5f;
        cfg.LifetimeMax = 5f;
        cfg.SpeedMin = 0.5f;
        cfg.SpeedMax = 1.5f;
        cfg.Direction = Vector3.Zero;
        cfg.SpreadDegrees = 180f;

        var sys = new ParticleSystem(32, seed: 33);
        sys.Emit(cfg, new Vector3(3f, 0f, 0f), 20);

        sys.Attractor = new ParticleAttractor
        {
            Target = Vector3.Zero,
            Strength = 50f,
            StrengthCurve = ParticleCurve.One,
            KillRadius = 0f,
        };

        for (int i = 0; i < 300; i++)
        {
            sys.Update(1f / 60f);
        }

        Assert.Equal(0, sys.AbsorbedTotal);
    }

    [Fact]
    public void IgnoreAttractor_ConfigOptsOut()
    {
        var attractedCfg = BaseCfg();
        attractedCfg.LifetimeMin = 1000f;
        attractedCfg.LifetimeMax = 1000f;
        attractedCfg.SpeedMin = 1f;
        attractedCfg.SpeedMax = 1f;
        attractedCfg.Direction = Vector3.UnitY;
        attractedCfg.SpreadDegrees = 0f;

        var ignoringCfg = attractedCfg;
        ignoringCfg.IgnoreAttractor = true;

        const int countEach = 8;
        var target = new Vector3(5f, 0f, 0f);

        // Phase 1 (no absorption): the opted-out group must match a no-attractor twin exactly, and the
        // attracted group must converge.
        var sys = new ParticleSystem(64, seed: 44);
        sys.Emit(attractedCfg, Vector3.Zero, countEach);   // indices 0..countEach-1
        sys.Emit(ignoringCfg, Vector3.Zero, countEach);    // indices countEach..2*countEach-1

        var twin = new ParticleSystem(64, seed: 44);
        twin.Emit(attractedCfg, Vector3.Zero, countEach);
        twin.Emit(ignoringCfg, Vector3.Zero, countEach);   // twin.Attractor is never assigned

        sys.Attractor = new ParticleAttractor
        {
            Target = target,
            Strength = 40f,
            StrengthCurve = ParticleCurve.One,
        };

        float initialAttractedMean = MeanDistance(sys.Active.Slice(0, countEach), target);

        for (int i = 0; i < 30; i++)
        {
            sys.Update(1f / 60f);
            twin.Update(1f / 60f);
        }

        for (int i = countEach; i < countEach * 2; i++)
        {
            Assert.Equal(twin.Active[i].Position, sys.Active[i].Position);
            Assert.Equal(twin.Active[i].Velocity, sys.Active[i].Velocity);
        }

        float finalAttractedMean = MeanDistance(sys.Active.Slice(0, countEach), target);
        Assert.True(finalAttractedMean < initialAttractedMean, "the attracted group should converge toward the target");

        // Phase 2 (absorption enabled): the ignoring group drifts along +Y, forever moving away from the
        // target's plane, so it can never enter the kill radius. Only the attracted group should absorb.
        var abs = new ParticleSystem(64, seed: 55);
        abs.Emit(attractedCfg, Vector3.Zero, countEach);
        abs.Emit(ignoringCfg, Vector3.Zero, countEach);
        abs.Attractor = new ParticleAttractor
        {
            Target = target,
            Strength = 60f,
            StrengthCurve = ParticleCurve.One,
            KillRadius = 1f,
        };

        for (int i = 0; i < 600; i++)
        {
            abs.Update(1f / 60f);
        }

        Assert.Equal(countEach, abs.AbsorbedTotal);
        Assert.Equal(countEach, abs.ActiveCount); // only the ignoring group remains
    }

    [Fact]
    public void MaxSpeed_ClampsSpeed()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 100f;
        cfg.LifetimeMax = 100f;
        cfg.SpeedMin = 0.5f;
        cfg.SpeedMax = 3f;
        cfg.Direction = Vector3.Zero;
        cfg.SpreadDegrees = 180f;

        var sys = new ParticleSystem(64, seed: 66);
        sys.Emit(cfg, Vector3.Zero, 32);

        const float maxSpeed = 2f;
        sys.Attractor = new ParticleAttractor
        {
            Target = new Vector3(50f, 0f, 0f),
            Strength = 200f,
            StrengthCurve = ParticleCurve.One,
            MaxSpeed = maxSpeed,
        };

        for (int i = 0; i < 60; i++)
        {
            sys.Update(1f / 60f);
            foreach (Particle p in sys.Active)
            {
                Assert.True(p.Velocity.Length() <= maxSpeed + 1e-4f, $"speed {p.Velocity.Length()} exceeds the cap");
            }
        }
    }

    [Fact]
    public void Attractor_Determinism_SameSeedSameCalls()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 5f;
        cfg.LifetimeMax = 8f;
        cfg.Gravity = new Vector3(0f, -1f, 0f);
        cfg.Drag = 0.1f;
        cfg.SpeedMin = 0.5f;
        cfg.SpeedMax = 2f;
        cfg.Direction = Vector3.Zero;
        cfg.SpreadDegrees = 180f;

        ParticleSystem Run()
        {
            var sys = new ParticleSystem(64, seed: 77);
            sys.Emit(cfg, Vector3.Zero, 32);
            sys.Attractor = new ParticleAttractor
            {
                Target = new Vector3(4f, 0f, 0f),
                Strength = 30f,
                StrengthCurve = ParticleCurve.One,
                KillRadius = 0.75f,
            };
            for (int i = 0; i < 300; i++)
            {
                sys.Update(1f / 60f);
            }
            return sys;
        }

        var a = Run();
        var b = Run();

        Assert.Equal(a.AbsorbedTotal, b.AbsorbedTotal);
        Assert.Equal(a.ActiveCount, b.ActiveCount);
        for (int i = 0; i < a.ActiveCount; i++)
        {
            Assert.Equal(a.Active[i].Position, b.Active[i].Position);
            Assert.Equal(a.Active[i].Velocity, b.Active[i].Velocity);
            Assert.Equal(a.Active[i].Age, b.Active[i].Age);
        }
    }
}
