using System.Threading.Tasks;
using System.Text.Json;
using KhaozEngine.NetWorld;
using KhaozEngine.Serialization;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldStoreBanStoreTests
{
    [Fact]
    public async Task Ban_PersistedBlob_MatchesReflectionEncoding_ByteForByte()
    {
        // The persisted ban blob is encoded through the source-generated NetWorldJsonContext (NativeAOT-safe). It must
        // stay byte-for-byte identical to the historical reflection encoding of the same BanDto so existing ban records
        // keep loading.
        var backing = new InMemoryWorldStore();
        await new WorldStoreBanStore(backing).BanAsync("evil", "cheating");

        byte[]? stored = await backing.LoadAsync("ban:evil");
        Assert.NotNull(stored);
        byte[] reflection = JsonSerializer.SerializeToUtf8Bytes(
            new WorldStoreBanStore.BanDto { AccountId = "evil", Reason = "cheating", Until = null },
            JsonDefaults.IndentedWrite);
        Assert.Equal(reflection, stored);
    }

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
