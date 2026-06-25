using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

public class WorldStoreTests
{
    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        IWorldStore store = new InMemoryWorldStore();
        await store.SaveAsync("char/1", new byte[] { 1, 2, 3 });
        byte[]? loaded = await store.LoadAsync("char/1");
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded);
    }

    [Fact]
    public async Task Load_MissingKey_ReturnsNull()
    {
        IWorldStore store = new InMemoryWorldStore();
        Assert.Null(await store.LoadAsync("nope"));
    }

    [Fact]
    public async Task Save_Overwrites()
    {
        IWorldStore store = new InMemoryWorldStore();
        await store.SaveAsync("k", new byte[] { 1 });
        await store.SaveAsync("k", new byte[] { 9, 9 });
        Assert.Equal(new byte[] { 9, 9 }, await store.LoadAsync("k"));
    }

    [Fact]
    public async Task Delete_RemovesAndReportsPresence()
    {
        IWorldStore store = new InMemoryWorldStore();
        await store.SaveAsync("k", new byte[] { 1 });
        Assert.True(await store.DeleteAsync("k"));
        Assert.False(await store.DeleteAsync("k"));   // already gone
        Assert.False(await store.ExistsAsync("k"));
    }

    [Fact]
    public async Task Load_ReturnsIndependentCopy()
    {
        IWorldStore store = new InMemoryWorldStore();
        await store.SaveAsync("k", new byte[] { 1, 2, 3 });
        byte[] first = (await store.LoadAsync("k"))!;
        first[0] = 99;                                 // mutate the returned array
        byte[] second = (await store.LoadAsync("k"))!;
        Assert.Equal(new byte[] { 1, 2, 3 }, second);  // stored state is unaffected
    }
}
