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
    public void Emit_beyond_capacity_recycles_oldest_slots()
    {
        var sys = NewSystem(poolSize: 4);
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), Color.Gray, 10);
        Assert.Equal(4, sys.ActiveCount);
    }
}
