using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerAdminTests
{
    private static float Flat(float x, float z) => 0f;

    [Fact]
    public async Task BanAsync_PersistsAndKicksOnlinePlayer()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var bans = new InMemoryBanStore();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, banStore: bans);
        var client = new NetClient(ct, Encoding.UTF8.GetBytes("evil"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        var admin = new ServerAdmin(server, bans);
        await admin.BanAsync("evil", "cheating");
        for (int i = 0; i < 60 && server.PlayerCount > 0; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(bans.IsBanned("evil"));
        Assert.Equal(0, server.PlayerCount);
        Assert.Equal("cheating", admin.ListBans().Single().Reason);
    }

    [Fact]
    public async Task ListAccounts_MaterializesEnumeration_AndFeatureDetects()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:1", new byte[] { 1 });

        var admin = new ServerAdmin(server, bans: null, accounts: store);
        IReadOnlyList<WorldStoreEntry> accounts = await admin.ListAccountsAsync("player:");

        Assert.Single(accounts);
        Assert.True(admin.AccountsSupported);
        Assert.False(admin.BansSupported);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await admin.UnbanAsync("x"));
    }
}
