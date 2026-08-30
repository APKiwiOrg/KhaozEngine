using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// Ground items end to end: the server's spawn, clock and despawn, and the drop arriving on a real client
/// through the wire. The engine owns the lifecycle and nothing else, so what these pin is existence: a drop
/// exists where it was dropped, for as long as its clock says, on both heads, and stops existing exactly once.
/// </summary>
public class TileGroundItemsTests
{
    const float Tick = 0.25f;
    const float Frame = 0.05f;

    // The loopback pair, TileWorldClientLoopbackTests' harness distilled to what a drop needs: a joined
    // client whose polls and presentation run against a server ticking on its own accumulator.
    sealed class Pair : IDisposable
    {
        public readonly TileWorldServer Server;
        public readonly TileWorldClient Client;
        readonly InMemoryTransportHub hub;
        float serverAccum;

        public Pair(TileCoord spawn)
        {
            TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
            hub = new InMemoryTransportHub();
            Server = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(spawn),
                TileMoveSimulatorTests.Bake(doc),
                new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
            Client = new TileWorldClient(hub.CreateClient(), new TileWorldClientConfig
            {
                TickSeconds = Tick,
                StepTicks = new TileStepTicks(walk: 4, run: 2),
            }, TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));
            Client.Tick(0.13f);
            Client.Poll();
        }

        public void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Client.Tick(Frame);
                Server.Poll();
                serverAccum += Frame;
                while (serverAccum >= Tick)
                {
                    serverAccum -= Tick;
                    Server.Tick(Tick);
                }
                Client.Poll();
                Client.AdvancePresentation(Frame);
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }

    [Fact]
    public void ADropExistsOnBothHeadsAndCarriesItsPayloadVerbatim()
    {
        using var pair = new Pair(new TileCoord(10, 10, 0));
        var drops = new List<(long NetId, TileGroundItem Item)>();
        pair.Frames(8);
        pair.Client.CollectGroundItems(drops);
        Assert.Empty(drops);

        long netId = pair.Server.SpawnGroundItem(new TileCoord(12, 9, 0), itemId: 7, count: 25,
            ttlTicks: 1000);
        Assert.NotEqual(0L, netId);
        Assert.Equal(1, pair.Server.GroundItemCount);
        Assert.True(pair.Server.TryGetGroundItem(netId, out TileGroundItem held));
        Assert.Equal(7, held.ItemId);
        Assert.Equal(25, held.Count);
        Assert.Equal(new TileCoord(12, 9, 0), held.Tile);

        pair.Frames(8);
        pair.Client.CollectGroundItems(drops);
        (long seenId, TileGroundItem seen) = Assert.Single(drops);
        Assert.Equal(netId, seenId);
        Assert.Equal(7, seen.ItemId);
        Assert.Equal(25, seen.Count);
        Assert.Equal(new TileCoord(12, 9, 0), seen.Tile);
    }

    [Fact]
    public void TheClockDespawnsADropOnBothHeadsAndSaysSoOnce()
    {
        using var pair = new Pair(new TileCoord(10, 10, 0));
        pair.Frames(4);
        long netId = pair.Server.SpawnGroundItem(new TileCoord(10, 11, 0), itemId: 1, count: 1, ttlTicks: 6);

        var expired = new List<long>();
        pair.Server.OnGroundItemExpired += id => expired.Add(id);
        // 6 ticks is 30 frames at this cadence; run well past it and the drop must be gone everywhere.
        pair.Frames(40);

        Assert.Equal(0, pair.Server.GroundItemCount);
        Assert.False(pair.Server.TryGetGroundItem(netId, out _));
        Assert.Equal([netId], expired);
        var drops = new List<(long NetId, TileGroundItem Item)>();
        pair.Client.CollectGroundItems(drops);
        Assert.Empty(drops);

        // The deliberate despawn's answer contract, and its silence: a taken drop is not an expiry.
        long second = pair.Server.SpawnGroundItem(new TileCoord(10, 11, 0), itemId: 2, count: 3,
            ttlTicks: 1000);
        Assert.True(pair.Server.DespawnGroundItem(second));
        Assert.False(pair.Server.DespawnGroundItem(second));
        pair.Frames(8);
        Assert.Equal([netId], expired);
    }

    [Fact]
    public void ADropIsServedOnItsOwnPlaneAlone()
    {
        // The serve's plane filter reads a drop's plane off its own component (a drop has no move state to
        // answer from), so a stack upstairs is not drawn through the floor. The internal serve seam, the
        // plane-filter test's own approach: the wire adds nothing to what interest decides here.
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(),
            hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        long beside = s.SpawnGroundItem(new TileCoord(12, 10, 0), 1, 1, 1000);
        long upstairs = s.SpawnGroundItem(new TileCoord(12, 10, 1), 1, 1, 1000);
        s.Tick(0.25f);

        HashSet<long> seen = s.ServeInterest(0);
        Assert.Contains(beside, seen);
        Assert.DoesNotContain(upstairs, seen);
    }

    [Fact]
    public void AFullCellRefusesCountablyAndBadSpawnsThrow()
    {
        using var pair = new Pair(new TileCoord(10, 10, 0));
        int budget = TileWorldServerTickTests.Config(new TileCoord(10, 10, 0)).MaxGroundItemsPerCell;
        for (int i = 0; i < budget; i++)
            Assert.NotEqual(0L, pair.Server.SpawnGroundItem(new TileCoord(11, 11, 0), 1, 1, 1000));

        Assert.Equal(0L, pair.Server.SpawnGroundItem(new TileCoord(11, 11, 0), 1, 1, 1000));
        Assert.Equal(1, pair.Server.RefusedGroundItemSpawnCount);

        // The caller-bug shapes throw, SpawnActor's split: a malformed placement or payload is a stack
        // trace, never a refusal a tick has to survive.
        Assert.ThrowsAny<ArgumentException>(() =>
            pair.Server.SpawnGroundItem(new TileCoord(10, 10, 99), 1, 1, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            pair.Server.SpawnGroundItem(new TileCoord(10, 10, 0), 1, 0, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            pair.Server.SpawnGroundItem(new TileCoord(10, 10, 0), 1, 1, 0));
    }
}
