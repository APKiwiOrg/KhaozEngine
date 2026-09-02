using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
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
        var client = new NetClient(ct, TestHandshake.Wire("evil"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        var admin = new ServerAdmin(server, bans);
        await admin.BanAsync("evil", "cheating");
        for (int i = 0; i < 60 && server.PlayerCount > 0; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(bans.IsBanned("evil"));
        Assert.Equal(0, server.PlayerCount);
        Assert.Equal("cheating", admin.ListBans().Single().Reason);
    }

    /// <summary>
    /// Banning a TOKENLESS connection is refused, because the id it would be filed under names a seat and not a
    /// person. Both heads derive a tokenless connection's account id as guest:{slot}, and the slot is recycled to
    /// whoever the allocator seats there next, so the ban landed on a chair: every later tokenless connection into
    /// that slot was rejected and the player who earned the ban reconnected onto the next slot and carried on.
    /// Kick is the honest tool for a player with no durable identity.
    /// </summary>
    [Fact]
    public async Task BanAsync_RefusesAGuestAccount_ratherThanBanningTheSeat()
    {
        var (st, _) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var bans = new InMemoryBanStore();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, banStore: bans);
        var admin = new ServerAdmin(server, bans);

        string seat = ResumePositionCache.GuestAccountPrefix + "0";
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(
            async () => await admin.BanAsync(seat, "griefing"));
        Assert.Contains(seat, refused.Message, StringComparison.Ordinal);

        Assert.False(bans.IsBanned(seat));
        Assert.Empty(admin.ListBans());

        // It is the PREFIX that decides, not the word: an account genuinely named guest is an account.
        await admin.BanAsync("guest", "griefing");
        Assert.True(bans.IsBanned("guest"));
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

    [Theory]
    [InlineData("Upper")]      // uppercase
    [InlineData("")]           // empty
    [InlineData("has space")]  // space
    [InlineData("-lead")]      // leading dash
    public void RegisterAction_RejectsInvalidNames(string bad)
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        Assert.Throws<ArgumentException>(() => admin.RegisterAction(bad, _ => AdminActionResult.Ok()));
    }

    [Fact]
    public void RegisterAction_RejectsNameOver64Chars()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        Assert.Throws<ArgumentException>(() => admin.RegisterAction(new string('a', 65), _ => AdminActionResult.Ok()));
    }

    [Fact]
    public void RegisterAction_RejectsDuplicate()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("ping", _ => AdminActionResult.Ok());
        Assert.Throws<ArgumentException>(() => admin.RegisterAction("ping", _ => AdminActionResult.Ok()));
    }

    [Fact]
    public void ActionNames_ListsRegistrations()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("alpha", _ => AdminActionResult.Ok());
        admin.RegisterAction("bravo", _ => AdminActionResult.Ok());
        Assert.Equal(new[] { "alpha", "bravo" }, admin.ActionNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SyncOverload_WrapsAndExecutes()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        admin.RegisterAction("echo", _ => AdminActionResult.Ok(new { ok = true }));

        Assert.True(admin.TryGetAction("echo", out var handler));
        AdminActionResult result = await handler(null, CancellationToken.None);
        Assert.Equal(AdminActionStatus.Ok, result.Status);
        Assert.NotNull(result.Payload);
    }

    [Fact]
    public void TryGetAction_ReturnsFalseForUnknown()
    {
        var admin = new ServerAdmin(new NullAdminControllable());
        Assert.False(admin.TryGetAction("missing", out _));
    }

    // A live world is irrelevant to the action registry, which hangs on ServerAdmin itself, so these tests back the
    // facade with a do-nothing controllable rather than spinning a WorldServer over LoopbackTransport.
    private sealed class NullAdminControllable : IAdminControllable
    {
        public IReadOnlyList<OnlinePlayer> ListOnline() => Array.Empty<OnlinePlayer>();
        public void Teleport(PlayerRef target, Vector3 position) { }
        public void Kick(PlayerRef target, string reason) { }
        public void Broadcast(string text) { }
    }
}
