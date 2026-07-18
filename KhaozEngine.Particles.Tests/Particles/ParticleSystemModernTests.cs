using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Particles;

public class ParticleSystemModernTests
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
        StartSize = 2f,
        EndSize = 2f,
        StartColor = Color.White,
        EndColor = Color.White,
    };

    [Fact]
    public void SizeVariance_KeepsSizesWithinRange_AndVaried()
    {
        var cfg = BaseCfg();
        cfg.SizeVariance = 0.5f; // multiplier in [0.5, 1.5] over StartSize 2 => [1, 3]

        var sys = new ParticleSystem(256, seed: 4);
        sys.Emit(cfg, Vector3.Zero, 200);
        sys.Update(0.0001f); // norm ~ 0 => size ~ baked start size

        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in sys.Active)
        {
            Assert.True(p.Size >= 1f - 1e-3f && p.Size <= 3f + 1e-3f, $"size {p.Size} out of range");
            min = MathF.Min(min, p.Size);
            max = MathF.Max(max, p.Size);
        }

        Assert.True(max - min > 0.3f, "size variance should produce a spread of sizes");
    }

    [Fact]
    public void VaryColor_BlendsBetweenTheTwoStartColors()
    {
        var cfg = BaseCfg();
        cfg.VaryColor = true;
        cfg.StartColor = new Color(1f, 0f, 0f, 1f);   // red
        cfg.StartColorB = new Color(0f, 1f, 0f, 1f);  // green
        cfg.EndColor = new Color(1f, 0f, 0f, 0f);
        cfg.EndColorB = new Color(0f, 1f, 0f, 0f);

        var sys = new ParticleSystem(256, seed: 8);
        sys.Emit(cfg, Vector3.Zero, 200);
        sys.Update(0.0001f); // norm ~ 0 => ~ baked start colour

        var reds = new HashSet<float>();
        foreach (var p in sys.Active)
        {
            // Lerp(red, green, t) = (1-t, t, 0): red + green ~ 1, blue ~ 0.
            Assert.Equal(1f, p.Color.R + p.Color.G, 3);
            Assert.True(MathF.Abs(p.Color.B) < 1e-3f);
            reds.Add(MathF.Round(p.Color.R, 3));
        }

        Assert.True(reds.Count > 5, "random-between-two-gradients should vary the blend per particle");
    }

    [Fact]
    public void MidColor_IsReachedAtNormHalf()
    {
        var cfg = BaseCfg();
        cfg.UseMidColor = true;
        cfg.StartColor = new Color(1f, 0f, 0f, 1f); // red
        cfg.MidColor = new Color(0f, 0f, 1f, 1f);   // blue at 0.5
        cfg.EndColor = new Color(0f, 1f, 0f, 1f);   // green

        var sys = new ParticleSystem(16, seed: 1);
        sys.Emit(cfg, Vector3.Zero, 1);
        sys.Update(0.5f); // norm = 0.5

        var p = sys.Active[0];
        Assert.True(p.Color.B > 0.99f, $"expected blue mid, got {p.Color}");
        Assert.True(p.Color.R < 0.01f);
        Assert.True(p.Color.G < 0.01f);
    }

    [Fact]
    public void SizeCurve_And_AlphaCurve_DriveInterpolation()
    {
        var cfg = BaseCfg();
        cfg.StartSize = 0f;
        cfg.EndSize = 10f;
        cfg.SizeCurve = ParticleCurve.EaseIn;   // Evaluate(0.5) = 0.25
        cfg.StartColor = new Color(1f, 1f, 1f, 1f);
        cfg.EndColor = new Color(1f, 1f, 1f, 0f);
        cfg.AlphaCurve = ParticleCurve.EaseOut; // Evaluate(0.5) = 0.75

        var sys = new ParticleSystem(16, seed: 1);
        sys.Emit(cfg, Vector3.Zero, 1);
        sys.Update(0.5f); // norm = 0.5

        var p = sys.Active[0];
        Assert.Equal(2.5f, p.Size, 3);         // Lerp(0,10, 0.25)
        Assert.Equal(0.25f, p.Color.A, 3);     // Lerp(1,0, 0.75)
    }

    [Fact]
    public void Spin_IntegratesRotationOverTime()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 10f;
        cfg.LifetimeMax = 10f;
        cfg.SpinMin = 2f;
        cfg.SpinMax = 2f; // fixed 2 rad/s

        var sys = new ParticleSystem(16, seed: 1);
        sys.Emit(cfg, Vector3.Zero, 1);

        sys.Update(0.5f);
        Assert.Equal(1.0f, sys.Active[0].Rotation, 4); // 2 * 0.5
        sys.Update(0.5f);
        Assert.Equal(2.0f, sys.Active[0].Rotation, 4); // 2 * 1.0
    }

    [Fact]
    public void RandomStartRotation_SeedsVariedRotationsInRange()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 10f;
        cfg.LifetimeMax = 10f;
        cfg.RandomStartRotation = true;

        var sys = new ParticleSystem(64, seed: 2);
        sys.Emit(cfg, Vector3.Zero, 32);

        float first = sys.Active[0].Rotation;
        bool varied = false;
        foreach (var p in sys.Active)
        {
            Assert.True(p.Rotation >= 0f && p.Rotation < MathF.PI * 2f + 1e-4f);
            if (MathF.Abs(p.Rotation - first) > 1e-3f)
            {
                varied = true;
            }
        }

        Assert.True(varied, "random start rotation should differ across particles");
    }

    [Fact]
    public void Seed_IsPopulatedAndDistinctPerParticle()
    {
        var sys = new ParticleSystem(32, seed: 1);
        sys.Emit(BaseCfg(), Vector3.Zero, 16);

        var seeds = new HashSet<float>();
        foreach (var p in sys.Active)
        {
            Assert.True(p.Seed >= 0f && p.Seed < 1f, $"seed {p.Seed} out of [0,1)");
            seeds.Add(p.Seed);
        }

        Assert.Equal(16, seeds.Count); // all distinct
    }

    private static EmitterConfig TurbulenceCfg()
    {
        var cfg = BaseCfg();
        cfg.LifetimeMin = 10f;
        cfg.LifetimeMax = 10f;
        cfg.SpeedMin = 3f;
        cfg.SpeedMax = 5f;
        cfg.Direction = Vector3.Zero; // omni spread so particles fan out
        cfg.SpreadDegrees = 180f;
        cfg.TurbulenceStrength = 6f;
        cfg.TurbulenceFrequency = 1f;
        return cfg;
    }

    [Fact]
    public void Turbulence_SameSeed_IsDeterministic()
    {
        var a = new ParticleSystem(64, seed: 7);
        var b = new ParticleSystem(64, seed: 7);
        var cfg = TurbulenceCfg();

        a.Emit(cfg, Vector3.Zero, 32);
        b.Emit(cfg, Vector3.Zero, 32);
        for (int i = 0; i < 20; i++)
        {
            a.Update(0.05f);
            b.Update(0.05f);
        }

        Assert.Equal(a.ActiveCount, b.ActiveCount);
        for (int i = 0; i < a.ActiveCount; i++)
        {
            Assert.Equal(a.Active[i].Position, b.Active[i].Position);
            Assert.Equal(a.Active[i].Velocity, b.Active[i].Velocity);
        }
    }

    [Fact]
    public void Turbulence_PerturbsMotion_ComparedToNone()
    {
        // Same seed => identical emitted particles. Only the turbulence force in Update differs.
        var withTurb = new ParticleSystem(64, seed: 3);
        var noTurb = new ParticleSystem(64, seed: 3);

        var turbCfg = TurbulenceCfg();
        var plainCfg = TurbulenceCfg();
        plainCfg.TurbulenceStrength = 0f;

        withTurb.Emit(turbCfg, Vector3.Zero, 32);
        noTurb.Emit(plainCfg, Vector3.Zero, 32);
        for (int i = 0; i < 20; i++)
        {
            withTurb.Update(0.05f);
            noTurb.Update(0.05f);
        }

        bool anyMoved = false;
        for (int i = 0; i < withTurb.ActiveCount; i++)
        {
            if ((withTurb.Active[i].Position - noTurb.Active[i].Position).Length() > 1e-3f)
            {
                anyMoved = true;
                break;
            }
        }

        Assert.True(anyMoved, "turbulence should perturb trajectories away from the drag/gravity-only path");
    }

    [Fact]
    public void Turbulence_DifferentSeed_Diverges()
    {
        var a = new ParticleSystem(64, seed: 1);
        var b = new ParticleSystem(64, seed: 2);
        var cfg = TurbulenceCfg();

        a.Emit(cfg, Vector3.Zero, 32);
        b.Emit(cfg, Vector3.Zero, 32);
        for (int i = 0; i < 10; i++)
        {
            a.Update(0.05f);
            b.Update(0.05f);
        }

        bool diverged = false;
        for (int i = 0; i < a.ActiveCount; i++)
        {
            if (a.Active[i].Position != b.Active[i].Position)
            {
                diverged = true;
                break;
            }
        }

        Assert.True(diverged);
    }

    [Fact]
    public void LegacyConfig_Update_MatchesStraightLerp_BitIdentical()
    {
        // A zero-default config must integrate size/colour with the exact legacy single-lerp formula.
        var cfg = BaseCfg();
        cfg.StartSize = 1f;
        cfg.EndSize = 4f;
        cfg.StartColor = new Color(0.2f, 0.4f, 0.6f, 1f);
        cfg.EndColor = new Color(0.9f, 0.1f, 0.3f, 0f);

        var sys = new ParticleSystem(16, seed: 1);
        sys.Emit(cfg, Vector3.Zero, 1);
        sys.Update(0.37f);

        var p = sys.Active[0];
        float n = p.Norm;
        float expectedSize = MathUtil.Lerp(1f, 4f, n);
        Color expectedColor = (Color)Vector4.Lerp(cfg.StartColor.ToVector4(), cfg.EndColor.ToVector4(), n);

        Assert.Equal(expectedSize, p.Size);
        Assert.Equal(expectedColor, p.Color);
    }
}
