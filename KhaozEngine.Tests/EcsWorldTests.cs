using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file sealed class Tag : IComponent { public int Value; }
file sealed class Other : IComponent { }

public class EcsWorldTests
{
    [Fact]
    public void SpawnGivesUniqueIds()
    {
        var w = new World();
        Assert.NotEqual(w.Spawn().Id, w.Spawn().Id);
    }

    [Fact]
    public void SetGetHasWork()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Tag { Value = 7 });
        Assert.True(w.Has<Tag>(e));
        Assert.Equal(7, w.Get<Tag>(e).Value);
        Assert.False(w.Has<Other>(e));
    }

    [Fact]
    public void GetReturnsSameInstanceSoMutationSticks()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Tag { Value = 1 });
        w.Get<Tag>(e).Value = 99;
        Assert.Equal(99, w.Get<Tag>(e).Value);
    }

    [Fact]
    public void QueryReturnsEntitiesWithAllComponents()
    {
        var w = new World();
        var a = w.Spawn(); w.Set(a, new Tag()); w.Set(a, new Other());
        var b = w.Spawn(); w.Set(b, new Tag());
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), w.Query<Tag>().Select(e => e.Id).OrderBy(x => x));
        Assert.Equal(new[] { a.Id }, w.Query<Tag, Other>().Select(e => e.Id).ToArray());
    }

    [Fact]
    public void DespawnIsDeferredUntilUpdateFlush()
    {
        var w = new World();
        var e = w.Spawn(); w.Set(e, new Tag());
        w.Despawn(e);
        Assert.True(w.IsAlive(e));   // still alive until flush
        w.Update(0f);                // flush happens after systems run
        Assert.False(w.IsAlive(e));
        Assert.Empty(w.Query<Tag>());
    }
}
