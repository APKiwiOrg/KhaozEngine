using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct LBPos : IComponent { public int X; }
file struct LBTag : IComponent { }

public class WorldLoadSurfaceTests
{
    [Fact]
    public void CreateAtPlacesEntityWithExactIdAndVersion()
    {
        var w = new World();
        var e = w.CreateAt(5, 3);
        Assert.Equal(5, e.Id);
        Assert.Equal(3u, e.Version);
        Assert.True(w.IsAlive(e));
        w.SetByType(e, typeof(LBPos), new LBPos { X = 9 });
        Assert.Equal(9, w.Get<LBPos>(e).X);
        w.SetByType(e, typeof(LBTag), new LBTag());
        Assert.True(w.Has<LBTag>(e));
    }

    [Fact]
    public void RestoreAllocatorControlsNextSpawnAndRecycling()
    {
        var w = new World();
        w.RestoreAllocator(10, new (int id, uint version)[] { (4, 7) });
        var reused = w.Spawn();
        Assert.Equal(4, reused.Id);                 // recycled id popped before fresh
        Assert.Equal(7u, reused.Version);           // its saved version kept (recycle does not reset)
        var fresh = w.Spawn();
        Assert.Equal(10, fresh.Id);                 // then continues from nextId
    }

    [Fact]
    public void SaveAccessorsReportAllocatorState()
    {
        var w = new World();
        var a = w.Spawn(); var b = w.Spawn(); w.Spawn();
        w.Despawn(b);                               // frees b.Id with a bumped version
        Assert.Equal(3, w.SaveNextId);
        var free = w.SaveFreeSlots.ToList();
        Assert.Single(free);
        Assert.Equal(b.Id, free[0].id);
        Assert.True(free[0].version > b.Version);   // version was bumped on despawn
    }
}
