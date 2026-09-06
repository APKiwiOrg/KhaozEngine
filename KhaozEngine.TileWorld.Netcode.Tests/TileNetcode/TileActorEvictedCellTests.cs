using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// An actor is marked <see cref="TransientScope.DurableOnly"/>, so a cell eviction FREEZES it rather than ending
/// it and a route back to that coordinate hands the same entity back under the same net id. Both actor passes that
/// ask which cell owns an id resolve through <c>ShardHost.TryGetOwner</c>, which answers for LIVE cells only, so
/// until this shipped a frozen actor was invisible to the per-cell cap and unreachable by a despawn that still
/// answered true.
/// </summary>
public class TileActorEvictedCellTests
{
    static readonly TileActorSpawn Rat = new(MaxHealth: 30, AttackTicks: 10, Facing: TileDirection.S);

    static TileWorldServer Server(INetTransport transport, int maxActorsPerCell = 64,
        int maxGroundItemsPerCell = 256)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        return new TileWorldServer(transport,
            TileWorldServerTickTests.Config(new TileCoord(10, 10, 0)) with
            {
                MaxActorsPerCell = maxActorsPerCell,
                MaxGroundItemsPerCell = maxGroundItemsPerCell,
            },
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
    }

    // A miniature evictor: freeze a cell's owned entities as it is removed, hand them back when the coordinate is
    // instantiated again. That is exactly the shape KhaozEngine.NetWorld.CellEvictor has, and this package is a
    // SIBLING of that stack rather than a dependent of it, so the test builds the half it needs instead.
    sealed class FreezeCache
    {
        static readonly HashSet<long> Nothing = new();
        readonly Dictionary<CellCoord, byte[]> frozen = new();

        public FreezeCache(ShardHost host)
        {
            host.CellRemoved += cell => frozen[cell.Coord] = cell.SnapshotOwned(Nothing, SnapshotPurpose.Eviction);
            host.CellCreated += cell =>
            {
                if (frozen.Remove(cell.Coord, out byte[]? bytes)) Assert.True(cell.TryRestoreOwned(bytes).Ok);
            };
        }

        public int FrozenCells => frozen.Count;
    }

    // The cap is what stops a spawner overfilling one cell, and it counted the live cells only. An actor waiting in
    // a freeze does not stop existing, so a spawn admitted over it puts a second actor on a cell that was already
    // full the moment the coordinate is re-entered, which the spawn itself is what does.
    [Fact]
    public void The_per_cell_cap_counts_an_actor_frozen_in_an_evicted_cell()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, maxActorsPerCell: 1);
        var freeze = new FreezeCache(s.Host);
        var home = new TileCoord(12, 12, 0);
        Assert.True(s.SpawnActor(home, Rat) > 0);
        Assert.True(s.Host.RemoveCell(TileCells.CoordOf(home)));
        Assert.Equal(1, freeze.FrozenCells);

        long second = s.SpawnActor(new TileCoord(13, 12, 0), Rat);

        Assert.Equal(0L, second);
        Assert.Equal(1, s.RefusedActorSpawnCount);
        Assert.Equal(1, s.ActorCount);
    }

    // The despawn drops every index keyed on the net id and then removes the entity, and the removal was skipped
    // silently for an entity no live cell owned. The answer was still true, so the caller was told a body was gone
    // that the next visit to that coordinate hands straight back: an actor with the movement components on it that
    // nothing on the server indexes, stepped forever and served to everyone standing near it.
    [Fact]
    public void A_despawn_reaches_an_actor_frozen_in_an_evicted_cell()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server);
        _ = new FreezeCache(s.Host);
        var home = new TileCoord(12, 12, 0);
        long actor = s.SpawnActor(home, Rat);
        CellCoord coord = TileCells.CoordOf(home);
        Assert.True(s.Host.RemoveCell(coord));

        Assert.True(s.DespawnActor(actor));

        Assert.Equal(0, s.ActorCount);
        // The route back, which is what a player walking into the region does. Nothing comes with it.
        CellSim back = s.Host.EnsureCell(coord);
        Assert.Equal(0, back.OwnedCount);
        Assert.False(s.Host.TryGetOwner(actor, out _, out _));
    }

    [Fact]
    public void The_per_cell_cap_counts_a_ground_item_frozen_in_an_evicted_cell()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, maxGroundItemsPerCell: 1);
        var freeze = new FreezeCache(s.Host);
        var home = new TileCoord(12, 12, 0);
        long first = s.SpawnGroundItem(home, itemId: 7, count: 1, ttlTicks: 100);
        Assert.NotEqual(0L, first);
        Assert.True(s.Host.RemoveCell(TileCells.CoordOf(home)));
        Assert.Equal(1, freeze.FrozenCells);

        long second = s.SpawnGroundItem(new TileCoord(13, 12, 0), itemId: 8, count: 1, ttlTicks: 100);

        Assert.Equal(0L, second);
        Assert.Equal(1, s.RefusedGroundItemSpawnCount);
        Assert.Equal(1, s.GroundItemCount);
        Assert.True(s.TryGetGroundItem(first, out TileGroundItem restored));
        Assert.Equal(7, restored.ItemId);
    }

    [Fact]
    public void An_evicted_spawner_retires_its_restored_actor_before_replacing_it()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub.Server, maxActorsPerCell: 1);
        _ = new FreezeCache(s.Host);
        var home = new TileCoord(12, 12, 0);
        TileActorSpawner spawner = s.Actors.Add(new TileActorDefinition
        {
            Id = "rat",
            MaxHealth = 30,
            AttackTicks = 10,
            RespawnDelayTicks = 2,
        }, home);
        s.Tick(0.25f);
        long first = spawner.ActorNetId;
        Assert.NotEqual(0L, first);
        Assert.True(s.Host.RemoveCell(TileCells.CoordOf(home)));

        s.Tick(0.25f);
        Assert.Equal(TileActorSpawnerState.Waiting, spawner.State);
        Assert.Equal(2, spawner.TicksUntilRespawn);
        s.Tick(0.25f);
        s.Tick(0.25f);

        long replacement = spawner.ActorNetId;
        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
        Assert.NotEqual(0L, replacement);
        Assert.NotEqual(first, replacement);
        Assert.Equal(1, s.ActorCount);
        Assert.False(s.Host.TryGetOwner(first, out _, out _));
        Assert.True(s.Actors.TryGetSpawnerOf(replacement, out TileActorSpawner linked));
        Assert.Same(spawner, linked);

        for (int i = 0; i < 4; i++) s.Tick(0.25f);
        Assert.Equal(TileActorSpawnerState.Alive, spawner.State);
        Assert.Equal(replacement, spawner.ActorNetId);
        Assert.Equal(1, s.ActorCount);
    }
}
