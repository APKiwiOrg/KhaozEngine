using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileInterestSlotsTests
{
    const float Tick = 0.25f;

    [Fact]
    public void Slots_match_replication_interest_at_boundary_and_plane()
    {
        using var h = new InterestHarness(radius: 35f);
        int source = h.Join("source", new TileCoord(0, 0, 0));
        int edge = h.Join("edge", new TileCoord(35, 0, 0));
        _ = h.Join("outside", new TileCoord(35, 1, 0));
        _ = h.Join("upstairs", new TileCoord(0, 0, 1));
        var slots = new List<int> { 999 };

        int count = h.Server.CollectInterestSlots(source, slots);

        Assert.Equal(2, count);
        Assert.Equal(new[] { source, edge }.Order(), slots);
    }

    [Fact]
    public void Slots_cross_a_region_edge_and_replace_the_callers_content_in_slot_order()
    {
        using var h = new InterestHarness(35f, new RegionCoord(0, 0), new RegionCoord(1, 0));
        int source = h.Join(6, "source", new TileCoord(63, 10, 0));
        int across = h.Join(2, "across", new TileCoord(64, 10, 0));
        var slots = new List<int> { 999, 998 };

        int count = h.Server.CollectInterestSlots(source, slots);

        Assert.Equal(2, count);
        Assert.Equal(new[] { across, source }, slots);
    }

    [Fact]
    public void Display_name_comes_from_the_live_identity_and_an_empty_slot_has_none()
    {
        using var h = new InterestHarness(radius: 35f);
        int slot = h.Join("Ari", new TileCoord(10, 10, 0));
        Assert.True(h.Server.TryGetPlayerNetId(slot, out long netId));
        Assert.True(h.Server.Host.TryGetOwner(netId, out CellSim cell, out Entity entity));
        cell.World.Set(entity, new TileIdentity { DisplayName = "Verified Ari" });

        Assert.True(h.Server.TryGetPlayerDisplayName(slot, out string displayName));
        Assert.Equal("Verified Ari", displayName);
        Assert.False(h.Server.TryGetPlayerDisplayName(7, out string missing));
        Assert.Equal(string.Empty, missing);
    }

    [Fact]
    public void An_empty_source_clears_the_destination_and_returns_zero()
    {
        using var h = new InterestHarness(radius: 35f);
        var slots = new List<int> { 999 };

        int count = h.Server.CollectInterestSlots(7, slots);

        Assert.Equal(0, count);
        Assert.Empty(slots);
    }

    sealed class InterestHarness : IDisposable
    {
        readonly InMemoryTransportHub hub = new();
        int nextSlot;

        public InterestHarness(float radius, params RegionCoord[] regions)
        {
            TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, regions);
            TileWorldServerConfig config = TileWorldServerTickTests.Config(new TileCoord(0, 0, 0)) with
            {
                InterestRadius = radius,
                OverlapMargin = radius,
            };
            Server = new TileWorldServer(hub.Server, config, TileMoveSimulatorTests.Bake(doc),
                new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        }

        public TileWorldServer Server { get; }

        public int Join(string displayName, TileCoord tile) => Join(nextSlot++, displayName, tile);

        public int Join(int slot, string displayName, TileCoord tile)
        {
            Server.SpawnPlayer(slot, $"account-{displayName}", displayName);
            Server.SetPlayerState(slot, TileMoveState.At(tile, TileDirection.S));
            Server.Tick(Tick);
            return slot;
        }

        public void Dispose() => Server.Dispose();
    }
}
