using System.Numerics;
using KhaozEngine.Particles;
using Xunit;

namespace KhaozEngine.Tests.Particles;

public class ParticleSystemTests
{
    private static EmitterConfig BasicCfg(Vector3 direction, float spread = 0f) => new()
    {
        LifetimeMin = 1f,
        LifetimeMax = 1f,
        SpeedMin = 5f,
        SpeedMax = 5f,
        Direction = direction,
        SpreadDegrees = spread,
        Gravity = Vector3.Zero,
        Drag = 0f,
        StartSize = 1f,
        EndSize = 3f,
        StartColor = new Vector4(1f, 0f, 0f, 1f),
        EndColor = new Vector4(0f, 0f, 1f, 0f),
    };

    [Fact]
    public void Emit_AddsCount()
    {
        var sys = new ParticleSystem(100, seed: 1);
        sys.Emit(BasicCfg(Vector3.UnitX), Vector3.Zero, 10);

        Assert.Equal(10, sys.ActiveCount);
        Assert.Equal(10, sys.Active.Length);
    }

    [Fact]
    public void Emit_ClampsToRemainingCapacity()
    {
        var sys = new ParticleSystem(8, seed: 1);
        sys.Emit(BasicCfg(Vector3.UnitX), Vector3.Zero, 20);

        Assert.Equal(8, sys.ActiveCount);
        Assert.Equal(8, sys.Capacity);

        // Already full: a second emit adds nothing.
        sys.Emit(BasicCfg(Vector3.UnitX), Vector3.Zero, 5);
        Assert.Equal(8, sys.ActiveCount);
    }

    [Fact]
    public void Update_AgesParticles()
    {
        var sys = new ParticleSystem(10, seed: 1);
        sys.Emit(BasicCfg(Vector3.UnitX), Vector3.Zero, 3);

        sys.Update(0.25f);

        foreach (var p in sys.Active)
        {
            Assert.Equal(0.25f, p.Age, 5);
        }
    }

    [Fact]
    public void Update_RecyclesDeadParticles()
    {
        var cfg = BasicCfg(Vector3.UnitX);
        cfg.LifetimeMin = 0.5f;
        cfg.LifetimeMax = 0.5f;

        var sys = new ParticleSystem(10, seed: 1);
        sys.Emit(cfg, Vector3.Zero, 4);
        Assert.Equal(4, sys.ActiveCount);

        sys.Update(0.6f); // Age 0.6 >= Life 0.5 => all recycled.
        Assert.Equal(0, sys.ActiveCount);
        Assert.Equal(0, sys.Active.Length);
    }

    [Fact]
    public void Update_RecyclesOnlyExpired_KeepsLiveCompacted()
    {
        var sys = new ParticleSystem(10, seed: 7);

        var shortCfg = BasicCfg(Vector3.UnitX);
        shortCfg.LifetimeMin = 0.5f;
        shortCfg.LifetimeMax = 0.5f;
        sys.Emit(shortCfg, Vector3.Zero, 3);

        var longCfg = BasicCfg(Vector3.UnitX);
        longCfg.LifetimeMin = 5f;
        longCfg.LifetimeMax = 5f;
        sys.Emit(longCfg, Vector3.Zero, 2);

        Assert.Equal(5, sys.ActiveCount);

        sys.Update(0.6f); // The 3 short ones die, the 2 long survive.

        Assert.Equal(2, sys.ActiveCount);
        // Survivors are the long-lived ones, still alive, in a contiguous span.
        foreach (var p in sys.Active)
        {
            Assert.True(p.Alive);
            Assert.Equal(5f, p.Life, 5);
        }
    }

    [Fact]
    public void SizeAndColor_StartAtConfigStart_AfterEmit()
    {
        var sys = new ParticleSystem(10, seed: 1);
        sys.Emit(BasicCfg(Vector3.UnitX), Vector3.Zero, 1);

        // Tiny dt => Norm ~ 0 => ~start.
        sys.Update(0.0001f);
        var p = sys.Active[0];

        Assert.Equal(1f, p.Size, 2);
        Assert.True(System.Math.Abs(p.Color.X - 1f) < 0.01f); // red start
        Assert.True(System.Math.Abs(p.Color.W - 1f) < 0.01f); // alpha start
    }

    [Fact]
    public void SizeAndColor_ReachConfigEnd_NearDeath()
    {
        var sys = new ParticleSystem(10, seed: 1);
        sys.Emit(BasicCfg(Vector3.UnitX), Vector3.Zero, 1); // Life = 1.0

        sys.Update(0.999f); // Norm ~ 0.999 => ~end.
        var p = sys.Active[0];

        Assert.Equal(3f, p.Size, 1);            // EndSize = 3
        Assert.True(p.Color.Z > 0.99f);          // blue end
        Assert.True(p.Color.W < 0.01f);          // alpha faded to 0
    }

