using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Pos : IComponent { public int X; }
file struct Marker : IComponent { }   // tag, forces a distinct archetype

public class CachedQueryTests
{
    [Fact]
    public void ForSameWorldReturnsSameQueryInstanceAcrossCalls()
    {
        var w = new World();
        var cached = new CachedQuery(world => world.Query().With<Pos>());

        Query first = cached.For(w);
        Query second = cached.For(w);

        Assert.Same(first, second);   // no per-tick reallocation
    }

    [Fact]
    public void ReusedQueryPicksUpEntitiesSpawnedIntoNewArchetypeBetweenCalls()
    {
        var w = new World();
        var cached = new CachedQuery(world => world.Query().With<Pos>());

        var e1 = w.Spawn(); w.Set(e1, new Pos { X = 1 });
        Assert.Single(cached.For(w).Entities().ToList());

        // New archetype (Pos + Marker) appears after the query was first used.
        var e2 = w.Spawn(); w.Set(e2, new Pos { X = 2 }); w.Set(e2, new Marker());

        var xs = cached.For(w).Entities().Select(e => w.Get<Pos>(e).X).ToList();
        xs.Sort();
        Assert.Equal(new[] { 1, 2 }, xs);   // gen self-refresh still works through the cache
    }

    [Fact]
    public void ForDifferentWorldRebuildsAndYieldsNewWorldEntities()
    {
        var cached = new CachedQuery(world => world.Query().With<Pos>());

        var w1 = new World();
        var a = w1.Spawn(); w1.Set(a, new Pos { X = 10 });
        Assert.Single(cached.For(w1).Entities().ToList());

        var w2 = new World();
        var b = w2.Spawn(); w2.Set(b, new Pos { X = 20 });
        var c = w2.Spawn(); w2.Set(c, new Pos { X = 30 });

        var xs = cached.For(w2).Entities().Select(e => w2.Get<Pos>(e).X).ToList();
        xs.Sort();
        Assert.Equal(new[] { 20, 30 }, xs);   // rebound to w2, not still serving w1's single entity
    }
}
