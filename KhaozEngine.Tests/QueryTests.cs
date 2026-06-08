using System.Collections.Generic;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct P : IComponent { public int X; }
file struct V : IComponent { public int Dx; }
file struct Stunned : IComponent { }   // tag filter

public class QueryTests
{
    [Fact]
    public void ForEachSingleComponentMutatesInPlace()
    {
        var w = new World();
        for (int i = 0; i < 3; i++) { var e = w.Spawn(); w.Set(e, new P { X = i }); }
        w.ForEach((Entity e, ref P p) => p.X += 10);
        var xs = new List<int>();
        w.ForEach((Entity e, ref P p) => xs.Add(p.X));
        xs.Sort();
        Assert.Equal(new[] { 10, 11, 12 }, xs);
    }

    [Fact]
    public void ForEachTwoComponentsOnlyVisitsEntitiesWithBoth()
    {
        var w = new World();
        var a = w.Spawn(); w.Set(a, new P { X = 1 }); w.Set(a, new V { Dx = 5 });
        var b = w.Spawn(); w.Set(b, new P { X = 2 });                    // no V
        int visited = 0;
        w.ForEach((Entity e, ref P p, ref V v) => { p.X += v.Dx; visited++; });
        Assert.Equal(1, visited);
        Assert.Equal(6, w.Get<P>(a).X);
        Assert.Equal(2, w.Get<P>(b).X);
    }

    [Fact]
    public void WithoutFilterExcludesTaggedEntities()
    {
        var w = new World();
        var a = w.Spawn(); w.Set(a, new P { X = 1 });
        var b = w.Spawn(); w.Set(b, new P { X = 2 }); w.Set(b, new Stunned());
        int seen = 0;
        w.Query().Without<Stunned>().ForEach((Entity e, ref P p) => seen++);
        Assert.Equal(1, seen);
    }
}
