using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldStoreBanStoreTests
{
    [Fact]
    public async Task Ban_Persists_AndHydratesIntoAFreshStore()
    {
        var backing = new InMemoryWorldStore();
        var bans = new WorldStoreBanStore(backing);
        await bans.BanAsync("evil", "cheating");
        Assert.True(bans.IsBanned("evil"));
        Assert.True(await backing.ExistsAsync("ban:evil"));

        var reloaded = new WorldStoreBanStore(backing);
        Assert.False(reloaded.IsBanned("evil"));   // cache not hydrated yet
        await reloaded.LoadAsync();
        Assert.True(reloaded.IsBanned("evil"));

        await reloaded.UnbanAsync("evil");
        Assert.False(reloaded.IsBanned("evil"));
        Assert.False(await backing.ExistsAsync("ban:evil"));
    }
}
