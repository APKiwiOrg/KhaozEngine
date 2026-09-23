using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The SIBLING instance component on a drop: its codec, the cap, the spawn overload that seats it, and the
/// compatibility claim the sibling shape was chosen for.
/// <para>The third fact is the one the design rests on rather than a nicety. A sibling was picked over a widened
/// <see cref="TileGroundItem"/> because that component writes twenty bytes with no declared length, so a client
/// built against today's protocol would misparse the rest of the entity if a field were added to it. A new
/// extension id is length prefixed, so a registry that never registered it skips it. That is a test here, not a
/// paragraph in a design doc.</para>
/// </summary>
public class TileGroundItemInstanceTests
{
    const float Tick = 0.25f;
    const float Frame = 0.05f;

    // The loopback pair, TileGroundItemsTests' harness verbatim: a joined client whose polls and presentation run
    // against a server ticking on its own accumulator. A copy rather than a shared helper, because the two classes
    // run in parallel and each wants its own transport.
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
    public void A_drop_with_no_instance_seats_NO_sibling_component_and_costs_no_wire_bytes()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(),
            hub.Server, new TileCoord(10, 10, 0));
        long netId = s.SpawnGroundItem(new TileCoord(12, 9, 0), itemId: 7, count: 25, ttlTicks: 1000);
        Assert.True(s.TryGetGroundItem(netId, out _));
        Assert.False(s.TryGetGroundItemInstance(netId, out TileGroundItemInstance absent));
        Assert.Equal(0L, absent.InstanceId);

        // And nothing on the wire: the whole point of a sibling over a widened component is that the drop
        // almost every kill leaves behind pays nothing for a feature it does not use. 4 count + 8 net id +
        // (2 type id + 1 length + 20 data) + 2 terminator.
        var world = new World();
        Entity e = world.Spawn();
        world.Set(e, new NetId(1));
        world.Set(e, new TileGroundItem { ItemId = 7, Count = 25, X = 12, Z = 9, Plane = 0 });
        byte[] snap = SnapshotWriter.WriteFiltered(world, TileProtocol.CreateRegistry(),
            new HashSet<long> { 1 }, ReplicationChannels.Replicate, ownerNetId: 1);
        Assert.Equal(37, snap.Length);
        Assert.Empty(FramedPayload(snap, TileProtocol.TileGroundItemInstanceTypeId));
    }

    [Fact]
    public void A_drop_with_an_instance_round_trips_the_id_and_the_payload()
    {
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        byte[] payload = [0x01, 0x02, 0xFE, 0xFF, 0x00, 0x7F];
        var sent = new TileGroundItemInstance { InstanceId = 4_100_200_300_400L, Payload = payload };

        var server = new World();
        Entity spawned = server.Spawn();
        server.Set(spawned, new NetId(1));
        server.Set(spawned, new TileGroundItem { ItemId = 7, Count = 1, X = 12, Z = 9, Plane = 0 });
        server.Set(spawned, sent);
        byte[] snap = SnapshotWriter.WriteFiltered(server, reg, new HashSet<long> { 1 },
            ReplicationChannels.Replicate, ownerNetId: 1);

        var client = new World();
        new ClientReplicationView(reg).Apply(client, snap);
        Entity e = client.Query().With<TileGroundItemInstance>().Entities().Single();
        TileGroundItemInstance got = client.Get<TileGroundItemInstance>(e);

        Assert.Equal(sent.InstanceId, got.InstanceId);
        Assert.Equal(payload, got.Payload);
        // A decoded payload is the reader's OWN array. The engine never decodes these bytes, so the one
        // property it can offer a game is that they arrived verbatim and are not shared with anything.
        Assert.NotSame(payload, got.Payload);
    }

    [Fact]
    public void An_OLD_client_registry_skips_the_sibling_and_still_parses_the_rest_of_the_entity()
    {
        // The writer is today's registry. The reader is a client built BEFORE the sibling existed, so it holds
        // the two ids it always held and has never heard of FirstExtensionTypeId + 8.
        var server = new World();
        Entity drop = server.Spawn();
        server.Set(drop, new NetId(1));
        server.Set(drop, new TileGroundItem { ItemId = 7, Count = 25, X = 12, Z = 9, Plane = 0 });
        server.Set(drop, new TileGroundItemInstance { InstanceId = 909L, Payload = [9, 9, 9, 9, 9] });
        // Registered AFTER the sibling, so it rides BEHIND it on the wire: this is the component whose bytes a
        // mis-skip would eat.
        server.Set(drop, new TileObjectState { ObjectId = 412, State = 3, X = 12, Z = 9, Plane = 0 });
        Entity second = server.Spawn();
        server.Set(second, new NetId(2));
        server.Set(second, new TileGroundItem { ItemId = 8, Count = 1, X = 13, Z = 9, Plane = 0 });
        byte[] snap = SnapshotWriter.WriteFiltered(server, TileProtocol.CreateRegistry(),
            new HashSet<long> { 1, 2 }, ReplicationChannels.Replicate, ownerNetId: 1);

        var client = new World();
        new ClientReplicationView(OldClientRegistry()).Apply(client, snap);

        // The rest of the ENTITY: the component behind the sibling is intact and holds what was sent.
        Entity e = client.Query().With<TileObjectState>().Entities().Single();
        TileObjectState state = client.Get<TileObjectState>(e);
        Assert.Equal(412L, state.ObjectId);
        Assert.Equal(3, state.State);
        TileGroundItem item = client.Get<TileGroundItem>(e);
        Assert.Equal(7, item.ItemId);
        Assert.Equal(25, item.Count);
        // And the rest of the SNAPSHOT: a mis-skip would have eaten the entity terminator and read the next
        // entity's net id out of the middle of a component.
        Assert.Equal(2, client.Query().With<TileGroundItem>().Entities().Count());
    }

    [Fact]
    public void A_declared_length_longer_than_the_frame_answers_a_ZERO_length_payload_not_a_throw()
    {
        // Three bytes behind a length that claims 600. The component's framed payload is the bound, so this is a
        // lie the reader can see, and the answer is an empty payload rather than an exception in the apply loop.
        byte[] body = Body(77L, declaredLength: 600, actual: [1, 2, 3]);
        var client = new World();
        new ClientReplicationView(TileProtocol.CreateRegistry()).Apply(client, Snapshot(netId: 5, body));

        Entity e = client.Query().With<TileGroundItemInstance>().Entities().Single();
        TileGroundItemInstance got = client.Get<TileGroundItemInstance>(e);
        Assert.Equal(77L, got.InstanceId);
        Assert.Empty(got.Payload);
    }

    [Fact]
    public void A_declared_length_above_MaxInstancePayloadBytes_answers_a_zero_length_payload()
    {
        // Every declared byte IS present here, so only the cap refuses it: a peer that sends 513 bytes is over a
        // limit no encoder of ours can reach, and the reader will not allocate to its word.
        byte[] oversize = new byte[TileProtocol.MaxInstancePayloadBytes + 1];
        oversize.AsSpan().Fill(0xAB);
        byte[] body = Body(78L, declaredLength: oversize.Length, actual: oversize);
        var client = new World();
        new ClientReplicationView(TileProtocol.CreateRegistry()).Apply(client, Snapshot(netId: 6, body));

        Entity e = client.Query().With<TileGroundItemInstance>().Entities().Single();
        TileGroundItemInstance got = client.Get<TileGroundItemInstance>(e);
        Assert.Equal(78L, got.InstanceId);
        Assert.Empty(got.Payload);
    }

    [Fact]
    public void SpawnGroundItem_throws_on_a_payload_above_the_cap_and_on_bytes_with_instance_id_0()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(),
            hub.Server, new TileCoord(10, 10, 0));

        // Both are caller bugs in the same class as the existing non-positive count throw, so both get a stack
        // trace rather than a refusal a tick has to survive.
        Assert.Throws<ArgumentOutOfRangeException>(() => s.SpawnGroundItem(new TileCoord(12, 9, 0), 7, 1, 1000,
            instanceId: 55L, payload: new byte[TileProtocol.MaxInstancePayloadBytes + 1]));
        Assert.Throws<ArgumentException>(() => s.SpawnGroundItem(new TileCoord(12, 9, 0), 7, 1, 1000,
            instanceId: 0L, payload: [1, 2, 3]));
        Assert.Equal(0, s.GroundItemCount);

        // The cap itself is reachable: 512 bytes spawn.
        long netId = s.SpawnGroundItem(new TileCoord(12, 9, 0), 7, 1, 1000,
            instanceId: 55L, payload: new byte[TileProtocol.MaxInstancePayloadBytes]);
        Assert.NotEqual(0L, netId);
        Assert.True(s.TryGetGroundItemInstance(netId, out TileGroundItemInstance held));
        Assert.Equal(TileProtocol.MaxInstancePayloadBytes, held.Payload.Length);

        // And an instance with no fields is legal: the id is what is never 0, not the payload.
        long bare = s.SpawnGroundItem(new TileCoord(12, 10, 0), 7, 1, 1000, instanceId: 56L, payload: []);
        Assert.True(s.TryGetGroundItemInstance(bare, out TileGroundItemInstance empty));
        Assert.Equal(56L, empty.InstanceId);
        Assert.Empty(empty.Payload);
    }

    [Fact]
    public void The_existing_SpawnGroundItem_overload_still_compiles_and_seats_instance_id_0()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(),
            hub.Server, new TileCoord(10, 10, 0));

        // The four-argument call is what every existing game already writes, and it delegates: same net id
        // contract, same refusals, and no component at all rather than one carrying 0.
        long netId = s.SpawnGroundItem(new TileCoord(12, 9, 0), 7, 25, 1000);
        Assert.True(s.TryGetGroundItem(netId, out TileGroundItem item));
        Assert.Equal(7, item.ItemId);
        Assert.Equal(25, item.Count);
        Assert.False(s.TryGetGroundItemInstance(netId, out _));

        // And the long form asked for no instance is the same drop by another name, which is what makes the
        // delegation safe to read: a spawn, a net id, and no component.
        long spelled = s.SpawnGroundItem(new TileCoord(10, 10, 0), 7, 25, 1000, instanceId: 0L, payload: []);
        Assert.NotEqual(0L, spelled);
        Assert.True(s.TryGetGroundItem(spelled, out _));
        Assert.False(s.TryGetGroundItemInstance(spelled, out _));

        // The refusals the four-argument overload has always had are the same refusals here, since they are the
        // same body: the delegation cannot have moved one.
        Assert.Throws<ArgumentOutOfRangeException>(() => s.SpawnGroundItem(new TileCoord(10, 10, 0), 1, 0, 1000,
            instanceId: 5L, payload: []));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.SpawnGroundItem(new TileCoord(10, 10, 0), 1, 1, 0,
            instanceId: 5L, payload: []));
        Assert.ThrowsAny<ArgumentException>(() => s.SpawnGroundItem(new TileCoord(10, 10, 99), 1, 1, 1000,
            instanceId: 5L, payload: []));
    }

    [Fact]
    public void The_instance_id_survives_a_drop_and_a_claim_by_a_stranger_and_by_the_dropper()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(),
            hub.Server, new TileCoord(10, 10, 0));
        byte[] payload = [0x10, 0x20, 0x30];
        long dropper = s.SpawnPlayer(0, "a", "Ari");
        long stranger = s.SpawnPlayer(1, "b", "Bo");
        Assert.NotEqual(dropper, stranger);

        // A claim is TryGet plus a Despawn that answers true, and the engine hands the claimant the SAME two
        // reads whoever they are: it has no claimant to special case, which is exactly what stops a
        // drop-and-claim cycle from laundering an instance into a new one.
        foreach (long claimant in new[] { stranger, dropper })
        {
            long netId = s.SpawnGroundItem(new TileCoord(12, 9, 0), 7, 1, 1000, instanceId: 77L, payload: payload);
            Assert.True(s.TryGetGroundItem(netId, out TileGroundItem item));
            Assert.True(s.TryGetGroundItemInstance(netId, out TileGroundItemInstance instance));
            Assert.Equal(7, item.ItemId);
            Assert.Equal(77L, instance.InstanceId);
            Assert.Equal(payload, instance.Payload);
            Assert.NotSame(payload, instance.Payload);   // the server's own copy: a caller cannot reach in later
            Assert.True(s.DespawnGroundItem(netId));

            // And it is gone exactly once, so the second claimant of one drop takes nothing at all.
            Assert.False(s.DespawnGroundItem(netId));
            Assert.False(s.TryGetGroundItemInstance(netId, out _));
            // The claimant's net id is never the drop's, which is the whole reason the component is not
            // registered OwnerOnly: that channel scopes a component to the entity whose net id the viewer owns.
            Assert.NotEqual(claimant, netId);
        }
    }

    [Fact]
    public void A_drop_with_an_instance_reaches_a_real_client_through_the_serve()
    {
        // The codec and the serve are different failures. A component can encode and decode perfectly and still
        // never be shown to anybody, so this rides the real wire and reads the drop off the client's own world.
        using var pair = new Pair(new TileCoord(10, 10, 0));
        pair.Frames(8);
        long netId = pair.Server.SpawnGroundItem(new TileCoord(12, 9, 0), itemId: 7, count: 25, ttlTicks: 1000,
            instanceId: 4_100_200_300_400L, payload: [1, 2, 3, 4]);
        pair.Frames(8);

        Assert.True(pair.Client.View.Entities.TryGetValue(netId, out Entity e));
        Assert.True(pair.Client.World.TryGet(e, out TileGroundItemInstance seen));
        Assert.Equal(4_100_200_300_400L, seen.InstanceId);
        Assert.Equal<byte[]>([1, 2, 3, 4], seen.Payload);

        // Not OwnerOnly: a drop's entity net id is never a viewer's, so registering it that way would hide it
        // from everybody including the player who dropped it. The client above is the only player on this
        // server and its own net id is not the drop's.
        Assert.NotEqual(netId, pair.Client.LocalNetId);
    }

    [Fact]
    public void CollectGroundItemInstances_returns_only_drops_carrying_an_instance_each_paired_with_its_own_drop()
    {
        // A plain drop beside two instanced ones, all on the real wire. Distinct items, counts, ids and payloads,
        // so a pairing that crossed two drops cannot pass by coincidence.
        using var pair = new Pair(new TileCoord(10, 10, 0));
        pair.Frames(8);
        long plain = pair.Server.SpawnGroundItem(new TileCoord(11, 10, 0), itemId: 3, count: 9, ttlTicks: 1000);
        long sword = pair.Server.SpawnGroundItem(new TileCoord(12, 9, 0), itemId: 7, count: 1, ttlTicks: 1000,
            instanceId: 4_100_200_300_400L, payload: [1, 2, 3]);
        long ring = pair.Server.SpawnGroundItem(new TileCoord(9, 11, 0), itemId: 8, count: 2, ttlTicks: 1000,
            instanceId: 77L, payload: [9]);
        pair.Frames(8);

        var drops = new List<(long NetId, TileGroundItem Item)>();
        pair.Client.CollectGroundItems(drops);
        Assert.Equal(3, drops.Count);   // the plain drop reached this client, so its absence below is the filter

        var instanced = new List<(long NetId, TileGroundItem Item, TileGroundItemInstance Instance)>();
        pair.Client.CollectGroundItemInstances(instanced);

        Assert.Equal(2, instanced.Count);
        Assert.DoesNotContain(instanced, entry => entry.NetId == plain);
        var byId = instanced.ToDictionary(entry => entry.NetId);   // unsorted by contract, so read by net id

        (_, TileGroundItem swordItem, TileGroundItemInstance swordInstance) = byId[sword];
        Assert.Equal(7, swordItem.ItemId);
        Assert.Equal(1, swordItem.Count);
        Assert.Equal(new TileCoord(12, 9, 0), swordItem.Tile);
        Assert.Equal(4_100_200_300_400L, swordInstance.InstanceId);
        Assert.Equal<byte[]>([1, 2, 3], swordInstance.Payload);

        (_, TileGroundItem ringItem, TileGroundItemInstance ringInstance) = byId[ring];
        Assert.Equal(8, ringItem.ItemId);
        Assert.Equal(2, ringItem.Count);
        Assert.Equal(new TileCoord(9, 11, 0), ringItem.Tile);
        Assert.Equal(77L, ringInstance.InstanceId);
        Assert.Equal<byte[]>([9], ringInstance.Payload);
    }

    [Fact]
    public void CollectGroundItemInstances_clears_the_callers_list_before_filling_it()
    {
        using var pair = new Pair(new TileCoord(10, 10, 0));
        pair.Frames(8);
        var stale = (NetId: 99L, Item: new TileGroundItem { ItemId = 1, Count = 1 },
            Instance: new TileGroundItemInstance { InstanceId = 1L, Payload = [] });
        var instanced = new List<(long NetId, TileGroundItem Item, TileGroundItemInstance Instance)> { stale, stale };

        // Nothing instanced on the ground yet: the stale entries go and nothing replaces them.
        pair.Server.SpawnGroundItem(new TileCoord(11, 10, 0), itemId: 3, count: 9, ttlTicks: 1000);
        pair.Frames(8);
        pair.Client.CollectGroundItemInstances(instanced);
        Assert.Empty(instanced);

        // A drop despawned since the last call is absent from the next, with no lifecycle held by the caller.
        long sword = pair.Server.SpawnGroundItem(new TileCoord(12, 9, 0), itemId: 7, count: 1, ttlTicks: 1000,
            instanceId: 42L, payload: [1]);
        pair.Frames(8);
        instanced.Add(stale);
        pair.Client.CollectGroundItemInstances(instanced);
        Assert.Equal(sword, Assert.Single(instanced).NetId);

        Assert.True(pair.Server.DespawnGroundItem(sword));
        pair.Frames(8);
        pair.Client.CollectGroundItemInstances(instanced);
        Assert.Empty(instanced);

        Assert.Throws<ArgumentNullException>(() => pair.Client.CollectGroundItemInstances(null!));
    }

    [Fact]
    public void The_instance_id_is_an_unsigned_varint_over_the_int64_bit_pattern()
    {
        // The package has no ContentVarint (it must not gain a dependency on KhaozEngine.Catalog), so the helper
        // behind this is private and these are the bytes that keep it honest against the one definition in
        // contracts 15. Unsigned LEB128 over the bit pattern, never zig-zagged, so -1 is the ten byte case.
        Assert.Equal<byte[]>([0x00, 0x00], PinnedBody(0L));
        Assert.Equal<byte[]>([0x7F, 0x00], PinnedBody(127L));
        Assert.Equal<byte[]>([0x80, 0x01, 0x00], PinnedBody(128L));
        Assert.Equal<byte[]>([0xFF, 0x7F, 0x00], PinnedBody(16_383L));
        Assert.Equal<byte[]>([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x00], PinnedBody(long.MaxValue));
        Assert.Equal<byte[]>([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x00], PinnedBody(-1L));

        // And the length prefix is the same varint, so a 200 byte payload declares itself in two bytes.
        byte[] body = PinnedBody(1L, new byte[200]);
        Assert.Equal(0x01, body[0]);
        Assert.Equal(0xC8, body[1]);
        Assert.Equal(0x01, body[2]);
        Assert.Equal(203, body.Length);
    }

    // The sibling component's framed payload as the writer emits it, for one entity carrying nothing else.
    static byte[] PinnedBody(long instanceId, byte[]? payload = null)
    {
        var world = new World();
        Entity e = world.Spawn();
        world.Set(e, new NetId(1));
        world.Set(e, new TileGroundItemInstance { InstanceId = instanceId, Payload = payload ?? [] });
        byte[] snap = SnapshotWriter.WriteFiltered(world, TileProtocol.CreateRegistry(),
            new HashSet<long> { 1 }, ReplicationChannels.Replicate, ownerNetId: 1);
        return FramedPayload(snap, TileProtocol.TileGroundItemInstanceTypeId);
    }

    // One component's framed payload out of a snapshot, empty when the id is not on it. Every id this registry
    // writes is an extension id, so every component here is length prefixed.
    static byte[] FramedPayload(byte[] snapshot, ushort typeId)
    {
        using var ms = new MemoryStream(snapshot);
        using var br = new BinaryReader(ms);
        int entities = br.ReadInt32();
        for (int i = 0; i < entities; i++)
        {
            br.ReadInt64();
            while (true)
            {
                ushort id = br.ReadUInt16();
                if (id == 0) break;
                byte[] data = br.ReadBytes(br.Read7BitEncodedInt());
                if (id == typeId) return data;
            }
        }

        return [];
    }

    // A hand-built component payload, so a test can say something an encoder of ours never would.
    static byte[] Body(long instanceId, int declaredLength, byte[] actual)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        Varint(bw, (ulong)instanceId);
        Varint(bw, (ulong)declaredLength);
        bw.Write(actual);
        bw.Flush();
        return ms.ToArray();
    }

    // One entity carrying one sibling component whose framed payload is exactly body: the snapshot layout is
    // [count][per entity: [net id][(type id, length, data)...][0]].
    static byte[] Snapshot(long netId, byte[] body)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(1);
        bw.Write(netId);
        bw.Write(TileProtocol.TileGroundItemInstanceTypeId);
        bw.Write7BitEncodedInt(body.Length);
        bw.Write(body);
        bw.Write((ushort)0);
        bw.Flush();
        return ms.ToArray();
    }

    static void Varint(BinaryWriter bw, ulong value)
    {
        while (value >= 0x80)
        {
            bw.Write((byte)(value | 0x80));
            value >>= 7;
        }

        bw.Write((byte)value);
    }

    // A client from before the sibling shipped: the two ids it knew, with the codecs it had. Written out here
    // rather than borrowed from TileProtocol on purpose, because what this pins is the wire an ALREADY SHIPPED
    // binary reads, which no later edit to the engine's own codecs can be allowed to move.
    static ReplicationRegistry OldClientRegistry()
    {
        var reg = new ReplicationRegistry();
        reg.Register<TileGroundItem>(TileProtocol.TileGroundItemTypeId,
            (v, w) => { w.Write(v.ItemId); w.Write(v.Count); w.Write(v.X); w.Write(v.Z); w.Write(v.Plane); },
            r => new TileGroundItem
            {
                ItemId = r.ReadInt32(),
                Count = r.ReadInt32(),
                X = r.ReadInt32(),
                Z = r.ReadInt32(),
                Plane = r.ReadInt32(),
            });
        reg.Register<TileObjectState>(TileProtocol.TileObjectStateTypeId,
            (v, w) => { w.Write(v.ObjectId); w.Write(v.State); w.Write(v.X); w.Write(v.Z); w.Write(v.Plane); },
            r => new TileObjectState
            {
                ObjectId = r.ReadInt64(),
                State = r.ReadInt32(),
                X = r.ReadInt32(),
                Z = r.ReadInt32(),
                Plane = r.ReadInt32(),
            });
        return reg;
    }
}
