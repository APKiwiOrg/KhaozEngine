using System;
using System.Linq;
using Microsoft.Xna.Framework;
using KhaozEngine.Effects;
using Xunit;

namespace KhaozEngine.Tests;

public class ParticleSystemTests
{
    private static ParticleSystem NewSystem(int poolSize = 80)
        => new(new Random(12345), poolSize);

    [Fact]
    public void Emit_adds_active_particles()
    {
        var sys = NewSystem();
        sys.Emit(ParticlePresets.Spark, new Vector2(100, 100), Color.Gray, 5);
        Assert.Equal(5, sys.ActiveCount);
    }

    [Fact]
    public void Emit_invertedRanges_throws()
    {
        var sys = NewSystem();
        var badLife = ParticlePresets.Spark with { MinLife = 5f, MaxLife = 1f };
        var badSpeed = ParticlePresets.Spark with { MinSpeed = 200f, MaxSpeed = 10f };

        Assert.Throws<ArgumentException>(() => sys.Emit(badLife, Vector2.Zero, Color.Gray, 1));
        Assert.Throws<ArgumentException>(() => sys.Emit(badSpeed, Vector2.Zero, Color.Gray, 1));
    }

    [Fact]
    public void Emit_beyond_capacity_recycles_oldest_slots()
    {
        var sys = NewSystem(poolSize: 4);

        // First batch fills the pool at a far-away position.
        var oldPos = new Vector2(-10000, -10000);
        sys.Emit(ParticlePresets.Spark, oldPos, Color.Gray, 4);
        Assert.Equal(4, sys.ActiveCount);

        // Second batch (also pool-sized) at a distant position must overwrite the OLDEST slots.
        var newPos = new Vector2(10000, 10000);
        sys.Emit(ParticlePresets.Spark, newPos, Color.Gray, 4);

        Assert.Equal(4, sys.ActiveCount);

        // Every surviving particle must belong to the NEW batch, proving the originals were actually
        // overwritten and not merely counted. The two batches are 20000 units apart and the Spark
        // preset jitters by only a few units, so requiring positions well into the newPos half
        // (> 5000 on each axis) cleanly excludes any old-batch survivor near (-10000, -10000).
        foreach (var p in sys.ActiveParticles())
        {
            Assert.True(p.Position.X > 5000 && p.Position.Y > 5000,
                $"particle at {p.Position} survived from the old batch: oldest slots not recycled");
        }
    }

    [Fact]
    public void Spark_color_is_base_lerped_to_white()
    {
        var sys = NewSystem();
        var baseColor = new Color(100, 100, 100);
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), baseColor, 1);
        var expected = Color.Lerp(baseColor, Color.White, 0.5f);
        Assert.Equal(expected, sys.ActiveParticles().Single().Color);
    }

    [Fact]
    public void Ember_color_overrides_base()
    {
        var sys = NewSystem();
        sys.Emit(ParticlePresets.Ember, new Vector2(0, 0), Color.Gray, 1);
        Assert.Equal(new Color(255, 160, 40), sys.ActiveParticles().Single().Color);
    }

    [Fact]
    public void Radial_emission_produces_varied_directions()
    {
        var sys = NewSystem();
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), Color.Gray, 16);
        var velocities = sys.ActiveParticles().Select(p => p.Velocity).ToList();
        Assert.True(velocities.Distinct().Count() > 1);
    }

    [Fact]
    public void Particles_age_out_after_their_lifetime()
    {
        var sys = NewSystem();
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), Color.Gray, 5);
        // Spark MaxLife is 0.35s; step well past it.
        for (int i = 0; i < 10; i++) sys.Update(0.1);
        Assert.Equal(0, sys.ActiveCount);
    }

    [Fact]
    public void Ember_rises_and_sways()
    {
        var sys = NewSystem(poolSize: 1);
        sys.Emit(ParticlePresets.Ember, new Vector2(50, 50), Color.Gray, 1);
        var start = sys.ActiveParticles().Single().Position;
        for (int i = 0; i < 5; i++) sys.Update(0.02);
        var now = sys.ActiveParticles().Single().Position;
        Assert.True(now.Y < start.Y, "ember should drift upward");
        Assert.NotEqual(start.X, now.X); // horizontal sway moved it
    }

    [Fact]
    public void Acceleration_changes_velocity_over_time()
    {
        var gravity = ParticlePresets.Spark with { Acceleration = new Vector2(0, 200), SwayAmplitude = 0 };
        var sys = NewSystem(poolSize: 1);
        sys.Emit(gravity, new Vector2(0, 0), Color.Gray, 1);
        float vy0 = sys.ActiveParticles().Single().Velocity.Y;
        sys.Update(0.1);
        float vy1 = sys.ActiveParticles().Single().Velocity.Y;
        Assert.True(vy1 > vy0, "downward gravity should increase Y velocity");
    }

    [Fact]
    public void Spark_size_is_constant_over_life()
    {
        var sys = NewSystem(poolSize: 1);
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), Color.Gray, 1);
        Assert.Equal(2f, sys.ActiveParticles().Single().Size, 3);
        sys.Update(0.1);
        Assert.Equal(2f, sys.ActiveParticles().Single().Size, 3);
    }

    [Fact]
    public void Ember_size_shrinks_toward_end_factor()
    {
        var sys = NewSystem(poolSize: 1);
        sys.Emit(ParticlePresets.Ember, new Vector2(0, 0), Color.Gray, 1);
        float sizeAtSpawn = sys.ActiveParticles().Single().Size;
        sys.Update(0.2);
        var p = sys.ActiveParticles().Single();
        float t = p.Life / p.MaxLife;
        float expected = 3f * (0.3f + 0.7f * t);
        Assert.Equal(expected, p.Size, 3);
        Assert.True(p.Size < sizeAtSpawn, "ember shrinks as it ages");
    }
}
