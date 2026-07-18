using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Counter : IComponent { public int N; }
file sealed class Clock { public float Total; }

file sealed class TickSystem : ISystem
{
    public void Update(World w, float dt) => w.ForEach((Entity e, ref Counter c) => c.N++);
}

file struct Spawned : IComponent { }   // tag set via the command buffer

file sealed class DeferredSetSystem : ISystem
{
    private readonly Entity _e;
    public DeferredSetSystem(Entity e) => _e = e;
    public void Update(World w, float dt) => w.Commands.Set(_e, new Spawned());
}

public class WorldSystemsTests
{
    [Fact]
    public void ResourcesSetGetHas()
    {
        var w = new World();
        Assert.False(w.HasResource<Clock>());
        w.SetResource(new Clock { Total = 1.5f });
        Assert.True(w.HasResource<Clock>());
        Assert.Equal(1.5f, w.GetResource<Clock>().Total);
    }

    [Fact]
    public void SystemsRunInOrderOnUpdate()
    {
        var w = new World();
        var e = w.Spawn(); w.Set(e, new Counter { N = 0 });
        w.AddSystem(new TickSystem());
        w.Update(1f);
        w.Update(1f);
        Assert.Equal(2, w.Get<Counter>(e).N);
    }

    [Fact]
    public void CommandBufferRecordedInSystemIsFlushedAfterUpdate()
    {
        var w = new World();
        var e = w.Spawn();
        w.AddSystem(new DeferredSetSystem(e));
        Assert.False(w.Has<Spawned>(e));
        w.Update(1f);
        Assert.True(w.Has<Spawned>(e));   // playback applied the buffered Set
    }
}
