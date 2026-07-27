using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Round-trips a 64-bit <see cref="NetId"/> through every wire path (full snapshot, AoI delta, persist blob, and the
/// per-client frame header) at boundary values: 0, <see cref="int.MaxValue"/>, a value past 2^32 (would have
/// overflowed the old 32-bit id), and one with the top node bits set (a negative signed long). A pre-10.0.0 build
/// truncated anything past 2^31; these confirm the widened path carries the full 64-bit value intact.
/// </summary>
public class NetIdWireBoundaryTests
{
    // 0; int.MaxValue; > 2^32; high-node-bits (node 0xABCD in the top 16 bits -> a negative signed long).
    public static IEnumerable<object[]> Boundaries => new[]
    {
        new object[] { 0L },
        new object[] { 1L },
        new object[] { (long)int.MaxValue },
        new object[] { 4_294_967_297L },                         // 2^32 + 1
        new object[] { unchecked((long)0xABCD_0000_1234_5678UL) } // high node bits set
    };

    private static readonly Vector3 Pos = new(1.5f, -2.5f, 3.5f);

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void FullSnapshot_round_trips_the_id(long value)
    {
        ReplicationRegistry reg = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(value));
        server.Set(e, ReplicatedPosition.FromWorld(Pos, WorldFrame.Origin));

        byte[] snap = SnapshotWriter.WriteFiltered(server, reg, new HashSet<long> { value },
            ReplicationChannels.Replicate, ownerNetId: value);

        var client = new World();
        var view = new ClientReplicationView(reg);
        view.Apply(client, snap);

        Assert.True(view.TryGetEntity(value, out Entity ce));
        Assert.True(client.TryGet(ce, out NetId nid) && nid.Value == value);
        Assert.True(client.TryGet(ce, out ReplicatedPosition p) && p.Value == Pos);
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void AoiDelta_round_trips_the_id(long value)
    {
        ReplicationRegistry reg = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(value));
        server.Set(e, ReplicatedPosition.FromWorld(Pos, WorldFrame.Origin));

        var repl = new AoiDeltaReplicator(reg);
        repl.BeginTick();
        byte[] delta = repl.WriteFor(slot: 0, server, new HashSet<long> { value }, ownerNetId: value);

        var client = new World();
        var view = new ClientReplicationView(reg);
        view.ApplyDelta(client, delta);

        Assert.True(view.TryGetEntity(value, out Entity ce));
        Assert.True(client.TryGet(ce, out NetId nid) && nid.Value == value);
        Assert.True(client.TryGet(ce, out ReplicatedPosition p) && p.Value == Pos);
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void PersistBlob_round_trips_the_id(long value)
    {
        var writer = new SnapshotBlobWriter();
        writer.AddEntity(value, new[] { new SnapshotBlobComponent(16, new byte[] { 1, 2, 3 }) });
        byte[] blob = writer.ToArray();

        var reader = new SnapshotBlobReader(blob);
        Assert.Single(reader.Entities);
        Assert.Equal(value, reader.Entities[0].NetId);
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.Entities[0].Components[0].Payload);
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void FrameHeader_round_trips_the_localNetId(long value)
    {
        byte[] frame = MoveProtocol.EncodeSnapshotFrame(localNetId: value, ackSeq: 7, snapshot: new byte[] { 9, 9 });
        Assert.True(MoveProtocol.TryDecodeSnapshotFrame(frame, out long localNetId, out int ackSeq, out byte[] body));
        Assert.Equal(value, localNetId);
        Assert.Equal(7, ackSeq);
        Assert.Equal(new byte[] { 9, 9 }, body);
    }

    // Applies a second full snapshot with the entity ABSENT: the full-state despawn loop must remove it. The 10.0.0
    // widening missed this loop (it iterated the gone List<long> as `int`), so an id past 2^31 truncated on the way
    // out - throwing KeyNotFoundException (killing the session) or despawning the wrong entity. These cover the
    // despawn path the original NetIdWireBoundaryTests never exercised (they only ever applied one snapshot).
    [Theory]
    [MemberData(nameof(Boundaries))]
    public void FullSnapshot_despawns_an_absent_id_without_truncation(long value)
    {
        ReplicationRegistry reg = MoveProtocol.CreateRegistry();
        var client = new World();
        var view = new ClientReplicationView(reg);

        // Snapshot 1: the entity is present.
        var s1 = new World();
        Entity e = s1.Spawn();
        s1.Set(e, new NetId(value));
        s1.Set(e, ReplicatedPosition.FromWorld(Pos, WorldFrame.Origin));
        byte[] snap1 = SnapshotWriter.WriteFiltered(s1, reg, new HashSet<long> { value },
            ReplicationChannels.Replicate, ownerNetId: value);
        view.Apply(client, snap1);
        Assert.True(view.TryGetEntity(value, out Entity ce));
        Assert.True(client.IsAlive(ce));

        // Snapshot 2: empty (the entity left). Full-state semantics: it must be despawned, not truncated.
        byte[] snap2 = SnapshotWriter.WriteFiltered(new World(), reg, new HashSet<long>(),
            ReplicationChannels.Replicate, ownerNetId: value);
        view.Apply(client, snap2);   // pre-fix: an id > 2^31 truncates here -> throw or wrong despawn

        Assert.False(view.TryGetEntity(value, out _));   // dropped from the live map
        Assert.False(client.IsAlive(ce));                // and actually despawned
    }

    // The collision the truncation could silently cause: a high-node id and a low id whose low 32 bits match. When the
    // high-node id leaves, the buggy (int) cast maps it onto the low id, despawning the WRONG (surviving) entity while
    // the departed one ghosts forever. ids 5 and Pack(1,5) both have low-32 == 5.
    [Fact]
    public void FullSnapshot_despawn_of_a_high_node_id_does_not_evict_the_low_id_it_truncates_onto()
    {
        ReplicationRegistry reg = MoveProtocol.CreateRegistry();
        var client = new World();
        var view = new ClientReplicationView(reg);

        long low = 5L;
        long high = NetIdAllocator.Pack(nodeId: 1, counter: 5);   // (1 << 48) | 5; (int)high == 5

        // Snapshot 1: both present.
        var s1 = new World();
        foreach (long id in new[] { low, high })
        {
            Entity en = s1.Spawn();
            s1.Set(en, new NetId(id));
            s1.Set(en, ReplicatedPosition.FromWorld(Pos, WorldFrame.Origin));
        }
        byte[] snap1 = SnapshotWriter.WriteFiltered(s1, reg, new HashSet<long> { low, high },
            ReplicationChannels.Replicate, ownerNetId: low);
        view.Apply(client, snap1);
        Assert.True(view.TryGetEntity(low, out Entity lowE));
        Assert.True(view.TryGetEntity(high, out _));

        // Snapshot 2: only `low` remains; the high-node id left.
        var s2 = new World();
        Entity keep = s2.Spawn();
        s2.Set(keep, new NetId(low));
        s2.Set(keep, ReplicatedPosition.FromWorld(Pos, WorldFrame.Origin));
        byte[] snap2 = SnapshotWriter.WriteFiltered(s2, reg, new HashSet<long> { low },
            ReplicationChannels.Replicate, ownerNetId: low);
        view.Apply(client, snap2);

        Assert.False(view.TryGetEntity(high, out _));            // the departed high-node id is gone
        Assert.True(view.TryGetEntity(low, out Entity lowStill)); // the low id it truncates onto SURVIVES
        Assert.True(client.IsAlive(lowStill));
        Assert.Equal(lowE, lowStill);                            // same entity, not resurrected
    }
}
