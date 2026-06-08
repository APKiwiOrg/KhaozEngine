using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public class WorldEntityTests
{
    [Fact]
    public void SpawnGivesDistinctLiveEntities()
    {
        var w = new World();
        var a = w.Spawn();
        var b = w.Spawn();
        Assert.NotEqual(a.Id, b.Id);
        Assert.True(w.IsAlive(a));
        Assert.True(w.IsAlive(b));
    }

    [Fact]
    public void DespawnInvalidatesHandleAndRecyclesIdWithNewVersion()
    {
        var w = new World();
        var a = w.Spawn();
        w.Despawn(a);
        Assert.False(w.IsAlive(a));
        var b = w.Spawn();                 // reuses a.Id
        Assert.Equal(a.Id, b.Id);
        Assert.NotEqual(a.Version, b.Version);
        Assert.True(w.IsAlive(b));
        Assert.False(w.IsAlive(a));        // stale handle stays dead
    }
}
