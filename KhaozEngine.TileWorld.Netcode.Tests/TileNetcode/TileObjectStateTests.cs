using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// Object states end to end: the codec, the server's set, clock and clear, the two seams that decide whether a
/// state is SERVED at all (the interest grid and the per-viewer plane filter), and the state arriving on a real
/// client through the wire with its events.
/// <para>The two serve seams carry their own tests rather than riding the wire ones, because that is the failure
/// this component was designed against: a replicated component encodes and decodes perfectly, every codec test
/// passes, and no client is ever shown it.</para>
/// </summary>
public class TileObjectStateTests
{
    const float Tick = 0.25f;
    const float Frame = 0.05f;

    // The loopback pair, TileGroundItemsTests' harness verbatim: a joined client whose polls and presentation
    // run against a server ticking on its own accumulator. A copy rather than a shared helper, because the two
    // classes run in parallel and each wants its own transport.
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
    public void A_state_round_trips_through_the_registry_verbatim()
    {
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        var sent = new TileObjectState { ObjectId = 4_100_200_300L, State = -7, X = 12, Z = 9, Plane = 2 };

        var server = new World();
        Entity spawned = server.Spawn();
        server.Set(spawned, new NetId(1));
        server.Set(spawned, sent);
        byte[] snap = SnapshotWriter.WriteFiltered(server, reg, new HashSet<long> { 1 },
            ReplicationChannels.Replicate, ownerNetId: 1);

        var client = new World();
        new ClientReplicationView(reg).Apply(client, snap);
        Entity e = client.Query().With<TileObjectState>().Entities().Single();
        TileObjectState got = client.Get<TileObjectState>(e);

        // Verbatim, every field, INCLUDING a negative state: the engine assigns State no meaning, so it has
        // nothing to clamp it to and a codec that quietly normalized it would be inventing one.
        Assert.Equal(sent.ObjectId, got.ObjectId);
        Assert.Equal(sent.State, got.State);
        Assert.Equal(new TileCoord(12, 9, 2), got.Tile);
    }

    [Fact]
    public void A_state_appears_on_the_client_and_leaves_when_the_server_clears_it()
    {
        using var pair = new Pair(new TileCoord(10, 10, 0));
        var states = new List<(long ObjectId, int State)>();
        pair.Frames(8);
        pair.Client.CollectObjectStates(states);
        Assert.Empty(states);

        pair.Server.SetObjectState(objectId: 412, state: 1, new TileCoord(12, 9, 0));
        Assert.Equal(1, pair.Server.ObjectStateCount);
        Assert.True(pair.Server.TryGetObjectState(412, out int held));
        Assert.Equal(1, held);

        pair.Frames(8);
        pair.Client.CollectObjectStates(states);
        (long seenId, int seenState) = Assert.Single(states);
        Assert.Equal(412L, seenId);
        Assert.Equal(1, seenState);
        Assert.True(pair.Client.TryGetObjectState(412, out int mirrored));
        Assert.Equal(1, mirrored);

        Assert.True(pair.Server.ClearObjectState(412));
        Assert.False(pair.Server.ClearObjectState(412));   // idempotent by answer, not by silence
        pair.Frames(8);
        pair.Client.CollectObjectStates(states);
        Assert.Empty(states);
        Assert.False(pair.Client.TryGetObjectState(412, out _));
    }

    [Fact]
    public void A_second_set_updates_the_state_in_place_and_keeps_the_entity()
    {
        using var pair = new Pair(new TileCoord(10, 10, 0));
        pair.Frames(4);
        long first = pair.Server.SetObjectState(412, 1, new TileCoord(12, 9, 0));
        long second = pair.Server.SetObjectState(412, 2, new TileCoord(12, 9, 0));

        // Same entity, so a viewer sees a value change rather than a despawn and a respawn.
        Assert.Equal(first, second);
        Assert.Equal(1, pair.Server.ObjectStateCount);
        Assert.True(pair.Server.TryGetObjectState(412, out int held));
        Assert.Equal(2, held);

        pair.Frames(8);
        Assert.True(pair.Client.TryGetObjectState(412, out int mirrored));
        Assert.Equal(2, mirrored);
    }

    [Fact]
    public void The_clock_clears_a_state_on_both_heads_and_says_so_once()
    {
        using var pair = new Pair(new TileCoord(10, 10, 0));
        pair.Frames(4);
        pair.Server.SetObjectState(412, 1, new TileCoord(10, 11, 0), ttlTicks: 6);

        var expired = new List<long>();
        pair.Server.OnObjectStateExpired += id => expired.Add(id);
        // 6 ticks is 30 frames at this cadence; run well past it and the state must be gone everywhere.
        pair.Frames(40);

        Assert.Equal(0, pair.Server.ObjectStateCount);
        Assert.False(pair.Server.TryGetObjectState(412, out _));
        Assert.Equal([412L], expired);
        var states = new List<(long ObjectId, int State)>();
        pair.Client.CollectObjectStates(states);
        Assert.Empty(states);

        // The deliberate clear's silence: a state a game reverted is not an expiry.
        pair.Server.SetObjectState(999, 3, new TileCoord(10, 11, 0));
        Assert.True(pair.Server.ClearObjectState(999));
        pair.Frames(8);
        Assert.Equal([412L], expired);
    }