    [Fact]
    public void Gravity_MovesPositionDownOverSteps()
    {
        var cfg = BasicCfg(Vector3.UnitX);
        cfg.SpeedMin = 0f;
        cfg.SpeedMax = 0f;                 // no initial velocity
        cfg.Gravity = new Vector3(0f, -10f, 0f);
        cfg.LifetimeMin = 10f;
        cfg.LifetimeMax = 10f;

        var sys = new ParticleSystem(10, seed: 1);
        sys.Emit(cfg, Vector3.Zero, 1);

        for (int i = 0; i < 10; i++)
        {
            sys.Update(0.1f);
        }

        Assert.True(sys.Active[0].Position.Y < 0f);
        Assert.True(sys.Active[0].Velocity.Y < 0f);
    }

    [Fact]
    public void Drag_ReducesSpeed()
    {
        var cfg = BasicCfg(Vector3.UnitX);
        cfg.SpeedMin = 10f;
        cfg.SpeedMax = 10f;
        cfg.Gravity = Vector3.Zero;
        cfg.Drag = 3f;
        cfg.LifetimeMin = 10f;
        cfg.LifetimeMax = 10f;

        var sys = new ParticleSystem(10, seed: 1);
        sys.Emit(cfg, Vector3.Zero, 1);

        float speedBefore = sys.Active[0].Velocity.Length();
        sys.Update(0.1f);
        float speedAfter = sys.Active[0].Velocity.Length();

        Assert.True(speedAfter < speedBefore);
    }

    [Fact]
    public void Determinism_SameSeedSameCalls_ProduceIdenticalActive()
    {
        var cfg = EmitterConfig.Spark;

        var a = new ParticleSystem(64, seed: 42);
        var b = new ParticleSystem(64, seed: 42);

        for (int frame = 0; frame < 5; frame++)
        {
            a.Emit(cfg, new Vector3(frame, 0, 0), 8);
            b.Emit(cfg, new Vector3(frame, 0, 0), 8);
            a.Update(0.05f);
            b.Update(0.05f);
        }

        Assert.Equal(a.ActiveCount, b.ActiveCount);
        var sa = a.Active;
        var sb = b.Active;
        for (int i = 0; i < sa.Length; i++)
        {
            Assert.Equal(sa[i].Position, sb[i].Position);
            Assert.Equal(sa[i].Velocity, sb[i].Velocity);
            Assert.Equal(sa[i].Age, sb[i].Age);
            Assert.Equal(sa[i].Life, sb[i].Life);
            Assert.Equal(sa[i].Size, sb[i].Size);
            Assert.Equal(sa[i].Color, sb[i].Color);
        }
    }

    [Fact]
    public void DifferentSeeds_DivergeSomewhere()
    {
        var cfg = EmitterConfig.Spark;
        var a = new ParticleSystem(64, seed: 1);
        var b = new ParticleSystem(64, seed: 2);

        a.Emit(cfg, Vector3.Zero, 16);
        b.Emit(cfg, Vector3.Zero, 16);

        bool anyDifferent = false;
        for (int i = 0; i < 16; i++)
        {
            if (a.Active[i].Velocity != b.Active[i].Velocity)
            {
                anyDifferent = true;
                break;
            }
        }

        Assert.True(anyDifferent);
    }

    [Fact]
    public void Spread_Zero_ProducesParallelVelocities()
    {
        var dir = Vector3.Normalize(new Vector3(1f, 2f, -1f));
        var cfg = BasicCfg(dir, spread: 0f);

        var sys = new ParticleSystem(32, seed: 3);
        sys.Emit(cfg, Vector3.Zero, 16);

        // With zero spread, every velocity points along Direction (speed fixed at 5).
        var expected = dir * 5f;
        foreach (var p in sys.Active)
        {
            Assert.True((p.Velocity - expected).Length() < 1e-3f,
                $"expected {expected}, got {p.Velocity}");
        }
    }

    [Fact]
    public void Spread_Wide_ProducesVariedDirections()
    {
        var dir = Vector3.UnitX;
        var cfg = BasicCfg(dir, spread: 180f);

        var sys = new ParticleSystem(64, seed: 9);
        sys.Emit(cfg, Vector3.Zero, 32);

        var first = Vector3.Normalize(sys.Active[0].Velocity);
        bool varied = false;
        for (int i = 1; i < sys.ActiveCount; i++)
        {
            var d = Vector3.Normalize(sys.Active[i].Velocity);
            if ((d - first).Length() > 0.1f)
            {
                varied = true;
                break;
            }
        }

        Assert.True(varied);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var sys = new ParticleSystem(10, seed: 1);
        sys.Emit(BasicCfg(Vector3.UnitX), Vector3.Zero, 5);
        Assert.Equal(5, sys.ActiveCount);

        sys.Clear();
        Assert.Equal(0, sys.ActiveCount);
        Assert.Equal(0, sys.Active.Length);
    }
}
