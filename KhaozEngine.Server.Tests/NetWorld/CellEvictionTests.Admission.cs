using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class CellEvictionTests
{
    [Theory]
    [InlineData(CellAdmissionOutcome.Unresolved)]
    [InlineData(CellAdmissionOutcome.Refused)]
    public async Task DeferredCacheRestoreKeepsItsTransientEntitiesAndBlocksEmptySaves(CellAdmissionOutcome refusal)
    {
        var store = new InMemoryWorldStore();
        var host = new GridHost();
        var persistence = new CellPersistence(host, store);
        var evictor = new CellEvictor(host, persistence);
        long id = host.SpawnNode(250, 250, 77);
        Assert.True(host.Host.TryGetOwner(id, out var source, out var entity));
        source.World.Set(entity, new Transient { Scope = TransientScope.DurableOnly });
        await EvictAsync(evictor, persistence, C22);
        byte[]? saved = await store.LoadAsync("cell:2:2");
        Assert.NotNull(saved);
        host.AdmissionRefusal = refusal;
        var destination = host.Host.EnsureCell(C22);
        Assert.False(destination.TryGetOwned(id, out _));
        await persistence.FlushAsync();
        Assert.True(persistence.IsBusy(C22));
        Assert.False(evictor.RequestEvict(C22));
        Assert.Equal(saved, await store.LoadAsync("cell:2:2"));
        host.AdmissionRefusal = null;
        evictor.Update(0);
        Assert.True(destination.TryGetOwned(id, out Entity restored));
        Assert.Equal(77, destination.World.Get<Node>(restored).Amount);
        Assert.Equal(TransientScope.DurableOnly, destination.World.Get<Transient>(restored).Scope);
        Assert.False(persistence.IsBusy(C22));
        Assert.Equal(1, evictor.RestoredFromCacheCount);
    }
}