    [Fact]
    public void A_ttl_of_zero_is_no_clock_at_all_and_a_negative_one_throws()
    {
        using var pair = new Pair(new TileCoord(10, 10, 0));
        pair.Frames(4);
        pair.Server.SetObjectState(412, 1, new TileCoord(10, 11, 0));
        pair.Frames(60);
        Assert.True(pair.Server.TryGetObjectState(412, out _));

        // Re-arming with 0 DISARMS a clock a previous call set, rather than expiring on the next sweep.
        pair.Server.SetObjectState(500, 1, new TileCoord(10, 11, 0), ttlTicks: 4);
        pair.Server.SetObjectState(500, 1, new TileCoord(10, 11, 0), ttlTicks: 0);
        pair.Frames(60);
        Assert.True(pair.Server.TryGetObjectState(500, out _));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            pair.Server.SetObjectState(1, 1, new TileCoord(10, 10, 0), ttlTicks: -1));
        // The caller-bug shape the drop door already refuses: a plane the world does not have.
        Assert.ThrowsAny<ArgumentException>(() =>
            pair.Server.SetObjectState(1, 1, new TileCoord(10, 10, 99)));
    }

    [Fact]
    public void A_state_is_served_only_to_a_viewer_in_range_of_it()
    {
        // The interest grid reads a state's position off its own component (a state has no move state to answer
        // from), so without that branch the entity has no position, never enters the grid, and reaches no client
        // at all while every codec test above still passes. The internal serve seam, the drop test's approach.
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(),
            hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        long near = s.SetObjectState(412, 1, new TileCoord(12, 10, 0));
        // Interest is 15 tiles, so 40 east of the viewer is comfortably outside it and inside the same region.
        long far = s.SetObjectState(413, 1, new TileCoord(50, 10, 0));
        s.Tick(0.25f);

        HashSet<long> seen = s.ServeInterest(0);
        Assert.Contains(near, seen);
        Assert.DoesNotContain(far, seen);
    }

    [Fact]
    public void A_state_is_served_on_its_own_plane_alone()
    {
        // The serve's plane filter reads a state's plane off its own component, so a stump upstairs is not drawn
        // through the floor onto a player standing below it.
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(),
            hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        long beside = s.SetObjectState(412, 1, new TileCoord(12, 10, 0));
        long upstairs = s.SetObjectState(413, 1, new TileCoord(12, 10, 1));
        s.Tick(0.25f);

        HashSet<long> seen = s.ServeInterest(0);
        Assert.Contains(beside, seen);
        Assert.DoesNotContain(upstairs, seen);
    }

    [Fact]
    public void The_client_raises_one_event_per_change_and_one_when_a_state_goes()
    {
        using var pair = new Pair(new TileCoord(10, 10, 0));
        var changed = new List<(long ObjectId, int State)>();
        var cleared = new List<long>();
        pair.Client.ObjectStateChanged += (id, state) => changed.Add((id, state));
        pair.Client.ObjectStateCleared += id => cleared.Add(id);

        pair.Frames(8);
        Assert.Empty(changed);

        pair.Server.SetObjectState(412, 1, new TileCoord(12, 9, 0));
        // Many snapshots carrying the SAME state. Once per change is the contract, not once per snapshot: a
        // renderer swapping a mesh out of this event would otherwise rebuild a prop four times a second forever.
        pair.Frames(20);
        Assert.Equal([(412L, 1)], changed);
        Assert.Empty(cleared);

        pair.Server.SetObjectState(412, 2, new TileCoord(12, 9, 0));
        pair.Frames(20);
        Assert.Equal([(412L, 1), (412L, 2)], changed);
        Assert.Empty(cleared);

        pair.Server.ClearObjectState(412);
        pair.Frames(20);
        Assert.Equal([(412L, 1), (412L, 2)], changed);
        Assert.Equal([412L], cleared);
    }
}

/// <summary>The allocation half, in its own class so it can enlist in the serial collection without dragging the
/// wire tests into it. See <c>AllocSensitiveCollection</c>: a per-thread allocation measurement is only honest
/// while nothing else in the assembly is churning the GC on another thread.</summary>
[Collection("AllocSensitive")]
public class TileObjectStateAllocationTests
{
    const float Tick = 0.25f;
    const float Frame = 0.05f;

    [Fact]
    public void Collecting_the_states_allocates_nothing_once_the_buffer_has_grown()
    {
        var hub = new InMemoryTransportHub();
        var spawn = new TileCoord(10, 10, 0);
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var server = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(spawn),
            TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        using var client = new TileWorldClient(hub.CreateClient(), new TileWorldClientConfig
        {
            TickSeconds = Tick,
            StepTicks = new TileStepTicks(walk: 4, run: 2),
        }, TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));
        client.Tick(0.13f);
        client.Poll();

        // A block around the spawn, every tile of it inside the 15 tile interest radius, so the mirror the
        // collect walks is filled through the real wire rather than by writing the world by hand.
        int placed = 0;
        for (int dz = -2; dz <= 3; dz++)
            for (int dx = -2; dx <= 3; dx++)
                server.SetObjectState(400 + placed++, 1, new TileCoord(spawn.X + dx, spawn.Z + dz, 0));

        float accum = 0f;
        for (int i = 0; i < 12; i++)
        {
            client.Tick(Frame);
            server.Poll();
            accum += Frame;
            while (accum >= Tick) { accum -= Tick; server.Tick(Tick); }
            client.Poll();
            client.AdvancePresentation(Frame);
        }
        Assert.Equal(placed, client.ObjectStateCount);

        // Warm up: the buffer grows to fit, the dictionary enumerator is a struct, and the entries are value
        // tuples, so a warm call has nothing left to allocate.
        var into = new List<(long ObjectId, int State)>();
        for (int i = 0; i < 8; i++) client.CollectObjectStates(into);
        Assert.Equal(placed, into.Count);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) client.CollectObjectStates(into);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before);
    }
}
