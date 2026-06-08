using System;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public struct DiA : IComponent { public int V; }
public struct DiB : IComponent { public int V; }
public struct DiC : IComponent { public int V; }

public class DeterministicIterationTests
{
    // Identical scripted sequence -> a fully-determined world.
    private static World Build()
    {
        var w = new World();
        var es = new Entity[12];
        for (int i = 0; i < 12; i++) es[i] = w.Spawn();
        for (int i = 0; i < 12; i++)
        {
            w.Set(es[i], new DiA { V = i });
            if (i % 2 == 0) w.Set(es[i], new DiB { V = i });
            if (i % 3 == 0) w.Set(es[i], new DiC { V = i });
        }
        w.Despawn(es[1]); w.Despawn(es[4]); w.Despawn(es[9]);   // swap-remove churn
        return w;
    }

    [Fact]
    public void IterationOrderIsReproducibleAcrossWorlds()
    {
        var oa = Build().Query().With<DiA>().Entities().ToArray();
        var ob = Build().Query().With<DiA>().Entities().ToArray();
        Assert.NotEmpty(oa);
        Assert.Equal(oa, ob);                       // identical handle sequence, element-for-element
    }

    [Fact]
    public void CrossArchetypeOrderFollowsCreationOrder()
    {
        var w = new World();
        var first = w.Spawn(); w.Set(first, new DiC { V = 1 });    // creates archetype {DiC}
        var second = w.Spawn(); w.Set(second, new DiA { V = 2 });  // then archetype {DiA}
        var order = w.Query().Entities().ToArray();                // no filter: spans all archetypes
        Assert.True(Array.IndexOf(order, first) < Array.IndexOf(order, second));
    }

    [Fact]
    public void SaveOutputIsByteStableAcrossIdenticalWorlds()
    {
        var ser = new WorldSerializer(typeof(DiA), typeof(DiB), typeof(DiC));
        Assert.Equal(ser.Save(Build()), ser.Save(Build()));
    }
}
