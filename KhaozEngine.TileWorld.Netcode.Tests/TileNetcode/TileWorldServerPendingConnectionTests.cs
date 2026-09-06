using System;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileWorldServerPendingConnectionTests
{
    [Fact]
    public void A_pending_connection_flood_stays_within_the_configured_cap_and_is_counted()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = CreateServer(hub, maxPendingConnections: 2);
        for (int i = 0; i < 7; i++) _ = hub.CreateClient();

        server.Poll();

        Assert.Equal(2, server.PendingConnectionCount);
        Assert.Equal(5, server.RefusedPendingConnectionCount);
    }

    [Fact]
    public void A_same_poll_hello_from_a_cap_rejected_connection_never_joins_the_tile_world()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = CreateServer(hub, maxPendingConnections: 1);
        using INetTransport holderTransport = hub.CreateClient();
        using INetTransport excessTransport = hub.CreateClient();
        var holder = new NetClient(holderTransport);
        var excess = new NetClient(excessTransport);
        int joinCount = 0;
        server.PlayerJoined += (_, _) => joinCount++;
        excess.Poll();

        server.Poll();

        Assert.Equal(0, joinCount);
        Assert.Equal(0, server.PlayerCount);
        Assert.Equal(1, server.PendingConnectionCount);
        Assert.Equal(1, server.RefusedPendingConnectionCount);

        holder.Poll();
        server.Poll();

        Assert.Equal(1, joinCount);
        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public void Pending_connections_remain_unlimited_by_default()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = CreateServer(hub);
        for (int i = 0; i < 200; i++) _ = hub.CreateClient();

        server.Poll();

        Assert.Equal(200, server.PendingConnectionCount);
        Assert.Equal(0, server.RefusedPendingConnectionCount);
    }

    [Fact]
    public void A_negative_pending_connection_cap_is_rejected_through_the_tile_server()
    {
        var hub = new InMemoryTransportHub();

        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateServer(hub, maxPendingConnections: -1));

        Assert.Equal("maxPendingConnections", refused.ParamName);
    }

    private static TileWorldServer CreateServer(InMemoryTransportHub hub, int? maxPendingConnections = null)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileWorldServerConfig config = TileWorldServerTickTests.Config(new TileCoord(1, 1, 0));
        if (maxPendingConnections.HasValue)
            config = config with { MaxPendingConnections = maxPendingConnections.Value };
        return new TileWorldServer(hub.Server, config, TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
    }
}
