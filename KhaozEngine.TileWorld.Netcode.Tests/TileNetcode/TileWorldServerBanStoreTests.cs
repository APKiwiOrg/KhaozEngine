using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// <see cref="TileWorldServerConfig.BanStore"/>: the tile server takes the same <see cref="IBanStore"/> a
/// <c>WorldServer</c> takes as <c>banStore:</c>, and reads it at the door. The type is named under
/// <c>KhaozEngine.NetWorld</c> but lives in <c>KhaozEngine.Netcode</c>, which is why this suite can use it while
/// <c>ArchitectureTests.TileWorldNetcode_NeverReferencesNetWorld</c> still holds for the package and for this project.
/// </summary>
public class TileWorldServerBanStoreTests
{
    static TileWorldServer Server(InMemoryTransportHub hub, IBanStore bans) =>
        new(hub.Server, TileWorldServerTickTests.Config(new TileCoord(4, 4, 0)) with { BanStore = bans },
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));

    // One connect attempt pumped to its answer: null when the server admitted it, else the refusal token.
    static string? Connect(InMemoryTransportHub hub, TileWorldServer server, string subject)
    {
        var net = new NetClient(hub.CreateClient(), Encoding.UTF8.GetBytes(subject));
        net.Poll();
        server.Poll();
        net.Poll();
        string? refused = null;
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
            if (ev.Kind == ClientSessionEventKind.Rejected) refused = ev.RejectReason;
        return refused;
    }

    [Fact]
    public async Task A_subject_banned_in_the_store_is_refused_at_the_door_with_the_ban_token()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("acct-banned", "cheating");
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub, bans);

        Assert.Equal(HandshakeToken.BannedReason, Connect(hub, server, "acct-banned"));
        Assert.Equal(0, server.PlayerCount);

        Assert.Null(Connect(hub, server, "acct-ok"));
        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public async Task The_store_is_read_live_so_a_ban_recorded_after_boot_refuses_the_next_connect()
    {
        var bans = new InMemoryBanStore();
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = Server(hub, bans);

        await bans.BanAsync("acct-late", "griefing");
        Assert.Equal(HandshakeToken.BannedReason, Connect(hub, server, "acct-late"));

        await bans.UnbanAsync("acct-late");
        Assert.Null(Connect(hub, server, "acct-late"));
        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public async Task The_store_and_the_predicate_compose_and_either_one_refuses()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("acct-store", "x");
        var hub = new InMemoryTransportHub();
        using var server = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(4, 4, 0)) with
            {
                BanStore = bans,
                IsBanned = subject => subject == "acct-predicate",
            },
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));

        Assert.Equal(HandshakeToken.BannedReason, Connect(hub, server, "acct-store"));
        Assert.Equal(HandshakeToken.BannedReason, Connect(hub, server, "acct-predicate"));
        Assert.Null(Connect(hub, server, "acct-ok"));
        Assert.Equal(1, server.PlayerCount);
    }
}
