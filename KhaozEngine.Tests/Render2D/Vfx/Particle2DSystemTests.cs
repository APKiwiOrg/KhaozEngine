using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>Headless coverage for the pooled, deterministic 2D particle system (no GPU).</summary>
public class Particle2DSystemTests
{
    const float Tol = 1e-4f;

    // A config whose only randomness is collapsed (min==max ranges, zero jitter), so a single emitted
    // particle is fully deterministic and its motion is hand-computable.
    static Particle2DEmitterConfig Fixed(float speed, Vector2 dir) => new()
    {
        MinLife = 100f,
        MaxLife = 100f,
        MinSpeed = speed,
        MaxSpeed = speed,
        Emission = Particle2DEmission.Directional,
        Direction = dir,
        SpreadRadians = 0f,
        JitterX = 0f,
        JitterY = 0f,
        StartSize = 1f,
        EndSize = 1f,
    };

    [Fact]
    public void Emit_RaisesActiveCount()
    {
        var sys = new Particle2DSystem(capacity: 16, seed: 1);
        Assert.Equal(0, sys.ActiveCount);
        sys.Emit(Fixed(10f, new Vector2(1, 0)), Vector2.Zero, 5);
        Assert.Equal(5, sys.ActiveCount);
    }

    [Fact]
    public void Update_DepletesLifeAndDeactivates()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { MinLife = 0.1f, MaxLife = 0.1f };
        var sys = new Particle2DSystem(8, 1);
        sys.Emit(cfg, Vector2.Zero, 3);
        Assert.Equal(3, sys.ActiveCount);
        sys.Update(0.2f);
        Assert.Equal(0, sys.ActiveCount);
    }

    [Fact]
    public void Gravity_AddsToVelocityThenIntegratesPosition()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { Acceleration = new Vector2(0, 200f) };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        sys.Update(0.1f);
        var p = sys.ActiveParticles().Single();
        Assert.Equal(20f, p.Velocity.Y, Tol);   // 0 + 200*0.1
        Assert.Equal(2f, p.Position.Y, Tol);     // moved by the new velocity * dt
    }

    [Fact]
    public void Drag_DampensVelocity()
    {
        var cfg = Fixed(100f, new Vector2(1, 0)) with { Drag = 0.5f };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        sys.Update(0.1f);
        var p = sys.ActiveParticles().Single();
        Assert.Equal(95f, p.Velocity.X, Tol);    // 100 * (1 - 0.5*0.1)
        Assert.Equal(9.5f, p.Position.X, Tol);   // moved by damped velocity * dt
    }

    [Fact]
    public void Rotation_AdvancesByAngularVelocity()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { MinAngularVelocity = 2f, MaxAngularVelocity = 2f };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        sys.Update(0.5f);
        var p = sys.ActiveParticles().Single();
        Assert.Equal(1f, p.Rotation, Tol);       // 2 rad/s * 0.5s
    }

    [Fact]
    public void Size_LerpsFromStartToEndOverLife()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { MinLife = 1f, MaxLife = 1f, StartSize = 10f, EndSize = 2f };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        Assert.Equal(10f, sys.ActiveParticles().Single().Size, Tol);   // spawn -> StartSize
        sys.Update(0.5f);
        Assert.Equal(6f, sys.ActiveParticles().Single().Size, Tol);    // halfway -> lerp(10,2,0.5)
    }

    [Fact]
    public void Color_LerpsFromStartToEndOverLife()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with
        {
            MinLife = 1f, MaxLife = 1f,
            StartColor = new Color(1, 0, 0, 1),
            EndColor = new Color(0, 0, 1, 1),
        };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        Assert.Equal(new Color(1, 0, 0, 1), sys.ActiveParticles().Single().Color);
        sys.Update(0.5f);
        var c = sys.ActiveParticles().Single().Color;
        Assert.Equal(0.5f, c.R, Tol);
        Assert.Equal(0.5f, c.B, Tol);
    }

    [Fact]
    public void Emit_WithTint_MultipliesStartColor()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { StartColor = Color.White, EndColor = Color.White };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, new Color(1, 0, 0, 1), 1);
        var c = sys.ActiveParticles().Single().Color;
        Assert.Equal(1f, c.R, Tol);
        Assert.Equal(0f, c.G, Tol);
        Assert.Equal(0f, c.B, Tol);
    }

    [Fact]
    public void Pool_IsRingBuffer_CapsAtCapacity()
    {
        var sys = new Particle2DSystem(capacity: 4, seed: 1);
        sys.Emit(Fixed(10f, new Vector2(1, 0)), Vector2.Zero, 6);
        Assert.Equal(4, sys.ActiveCount);
    }

    [Fact]
    public void Blend_FromConfig_IsExposedOnView()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { Blend = BlendMode.Additive };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        Assert.Equal(BlendMode.Additive, sys.ActiveParticles().Single().Blend);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalState()
    {
        var cfg = new Particle2DEmitterConfig
        {
            MinLife = 0.5f, MaxLife = 1.5f,
            MinSpeed = 20f, MaxSpeed = 80f,
            Emission = Particle2DEmission.Radial,
            JitterX = 4f, JitterY = 4f,
            StartSize = 3f, EndSize = 0f,
            Acceleration = new Vector2(0, 50f),
            Drag = 0.2f,
            SwayFrequency = 5f, SwayAmplitude = 6f,
            RotationJitter = 3f,
            MinAngularVelocity = -2f, MaxAngularVelocity = 2f,
        };

        var a = new Particle2DSystem(64, 12345);
        var b = new Particle2DSystem(64, 12345);
        a.Emit(cfg, new Vector2(100, 100), 40);
        b.Emit(cfg, new Vector2(100, 100), 40);
        for (int i = 0; i < 10; i++) { a.Update(0.016f); b.Update(0.016f); }

        List<Particle2DView> pa = a.ActiveParticles().ToList();
        List<Particle2DView> pb = b.ActiveParticles().ToList();
        Assert.Equal(pa.Count, pb.Count);
        for (int i = 0; i < pa.Count; i++)
        {
            Assert.Equal(pa[i].Position.X, pb[i].Position.X, Tol);
            Assert.Equal(pa[i].Position.Y, pb[i].Position.Y, Tol);
            Assert.Equal(pa[i].Velocity.X, pb[i].Velocity.X, Tol);
            Assert.Equal(pa[i].Rotation, pb[i].Rotation, Tol);
        }
    }

    [Fact]
    public void DifferentSeed_DivergesState()
    {
        var cfg = Fixed(50f, new Vector2(1, 0)) with { Emission = Particle2DEmission.Radial };
        var a = new Particle2DSystem(64, 1);
        var b = new Particle2DSystem(64, 2);
        a.Emit(cfg, Vector2.Zero, 30);
        b.Emit(cfg, Vector2.Zero, 30);
        a.Update(0.1f); b.Update(0.1f);
        // At least one particle differs between the two seeds.
        var pa = a.ActiveParticles().ToList();
        var pb = b.ActiveParticles().ToList();
        bool anyDiff = pa.Where((t, i) => Vector2.Distance(t.Position, pb[i].Position) > Tol).Any();
        Assert.True(anyDiff, "different seeds should diverge");
    }

    [Fact]
    public void DirectionalEmission_StaysWithinCone()
    {
        var cfg = new Particle2DEmitterConfig
        {
            MinLife = 1f, MaxLife = 1f,
            MinSpeed = 50f, MaxSpeed = 50f,
            Emission = Particle2DEmission.Directional,
            Direction = new Vector2(1, 0),
            SpreadRadians = 0.3f,
        };
        var sys = new Particle2DSystem(64, 7);
        sys.Emit(cfg, Vector2.Zero, 40);
        foreach (var p in sys.ActiveParticles())
            Assert.True(p.Velocity.X > 0f, "directional cone around +X should keep vx positive");
    }

    [Fact]
    public void Clear_RemovesAllParticles()
    {
        var sys = new Particle2DSystem(8, 1);
        sys.Emit(Fixed(10f, new Vector2(1, 0)), Vector2.Zero, 5);
        sys.Clear();
        Assert.Equal(0, sys.ActiveCount);
    }
}
