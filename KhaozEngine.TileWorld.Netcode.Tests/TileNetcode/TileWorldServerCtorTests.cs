using System;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The constructor refusals that keep the server's two doors one door. <c>TileWorldPersistence</c> validates a stored
/// plane against the collision MAP's count and <c>SetPlayerState</c> against the CONFIG's, so a head that bakes more
/// planes than it configures would admit a record at the binding that the door still throws on, out of
/// <c>Update(dt)</c> on a live server. The constructor is where that mismatch is cheapest to refuse.
/// </summary>
public class TileWorldServerCtorTests
{
    [Fact]
    public void A_plane_count_that_disagrees_with_the_baked_map_is_refused_at_construction()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0));
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var hub = new InMemoryTransportHub();
        TileWorldServerConfig mismatched = TileWorldServerTickTests.Config(new TileCoord(1, 1, 0)) with { PlaneCount = 3 };

        ArgumentException refused = Assert.Throws<ArgumentException>(() => new TileWorldServer(hub.Server, mismatched, map,
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator()));

        Assert.Equal("config", refused.ParamName);
        Assert.Contains("PlaneCount 3", refused.Message);
        Assert.Contains("4 planes", refused.Message);
    }

    [Fact]
    public void A_plane_count_that_matches_the_baked_map_constructs()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0));
        var hub = new InMemoryTransportHub();

        using var server = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(new TileCoord(1, 1, 0)),
            TileMoveSimulatorTests.Bake(doc), new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs),
            new AllowAllAuthenticator());

        Assert.Equal(0, server.TickCount);
    }
}
